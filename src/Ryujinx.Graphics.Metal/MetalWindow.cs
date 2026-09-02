using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Metal.Interop;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    public class MetalWindow : IWindow
    {
        private readonly MetalRenderer _renderer;
        private readonly nint _device;
        private readonly nint _commandQueue;

        private AntiAliasing _antiAliasing;
        private ScalingFilter _scalingFilter;
        private float _scalingFilterLevel;
        private int _width = 1280;
        private int _height = 720;

        private nint _layer;
        private nint _pipelineState;
        private MetalFXUpscaler _upscaler;
        private int _lastInputWidth;
        private int _lastInputHeight;
        private int _readbackFrames;
        private bool _sawNonzeroFrame;

        private const string PresentationShader = @"
#include <metal_stdlib>
using namespace metal;

struct VertexOut {
    float4 pos [[position]];
    float2 uv;
};

vertex VertexOut v_main(uint vid [[vertex_id]]) {
    float2 pos = float2((vid << 1) & 2, vid & 2);
    VertexOut out;
    out.pos = float4(pos * float2(2, -2) + float2(-1, 1), 0, 1);
    out.uv = pos;
    return out;
}

fragment float4 f_main(VertexOut in [[stage_in]], texture2d<float> tex [[texture(0)]]) {
    constexpr sampler s(filter::linear);
    return tex.sample(s, in.uv);
}
";

        public MetalWindow(MetalRenderer renderer, nint device, nint commandQueue)
        {
            _renderer = renderer;
            _device = device;
            _commandQueue = commandQueue;

            CompilePresentationShader();
        }

        private void CompilePresentationShader()
        {
            nint sourceString = MetalBindings.CreateNSString(PresentationShader);
            nint library = MetalBindings.objc_msgSend(_device, MetalBindings.SelNewLibraryWithSourceOptionsError, sourceString, nint.Zero, nint.Zero);
            
            nint vName = MetalBindings.CreateNSString("v_main");
            nint fName = MetalBindings.CreateNSString("f_main");
            
            nint vFunc = MetalBindings.objc_msgSend(library, MetalBindings.SelNewFunctionWithName, vName);
            nint fFunc = MetalBindings.objc_msgSend(library, MetalBindings.SelNewFunctionWithName, fName);
            
            nint pipelineDescriptor = MetalBindings.objc_msgSend(MetalBindings.objc_getClass("MTLRenderPipelineDescriptor"), MetalBindings.SelNew);
            MetalBindings.objc_msgSend_void(pipelineDescriptor, MetalBindings.SelSetVertexFunction, vFunc);
            MetalBindings.objc_msgSend_void(pipelineDescriptor, MetalBindings.SelSetFragmentFunction, fFunc);
            
            nint colorAttachments = MetalBindings.objc_msgSend(pipelineDescriptor, MetalBindings.SelColorAttachments);
            nint colorAttachment0 = MetalBindings.objc_msgSend(colorAttachments, MetalBindings.SelObjectAtIndexedSubscript, (nuint)0);
            MetalBindings.objc_msgSend_void(colorAttachment0, MetalBindings.SelSetPixelFormat, (nuint)MetalBindings.MTLPixelFormatBGRA8Unorm);
            
            _pipelineState = MetalBindings.objc_msgSend(_device, MetalBindings.SelNewRenderPipelineStateWithDescriptorError, pipelineDescriptor, nint.Zero);
            
            if (_pipelineState == nint.Zero)
            {
                throw new Exception("CRITICAL ERROR: Failed to compile Metal PresentationShader!");
            }
            
            MetalBindings.Release(pipelineDescriptor);
            MetalBindings.Release(vFunc);
            MetalBindings.Release(fFunc);
            MetalBindings.Release(library);
            MetalBindings.Release(vName);
            MetalBindings.Release(fName);
            MetalBindings.Release(sourceString);
        }

        /// <summary>
        /// Attaches a CAMetalLayer (from the SDL3 Metal view) to this window.
        /// Ownership is taken via retain/release.
        /// </summary>
        public void SetLayer(nint layer)
        {
            if (_layer != nint.Zero)
            {
                MetalBindings.Release(_layer);
            }

            _layer = layer == nint.Zero ? nint.Zero : MetalBindings.Retain(layer);

            if (_layer != nint.Zero)
            {
                MetalBindings.objc_msgSend_void(_layer, MetalBindings.SelSetDevice, _device);
                MetalBindings.objc_msgSend_void(_layer, MetalBindings.SelSetPixelFormat, (nuint)MetalBindings.MTLPixelFormatBGRA8Unorm);
                MetalBindings.objc_msgSend_void(_layer, MetalBindings.SelSetFramebufferOnly, false);
                MetalBindings.objc_msgSend_void(_layer, MetalBindings.SelSetDrawableSize, (double)_width, (double)_height);
            }
        }

        public void Present(ITexture texture, ImageCrop crop, Action swapBuffersCallback)
        {
            try
            {
                if (_layer == nint.Zero)
                {
                    swapBuffersCallback();
                    return;
                }

                // M4: commit any pending pipeline render pass before the blit so the
                // blit reads the freshly rendered framebuffer (ordered command buffers).
                _renderer?.FlushBeforePresent();
                nint drawable = MetalBindings.objc_msgSend(_layer, MetalBindings.SelNextDrawable);

                if (drawable == nint.Zero)
                {
                    Logger.Error?.Print(LogClass.Gpu, "CRITICAL: nextDrawable returned nil! Layer might be 0x0 or detached.");
                    swapBuffersCallback();
                    return;
                }

                nint drawableTexture = MetalBindings.objc_msgSend(drawable, MetalBindings.SelTexture);

                if (drawableTexture == nint.Zero)
                {
                    Logger.Error?.Print(LogClass.Gpu, "CRITICAL: drawableTexture returned nil!");
                    swapBuffersCallback();
                    return;
                }

                nint commandBuffer = MetalBindings.objc_msgSend(_commandQueue, MetalBindings.SelCommandBufferWithUnretainedReferences);

                if (texture is Ryujinx.Graphics.GAL.Multithreading.Resources.ThreadedTexture tTex)
                {
                    texture = tTex.Base;
                }

                if (texture is MetalTexture metalTexture && metalTexture.TextureHandle != nint.Zero)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"[PRESENT] metalTexture handle=0x{metalTexture.TextureHandle:X} fmt={metalTexture.Format} w={metalTexture.Width} h={metalTexture.Height}");
                    // Fallback to standard render encoder
                    nint passDescriptor = MetalBindings.objc_msgSend(
                        MetalBindings.objc_getClass("MTLRenderPassDescriptor"),
                        MetalBindings.SelRenderPassDescriptor);

                    if (passDescriptor != nint.Zero)
                    {
                        nint colorAttachments = MetalBindings.objc_msgSend(passDescriptor, MetalBindings.SelColorAttachments);
                        nint colorAttachment = MetalBindings.objc_msgSend(colorAttachments, MetalBindings.SelObjectAtIndexedSubscript, (nuint)0);

                        if (colorAttachment != nint.Zero)
                        {
                            MetalBindings.objc_msgSend_void(colorAttachment, MetalBindings.SelSetTexture, drawableTexture);
                            MetalBindings.objc_msgSend_void(colorAttachment, MetalBindings.SelSetLoadAction, (nuint)2); // MTLLoadActionClear
                            // Red clear color for diagnostic
                            unsafe
                            {
                                MTLColor red = new(1.0, 0.0, 0.0, 1.0);
                                MetalBindings.objc_msgSend_void(colorAttachment, MetalBindings.SelSetClearColor, &red);
                            }
                            MetalBindings.objc_msgSend_void(colorAttachment, MetalBindings.SelSetStoreAction, (nuint)MetalBindings.MTLStoreActionStore);
                        }

                        nint encoder = MetalBindings.objc_msgSend(commandBuffer, MetalBindings.SelRenderCommandEncoderWithDescriptor, passDescriptor);

                        if (encoder != nint.Zero)
                        {
                            if (_pipelineState != nint.Zero)
                            {
                                MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelSetRenderPipelineState, _pipelineState);
                                MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelSetFragmentTextureAtIndex, metalTexture.TextureHandle, (nuint)0);
                                MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelDrawPrimitivesVertexStartVertexCount, (nuint)MetalBindings.MTLPrimitiveTypeTriangle, (nuint)0, (nuint)3);
                            }
                            else
                            {
                                Logger.Error?.Print(LogClass.Gpu, "CRITICAL: _pipelineState is zero during encode!");
                            }

                            MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelEndEncoding);
                        }
                        else
                        {
                            Logger.Error?.Print(LogClass.Gpu, "CRITICAL: RenderCommandEncoder creation failed!");
                        }
                    }
                    else
                    {
                        Logger.Error?.Print(LogClass.Gpu, "CRITICAL: RenderPassDescriptor creation failed!");
                    }

                    LogPresentTextureReadback(metalTexture);
                }
                MetalBindings.objc_msgSend_void(commandBuffer, MetalBindings.SelPresentDrawable, drawable);
                MetalBindings.objc_msgSend_void(commandBuffer, MetalBindings.SelCommit);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"MetalWindow.Present failed: {ex.Message}");
            }

            swapBuffersCallback();
        }

        public void SetSize(int width, int height)
        {
            _width = width;
            _height = height;

            if (_layer != nint.Zero)
            {
                MetalBindings.objc_msgSend_void(_layer, MetalBindings.SelSetDrawableSize, (double)_width, (double)_height);
            }
        }

        /// <summary>
        /// Reads back the presented framebuffer over time and logs non-trivial texel
        /// statistics (mean, min/max, nonzero count) plus corner/center texels until a
        /// real (bright) frame is seen. This is the verification/debugging signal for
        /// the magenta fix: a live image shows a nonzero mean and varied texels, while
        /// an empty (all-zero) framebuffer stays 0. Gated by a generous cap. The
        /// "nonzero-first" latch stops logging once a definitely-live frame appears.
        /// </summary>
        private void LogPresentTextureReadback(MetalTexture texture)
        {
            if (_sawNonzeroFrame || _readbackFrames >= 240 || texture == null || texture.TextureHandle == nint.Zero)
            {
                return;
            }

            _readbackFrames++;

            using (PinnedSpan<byte> data = texture.GetData())
            {
                ReadOnlySpan<byte> span = data.Get();
                int w = texture.Width;
                int h = texture.Height;

                if (span.Length < w * h || w <= 0 || h <= 0)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"[READBACK] frame {_readbackFrames}: no data ({span.Length} bytes for {w}x{h})");
                    return;
                }

                int bytesPerPixel = span.Length / (w * h);
                int channels = Math.Min(4, bytesPerPixel);
                int stepX = Math.Max(1, w / 32);
                int stepY = Math.Max(1, h / 32);
                int gridCount = ((w + stepX - 1) / stepX) * ((h + stepY - 1) / stepY);

                long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                int nonzero = 0;
                int minR = 255, maxR = 0;

                for (int y = 0; y < h; y += stepY)
                {
                    for (int x = 0; x < w; x += stepX)
                    {
                        int offset = (y * w + x) * bytesPerPixel;
                        int r = channels > 0 ? span[offset] : 0;
                        int g = channels > 1 ? span[offset + 1] : 0;
                        int b = channels > 2 ? span[offset + 2] : 0;
                        int a = channels > 3 ? span[offset + 3] : 255;

                        sumR += r;
                        sumG += g;
                        sumB += b;
                        sumA += a;
                        minR = Math.Min(minR, r);
                        maxR = Math.Max(maxR, r);

                        if (r != 0 || g != 0)
                        {
                            nonzero++;
                        }
                    }
                }

                if (nonzero > gridCount / 4 && maxR >= 24)
                {
                    _sawNonzeroFrame = true;
                }

                string hex(int v) => v.ToString("0x02");
                int px(int x, int y) => (y * w + x) * bytesPerPixel;
                int cx = w / 2, cy = h / 2;
                var corners = $"tl={hex(span[px(0, 0)])}{hex(span[px(0, 0)] + 1)}{hex(span[px(0, 0)] + 2)} " +
                              $"tr={hex(span[px(w - 1, 0)])}{hex(span[px(w - 1, 0)] + 1)}{hex(span[px(w - 1, 0)] + 2)} " +
                              $"bl={hex(span[px(0, h - 1)])}{hex(span[px(0, h - 1)] + 1)}{hex(span[px(0, h - 1)] + 2)} " +
                              $"br={hex(span[px(w - 1, h - 1)])}{hex(span[px(w - 1, h - 1)] + 1)}{hex(span[px(w - 1, h - 1)] + 2)} " +
                              $"center={hex(span[px(cx, cy)])}{hex(span[px(cx, cy)] + 1)}{hex(span[px(cx, cy)] + 2)}";

                Logger.Warning?.Print(LogClass.Gpu,
                    $"[READBACK] frame {_readbackFrames}: mean=({sumR / gridCount},{sumG / gridCount},{sumB / gridCount},{sumA / gridCount}) " +
                    $"minR={minR} maxR={maxR} nonzeroGrid={nonzero}/{gridCount} {corners} sawNonzero={_sawNonzeroFrame}");
            }
        }

        public void ChangeVSyncMode(VSyncMode vSyncMode) { }

        public void SetAntiAliasing(AntiAliasing antialiasing)
        {
            _antiAliasing = antialiasing;
        }

        public void SetScalingFilter(ScalingFilter type)
        {
            _scalingFilter = type;
        }

        public void SetScalingFilterLevel(float level)
        {
            _scalingFilterLevel = level;
        }

        public void SetColorSpacePassthrough(bool colorSpacePassThroughEnabled) { }

        public void SetOsdText(string text, bool visible) { }

        public void Dispose()
        {
            if (_pipelineState != nint.Zero)
            {
                MetalBindings.Release(_pipelineState);
                _pipelineState = nint.Zero;
            }

            if (_layer != nint.Zero)
            {
                MetalBindings.Release(_layer);
                _layer = nint.Zero;
            }

            if (_upscaler != null)
            {
                _upscaler.Dispose();
                _upscaler = null;
            }

            GC.SuppressFinalize(this);
        }
    }
}
