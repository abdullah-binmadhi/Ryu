using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Metal.Interop;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// M4b: GPU-side pixel-format-converting copy for the native Metal backend.
    ///
    /// Metal's <c>MTLBlitCommandEncoder copyFromTexture:...</c> requires the source and
    /// destination textures to share the <b>same pixel format</b>; it cannot convert.
    /// Copies that cross formats (e.g. RG11B10Float → RGBA8Unorm, the Nier composite →
    /// present-surface blit) silently become a no-op, leaving presentation surfaces
    /// all-zero — the observed full-screen magenta. This pass performs the conversion
    /// with a minimal fullscreen-triangle render whose fragment shader samples the
    /// source texture and writes the destination color attachment.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal sealed unsafe class MetalFormatBlit : IDisposable
    {
        // MTLLoadActionDontCare = 0, MTLStoreActionStore = 1.
        private const ulong LoadActionDontCare = 0;
        private const ulong StoreActionStore = 1;

        private const string BlitShaderSource = @"
#include <metal_stdlib>
using namespace metal;

#define BLIT_PARAMS_INDEX 0
#define BLIT_TEXTURE_INDEX 0

struct BlitVertexOut {
    float4 pos [[position]];
    float2 uv;
};

struct BlitParams {
    float4 srcUvRect; // x,y = src region origin (normalized), z,w = src region size (normalized)
    uint filterMode;  // 0 = nearest, 1 = linear
    uint pad0;
    uint pad1;
    uint pad2;
};

vertex BlitVertexOut blit_v(uint vid [[vertex_id]], const device BlitParams & blit [[buffer(BLIT_PARAMS_INDEX)]]) {
    BlitVertexOut out;
    float2 pos = float2((vid << 1) & 2, vid & 2);
    out.pos = float4(pos * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    out.uv = blit.srcUvRect.xy + pos * blit.srcUvRect.zw;
    return out;
}

fragment float4 blit_f(BlitVertexOut in [[stage_in]],
                       texture2d<float> src [[texture(BLIT_TEXTURE_INDEX)]],
                       const device BlitParams & blit [[buffer(BLIT_PARAMS_INDEX)]]) {
    constexpr sampler s_near(filter::nearest, address::clamp_to_edge);
    constexpr sampler s_lin(filter::linear, address::clamp_to_edge);
    return blit.filterMode != 0 ? src.sample(s_lin, in.uv) : src.sample(s_near, in.uv);
}
";

        private readonly nint _device;
        private readonly nint _queue;
        private readonly nint _library;
        private readonly nint _vertexFunction;
        private readonly nint _fragmentFunction;
        private readonly Dictionary<ulong, nint> _pipelineCache = new();
        private bool _disposed;
        private bool _probed;

        public MetalFormatBlit(nint device, nint queue)
        {
            _device = device;
            _queue = queue;

            nint sourceString = MetalBindings.CreateNSString(BlitShaderSource);
            nint nsError = nint.Zero;

            try
            {
                _library = MetalBindings.objc_msgSend(
                    _device,
                    MetalBindings.SelNewLibraryWithSourceOptionsError,
                    sourceString,
                    nint.Zero,
                    (nint)(&nsError));

                if (_library == nint.Zero)
                {
                    throw new Exception($"MetalFormatBlit: MSL compile failed: {MetalBindings.GetErrorDescription(nsError)}");
                }

                _vertexFunction = MetalBindings.objc_msgSend(
                    _library,
                    MetalBindings.SelNewFunctionWithName,
                    MetalBindings.CreateNSString("blit_v"));

                _fragmentFunction = MetalBindings.objc_msgSend(
                    _library,
                    MetalBindings.SelNewFunctionWithName,
                    MetalBindings.CreateNSString("blit_f"));

                if (_vertexFunction == nint.Zero || _fragmentFunction == nint.Zero)
                {
                    throw new Exception("MetalFormatBlit: newFunctionWithName returned nil for blit_v/blit_f");
                }
            }
            finally
            {
                MetalBindings.Release(sourceString);
            }
        }

        /// <summary>
        /// Copies a rect (srcX, srcY, width, height) of <paramref name="src"/> into
        /// (dstX, dstY, width, height) of <paramref name="dst"/>, converting pixel
        /// format from src's to dst's format via a fullscreen-triangle fragment pass.
        /// The command buffer is ordered on the shared queue (render → this blit →
        /// later presents) exactly like the fast blit path.
        /// </summary>
        public void Copy(MetalTexture src, MetalTexture dst, int srcX, int srcY, int dstX, int dstY, int width, int height)
        {
            Copy(
                src,
                dst,
                new Extents2DF(srcX, srcY, srcX + width, srcY + height),
                new Extents2DF(dstX, dstY, dstX + width, dstY + height),
                linearFilter: false);
        }

        public void Copy(MetalTexture src, MetalTexture dst, Extents2D srcRegion, Extents2D dstRegion, bool linearFilter = false)
        {
            Copy(
                src,
                dst,
                new Extents2DF(srcRegion.X1, srcRegion.Y1, srcRegion.X2, srcRegion.Y2),
                new Extents2DF(dstRegion.X1, dstRegion.Y1, dstRegion.X2, dstRegion.Y2),
                linearFilter);
        }

        public void Copy(MetalTexture src, MetalTexture dst, Extents2DF srcRegion, Extents2DF dstRegion, bool linearFilter = false)
        {
            nint srcHandle = src.TextureHandle;
            nint dstHandle = dst.TextureHandle;

            if (srcHandle == nint.Zero || dstHandle == nint.Zero)
            {
                return;
            }

            ulong dstPixelFormat = MetalFormats.ToMtlPixelFormat(dst.Format);

            if (dstPixelFormat == 0)
            {
                Logger.Error?.Print(LogClass.Gpu, $"MetalFormatBlit: destination format {dst.Format} unsupported");
                return;
            }

            nint pipeline = GetOrCreatePipeline(dstPixelFormat);

            if (pipeline == nint.Zero)
            {
                Logger.Error?.Print(LogClass.Gpu, $"MetalFormatBlit: failed to create format-converting pipeline for {dst.Format}");
                return;
            }

            nint commandBuffer = MetalBindings.Retain(MetalBindings.objc_msgSend(_queue, MetalBindings.SelCommandBuffer));

            if (commandBuffer == nint.Zero)
            {
                return;
            }

            if (src.Renderer != null && src.Renderer.M4Queue.CompletionEvent != nint.Zero && src.Renderer.M4Queue.LastSignaledValue > 0)
            {
                MetalBindings.objc_msgSend_void(commandBuffer, MetalBindings.SelEncodeWaitForEventValue, src.Renderer.M4Queue.CompletionEvent, src.Renderer.M4Queue.LastSignaledValue);
            }

            try
            {
                nint passDescriptor = MetalBindings.objc_msgSend(
                    MetalBindings.objc_getClass("MTLRenderPassDescriptor"),
                    MetalBindings.SelRenderPassDescriptor);

                nint colorAttachments = passDescriptor != nint.Zero
                    ? MetalBindings.objc_msgSend(passDescriptor, MetalBindings.SelColorAttachments)
                    : nint.Zero;

                nint colorAttachment = colorAttachments != nint.Zero
                    ? MetalBindings.objc_msgSend(colorAttachments, MetalBindings.SelObjectAtIndexedSubscript, (nuint)0)
                    : nint.Zero;

                if (colorAttachment == nint.Zero)
                {
                    Logger.Error?.Print(LogClass.Gpu, "MetalFormatBlit: color attachment 0 unavailable");
                    return;
                }

                MetalBindings.objc_msgSend_void(colorAttachment, MetalBindings.SelSetTexture, dstHandle);
                MetalBindings.objc_msgSend_void(colorAttachment, MetalBindings.SelSetLoadAction, (nuint)LoadActionDontCare);
                MetalBindings.objc_msgSend_void(colorAttachment, MetalBindings.SelSetStoreAction, (nuint)StoreActionStore);

                nint encoder = MetalBindings.objc_msgSend(commandBuffer, MetalBindings.SelRenderCommandEncoderWithDescriptor, passDescriptor);

                if (encoder == nint.Zero)
                {
                    Logger.Error?.Print(LogClass.Gpu, "MetalFormatBlit: renderCommandEncoderWithDescriptor failed");
                    return;
                }

                MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelSetRenderPipelineState, pipeline);

                float dstX = MathF.Min(dstRegion.X1, dstRegion.X2);
                float dstY = MathF.Min(dstRegion.Y1, dstRegion.Y2);
                float dstW = MathF.Abs(dstRegion.X2 - dstRegion.X1);
                float dstH = MathF.Abs(dstRegion.Y2 - dstRegion.Y1);

                unsafe
                {
                    MTLViewport viewport = new(dstX, dstY, dstW, dstH, 0.0, 1.0);
                    MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelSetViewport, &viewport);
                }

                float uvX1 = (float)srcRegion.X1 / Math.Max(1, src.Width);
                float uvX2 = (float)srcRegion.X2 / Math.Max(1, src.Width);
                float uvY1 = (float)srcRegion.Y1 / Math.Max(1, src.Height);
                float uvY2 = (float)srcRegion.Y2 / Math.Max(1, src.Height);

                if (dstRegion.X1 > dstRegion.X2)
                {
                    (uvX1, uvX2) = (uvX2, uvX1);
                }
                if (dstRegion.Y1 > dstRegion.Y2)
                {
                    (uvY1, uvY2) = (uvY2, uvY1);
                }

                float uvX = uvX1;
                float uvY = uvY1;
                float uvW = uvX2 - uvX1;
                float uvH = uvY2 - uvY1;

                unsafe
                {
                    float[] blitParams = { uvX, uvY, uvW, uvH, linearFilter ? 1f : 0f, 0f, 0f, 0f };
                    fixed (float* p = blitParams)
                    {
                        MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelSetVertexBytesLengthAtIndex, p, (nuint)(sizeof(float) * 8), (nuint)0);
                        MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelSetFragmentBytesLengthAtIndex, p, (nuint)(sizeof(float) * 8), (nuint)0);
                    }
                }

                MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelSetFragmentTextureAtIndex, srcHandle, (nuint)0);

                MetalBindings.objc_msgSend_void(
                    encoder,
                    MetalBindings.SelDrawPrimitivesVertexStartVertexCount,
                    (nuint)MetalBindings.MTLPrimitiveTypeTriangle,
                    (nuint)0,
                    (nuint)3);

                MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelEndEncoding);
                MetalBindings.objc_msgSend_void(commandBuffer, MetalBindings.SelCommit);

                if (!_probed)
                {
                    ProbeCopyResult(commandBuffer, dst, (int)dstX, (int)dstY, (int)dstW, (int)dstH);
                }
            }
            finally
            {
                MetalBindings.Release(commandBuffer);
            }
        }

        /// <summary>
        /// Debug helper: for the first conversion copy only, waits for the command
        /// buffer to complete and reads the destination back to confirm the fragment
        /// pass actually wrote non-(near-)zero data.
        /// </summary>
        private void ProbeCopyResult(nint commandBuffer, MetalTexture dst, int dstX, int dstY, int width, int height)
        {
            _probed = true;
            MetalBindings.objc_msgSend_void(commandBuffer, MetalBindings.SelWaitUntilCompleted);

            using (PinnedSpan<byte> data = dst.GetData())
            {
                ReadOnlySpan<byte> span = data.Get();
                int w = dst.Width;
                int h = dst.Height;

                if (span.Length == 0 || w <= 0 || h <= 0)
                {
                    Logger.Error?.Print(LogClass.Gpu, $"[BLIT_PROBE] no data after wait ({span.Length} bytes for {w}x{h})");
                    return;
                }

                int bytesPerPixel = span.Length / (w * h);
                string hex(int v) => v.ToString("0x02");
                int px(int x, int y) => (y * w + x) * bytesPerPixel;
                int cx = w / 2, cy = h / 2;
                int pSrc = px(dstX, dstY), pCenter = px(cx, cy);
                var sample = $"{hex(span[pSrc])}{hex(span[pSrc] + 1)}{hex(span[pSrc] + 2)} " +
                             $"{hex(span[pCenter])}{hex(span[pCenter] + 1)}{hex(span[pCenter] + 2)} " +
                             $"{hex(span[px(dstX + width - 1, dstY + height - 1)])}{hex(span[px(dstX + width - 1, dstY + height - 1)] + 1)}{hex(span[px(dstX + width - 1, dstY + height - 1)] + 2)}";

                long sum = 0;
                int nonzero = 0;
                for (int y = dstY; y < dstY + height; y += Math.Max(1, height / 8))
                {
                    for (int x = dstX; x < dstX + width; x += Math.Max(1, width / 8))
                    {
                        int r = span[px(x, y)];
                        sum += r;
                        if (r != 0)
                        {
                            nonzero++;
                        }
                    }
                }

                Logger.Error?.Print(LogClass.Gpu,
                    $"[BLIT_PROBE] wrote dst rect ({dstX},{dstY})+{width}x{height}: tl/center/br={sample} meanR={(sum / 64).ToString()} nonzeroR={nonzero}/64");
            }
        }

        private nint GetOrCreatePipeline(ulong dstPixelFormat)
        {
            if (_pipelineCache.TryGetValue(dstPixelFormat, out nint cached) && cached != nint.Zero)
            {
                return cached;
            }

            nint descriptor = MetalBindings.objc_msgSend(
                MetalBindings.objc_getClass("MTLRenderPipelineDescriptor"),
                MetalBindings.SelNew);

            if (descriptor == nint.Zero)
            {
                return nint.Zero;
            }

            nint nsError = nint.Zero;
            nint pipeline = nint.Zero;

            try
            {
                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetVertexFunction, _vertexFunction);
                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetFragmentFunction, _fragmentFunction);

                nint colorAttachments = MetalBindings.objc_msgSend(descriptor, MetalBindings.SelColorAttachments);
                nint colorAttachment = colorAttachments != nint.Zero
                    ? MetalBindings.objc_msgSend(colorAttachments, MetalBindings.SelObjectAtIndexedSubscript, (nuint)0)
                    : nint.Zero;

                if (colorAttachment != nint.Zero)
                {
                    MetalBindings.objc_msgSend_void(colorAttachment, MetalBindings.SelSetPixelFormat, (nuint)dstPixelFormat);
                }

                unsafe
                {
                    pipeline = MetalBindings.objc_msgSend(
                        _device,
                        MetalBindings.SelNewRenderPipelineStateWithDescriptorError,
                        descriptor,
                        (nint)(&nsError));
                }

                if (pipeline != nint.Zero)
                {
                    _pipelineCache[dstPixelFormat] = pipeline;
                }
                else
                {
                    Logger.Error?.Print(LogClass.Gpu, $"MetalFormatBlit: pipeline-state creation failed: {MetalBindings.GetErrorDescription(nsError)}");
                }
            }
            finally
            {
                MetalBindings.Release(descriptor);
            }

            return pipeline;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_vertexFunction != nint.Zero)
            {
                MetalBindings.Release(_vertexFunction);
            }

            if (_fragmentFunction != nint.Zero)
            {
                MetalBindings.Release(_fragmentFunction);
            }

            if (_library != nint.Zero)
            {
                MetalBindings.Release(_library);
            }

            foreach ((_, nint pipeline) in _pipelineCache)
            {
                MetalBindings.Release(pipeline);
            }

            _pipelineCache.Clear();
            GC.SuppressFinalize(this);
        }
    }
}