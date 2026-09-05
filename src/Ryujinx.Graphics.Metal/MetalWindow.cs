using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Metal.Interop;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;

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
        private int _presentCount;
        private bool _sawNonzeroFrame;
        private bool _captureStarted;
        private int _captureFrames;
        private int _logPresentCount;

        private static readonly bool s_captureEnabled =
            Environment.GetEnvironmentVariable("RYU_METAL_CAPTURE") == "1" ||
            Environment.GetEnvironmentVariable("RYU_METAL_CAPTURE_DIR") != null;

        private static readonly bool s_preferLastDrawn =
            Environment.GetEnvironmentVariable("RYU_METAL_PRESENT_LAST_DRAWN") == "1";

        private static readonly string CaptureDirectory = InitializeCaptureDirectory();

        private static string InitializeCaptureDirectory()
        {
            string dir = Environment.GetEnvironmentVariable("RYU_METAL_CAPTURE_DIR") ??
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "captures");
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch { }
            return dir;
        }

        private struct ReadbackRequest
        {
            public nint TextureHandle;
            public int Width;
            public int Height;
            public Format Format;
            public ulong TargetFenceValue;
            public int FrameIndex;
            public string Tag;
        }

        private readonly BlockingCollection<ReadbackRequest> _readbackQueue = new(new ConcurrentQueue<ReadbackRequest>(), 8);
        private Thread _readbackThread;
        private volatile bool _readbackRunning;

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
            StartReadbackWorker();
        }

        private void StartReadbackWorker()
        {
            _readbackRunning = true;
            _readbackThread = new Thread(ReadbackWorkerLoop)
            {
                IsBackground = true,
                Name = "Metal.HeadlessReadbackWorker"
            };
            _readbackThread.Start();
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

                // Start an opt-in Xcode GPU capture before flushing the game pass. This
                // captures the real game render path, not only the synthetic diagnostics.
                if (!_captureStarted && Environment.GetEnvironmentVariable("RYU_METAL_GPU_CAPTURE") == "1")
                {
                    nint captureManager = MetalBindings.objc_msgSend(
                        MetalBindings.objc_getClass("MTLCaptureManager"),
                        MetalBindings.SelSharedCaptureManager);
                    if (captureManager != nint.Zero)
                    {
                        MetalBindings.objc_msgSend_void(captureManager, MetalBindings.SelStartCaptureWithDevice, _device);
                        _captureStarted = true;
                        Logger.Warning?.Print(LogClass.Gpu, "[GPU_CAPTURE] started MTLCaptureManager capture for one presented frame");
                    }
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

                if (_renderer != null && _renderer.M4Queue.CompletionEvent != nint.Zero && _renderer.M4Queue.LastSignaledValue > 0)
                {
                    MetalBindings.objc_msgSend_void(commandBuffer, MetalBindings.SelEncodeWaitForEventValue, _renderer.M4Queue.CompletionEvent, _renderer.M4Queue.LastSignaledValue);
                }

                if (texture is MetalTexture metalTexture && metalTexture.TextureHandle != nint.Zero)
                {
                    MetalTexture sourceToPresent = metalTexture;
                    MetalTexture lastDrawn = _renderer?.LastDrawnTarget;
                    bool preferLastDrawn = s_preferLastDrawn;

                    if (preferLastDrawn && lastDrawn != null && lastDrawn.TextureHandle != nint.Zero &&
                        lastDrawn.Width == metalTexture.Width && lastDrawn.Height == metalTexture.Height)
                    {
                        sourceToPresent = lastDrawn;
                        Logger.Warning?.Print(LogClass.Gpu, $"[TARGET_DIAG] presenting LastDrawn 0x{lastDrawn.TextureHandle:X} instead of submitted 0x{metalTexture.TextureHandle:X}");
                    }

                    if (lastDrawn != null && lastDrawn.TextureHandle != nint.Zero && lastDrawn != metalTexture)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, $"[TARGET_DIAG] Swapchain 0x{sourceToPresent.TextureHandle:X} ({sourceToPresent.Format}) != LastDrawn 0x{lastDrawn.TextureHandle:X} ({lastDrawn.Format}) draws={(_renderer.Pipeline as MetalPipeline)?.LastDrawnTargetDrawCount}");
                    }

                    if (_logPresentCount++ < 10)
                    {
                        Logger.Warning?.Print(LogClass.Gpu, $"[PRESENT] metalTexture handle=0x{metalTexture.TextureHandle:X} fmt={metalTexture.Format} w={metalTexture.Width} h={metalTexture.Height}");
                    }
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
                            // Clear only as a fallback for uncovered pixels. The presentation
                            // shader writes the drawable's visible color; diagnostic clears must
                            // not tint or replace the submitted framebuffer.
                            MetalBindings.objc_msgSend_void(colorAttachment, MetalBindings.SelSetLoadAction, (nuint)2); // MTLLoadActionClear
                            unsafe
                            {
                                MTLColor clear = new(0.0, 0.0, 0.0, 1.0);
                                MetalBindings.objc_msgSend_void(colorAttachment, MetalBindings.SelSetClearColor, &clear);
                            }
                            MetalBindings.objc_msgSend_void(colorAttachment, MetalBindings.SelSetStoreAction, (nuint)MetalBindings.MTLStoreActionStore);
                        }

                        nint encoder = MetalBindings.objc_msgSend(commandBuffer, MetalBindings.SelRenderCommandEncoderWithDescriptor, passDescriptor);

                        if (encoder != nint.Zero)
                        {
                            if (_pipelineState != nint.Zero)
                            {
                                MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelSetRenderPipelineState, _pipelineState);
                                MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelSetCullMode, (nuint)MetalBindings.MTLCullModeNone);
                                MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelSetFragmentTextureAtIndex, sourceToPresent.TextureHandle, (nuint)0);
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

                    QueuePresentReadback(sourceToPresent, _renderer?.M4Queue?.LastSignaledValue ?? 0, preferLastDrawn ? "LAST_DRAWN_PRESENT" : "SWAPCHAIN");
                }
                MetalBindings.objc_msgSend_void(commandBuffer, MetalBindings.SelPresentDrawable, drawable);
                MetalBindings.objc_msgSend_void(commandBuffer, MetalBindings.SelCommit);

                if (_captureStarted && ++_captureFrames >= 1)
                {
                    nint captureManager = MetalBindings.objc_msgSend(
                        MetalBindings.objc_getClass("MTLCaptureManager"),
                        MetalBindings.SelSharedCaptureManager);
                    if (captureManager != nint.Zero)
                    {
                        MetalBindings.objc_msgSend_void(captureManager, MetalBindings.SelStopCapture);
                        Logger.Warning?.Print(LogClass.Gpu, "[GPU_CAPTURE] stopped after one presented frame; inspect the active Xcode GPU capture");
                    }
                }
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

        private void QueuePresentReadback(MetalTexture texture, ulong targetFenceValue, string tag = "SWAPCHAIN")
        {
            if (!s_captureEnabled || texture == null || texture.TextureHandle == nint.Zero)
            {
                return;
            }

            _presentCount++;

            // Sample early frames (1..5) then every 30 frames (~1s at 30fps)
            bool shouldSample = _presentCount <= 5 || (_presentCount % 30 == 0);
            if (!shouldSample || _readbackQueue.Count >= 2)
            {
                return;
            }

            _readbackFrames++;

            ulong eventNow = (_renderer != null && _renderer.M4Queue.CompletionEvent != nint.Zero) ? _renderer.M4Queue.SignaledValue : 0;
            Logger.Warning?.Print(LogClass.Gpu, $"[FENCE:ENQ] {tag} frame={_presentCount} target={targetFenceValue} eventNow={eventNow} diff={targetFenceValue - Math.Min(targetFenceValue, eventNow)}");

            _readbackQueue.TryAdd(new ReadbackRequest
            {
                TextureHandle = texture.TextureHandle,
                Width = texture.Width,
                Height = texture.Height,
                Format = texture.Format,
                TargetFenceValue = targetFenceValue,
                FrameIndex = _presentCount,
                Tag = tag
            });
        }

        private unsafe void ReadbackWorkerLoop()
        {
            const nuint PageAlignment = 16384; // 16KB Apple Silicon system page size
            byte* alignedBuffer = null;
            nuint bufferCapacity = 0;

            try
            {
                while (_readbackRunning)
                {
                    if (!_readbackQueue.TryTake(out ReadbackRequest req, 100))
                    {
                        continue;
                    }

                    if (req.TextureHandle == nint.Zero)
                    {
                        continue;
                    }

                    // 1. Synchronization via MTLSharedEvent: never read the texture before
                    //    the GPU has reached the batch that rendered it. Previous behavior
                    //    waited ≤100 ms and proceeded on timeout, so headless readbacks raced
                    //    ahead of the (heavily oversubscribed) GPU and always returned black.
                    if (_renderer != null && _renderer.M4Queue.CompletionEvent != nint.Zero && req.TargetFenceValue > 0)
                    {
                        ulong before = _renderer.M4Queue.SignaledValue;
                        int waitIters = 0;

                        for (int attempt = 0; attempt < 60; attempt++)
                        {
                            ulong now = _renderer.M4Queue.SignaledValue;

                            if (now >= req.TargetFenceValue)
                            {
                                break;
                            }

                            waitIters++;
                            Metal4Bindings.m4_wait_event_bool(
                                _renderer.M4Queue.CompletionEvent,
                                Metal4Bindings.SelWaitUntilSignaledValueTimeoutMS,
                                req.TargetFenceValue,
                                250);
                        }

                        Logger.Warning?.Print(LogClass.Gpu, $"[FENCE:WAIT] {req.Tag} frame={req.FrameIndex} target={req.TargetFenceValue} before={before} after={_renderer.M4Queue.SignaledValue} iters={waitIters}");
                    }

                    int w = req.Width;
                    int h = req.Height;
                    int bytesPerRow = w * 4; // 4 bytes per pixel for R8G8B8A8 and R11G11B10Float
                    nuint totalSize = (nuint)(bytesPerRow * h);

                    if (alignedBuffer == null || bufferCapacity < totalSize)
                    {
                        if (alignedBuffer != null)
                        {
                            System.Runtime.InteropServices.NativeMemory.AlignedFree(alignedBuffer);
                        }
                        nuint allocSize = (totalSize + PageAlignment - 1) & ~(PageAlignment - 1);
                        alignedBuffer = (byte*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc(allocSize, PageAlignment);
                        bufferCapacity = allocSize;
                    }

                    MTLRegion region = new(0, 0, 0, (nuint)w, (nuint)h, 1);

                    // 2. Direct 16KB page-aligned UMA getBytes:
                    MetalBindings.objc_msgSend_void(
                        req.TextureHandle,
                        MetalBindings.SelGetBytesBytesPerRowFromRegionMipmapLevel,
                        alignedBuffer,
                        (nuint)bytesPerRow,
                        &region,
                        (nuint)0);

                    // 3. Analyze readback data
                    ProcessReadbackData(alignedBuffer, w, h, req.FrameIndex, req.Format, req.Tag);
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"Headless readback worker error: {ex.Message}");
            }
            finally
            {
                if (alignedBuffer != null)
                {
                    System.Runtime.InteropServices.NativeMemory.AlignedFree(alignedBuffer);
                }
            }
        }

        private unsafe void ProcessReadbackData(byte* span, int w, int h, int frameIndex, Format format, string tag)
        {
            int bytesPerPixel = 4;
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
                    int r, g, b, a;

                    if (format == Format.R11G11B10Float)
                    {
                        uint packed = *(uint*)(span + offset);
                        r = (int)((packed & 0x7FF) >> 3);
                        g = (int)(((packed >> 11) & 0x7FF) >> 3);
                        b = (int)(((packed >> 22) & 0x3FF) >> 2);
                        a = 255;
                    }
                    else
                    {
                        r = span[offset];
                        g = span[offset + 1];
                        b = span[offset + 2];
                        a = span[offset + 3];
                    }

                    sumR += r;
                    sumG += g;
                    sumB += b;
                    sumA += a;
                    minR = Math.Min(minR, r);
                    maxR = Math.Max(maxR, r);

                    if (r != 0 || g != 0 || b != 0)
                    {
                        nonzero++;
                    }
                }
            }

            if (nonzero > gridCount / 4 && (maxR >= 24 || sumG / gridCount >= 24 || sumB / gridCount >= 24))
            {
                _sawNonzeroFrame = true;
            }

            string hex(int v) => v.ToString("X2");
            int px(int x, int y, int c) => (y * w + x) * bytesPerPixel + c;
            int cx = w / 2, cy = h / 2;
            var corners = $"tl={hex(span[px(0, 0, 0)])}{hex(span[px(0, 0, 1)])}{hex(span[px(0, 0, 2)])} " +
                          $"tr={hex(span[px(w - 1, 0, 0)])}{hex(span[px(w - 1, 0, 1)])}{hex(span[px(w - 1, 0, 2)])} " +
                          $"bl={hex(span[px(0, h - 1, 0)])}{hex(span[px(0, h - 1, 1)])}{hex(span[px(0, h - 1, 2)])} " +
                          $"br={hex(span[px(w - 1, h - 1, 0)])}{hex(span[px(w - 1, h - 1, 1)])}{hex(span[px(w - 1, h - 1, 2)])} " +
                          $"center={hex(span[px(cx, cy, 0)])}{hex(span[px(cx, cy, 1)])}{hex(span[px(cx, cy, 2)])}";

            Logger.Warning?.Print(LogClass.Gpu,
                $"[READBACK:{tag}] frame {frameIndex} ({format}): mean=({sumR / gridCount},{sumG / gridCount},{sumB / gridCount},{sumA / gridCount}) " +
                $"minR={minR} maxR={maxR} nonzeroGrid={nonzero}/{gridCount} {corners} sawNonzero={_sawNonzeroFrame}");

            SaveFramePng(span, w, h, frameIndex, format, tag);
        }

        private static unsafe void SaveFramePng(byte* span, int w, int h, int frameIndex, Format format, string tag)
        {
            if (!s_captureEnabled)
            {
                return;
            }

            try
            {
                string dir = CaptureDirectory;
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                string tempBmp = Path.Combine(dir, $"temp_{frameIndex}.bmp");
                string finalPng = Path.Combine(dir, $"frame_{frameIndex:D4}.png");
                string latestPng = Path.Combine(dir, "latest_frame.png");

                int rowPadding = (4 - (w * 3) % 4) % 4;
                int imageSize = (w * 3 + rowPadding) * h;
                int fileSize = 54 + imageSize;

                using (var fs = new FileStream(tempBmp, FileMode.Create, FileAccess.Write))
                using (var bw = new BinaryWriter(fs))
                {
                    // BMP Header
                    bw.Write((byte)'B');
                    bw.Write((byte)'M');
                    bw.Write(fileSize);
                    bw.Write(0);
                    bw.Write(54);

                    // DIB Header (BITMAPINFOHEADER)
                    bw.Write(40);
                    bw.Write(w);
                    bw.Write(h);
                    bw.Write((short)1);
                    bw.Write((short)24); // 24-bit RGB
                    bw.Write(0);
                    bw.Write(imageSize);
                    bw.Write(2835);
                    bw.Write(2835);
                    bw.Write(0);
                    bw.Write(0);

                    byte[] pad = new byte[rowPadding];
                    int bytesPerPixel = 4;

                    // BMP stores scanlines bottom-to-top, BGR
                    for (int y = h - 1; y >= 0; y--)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            int offset = (y * w + x) * bytesPerPixel;
                            byte r, g, b;

                            if (format == Format.R11G11B10Float)
                            {
                                uint packed = *(uint*)(span + offset);
                                r = (byte)((packed & 0x7FF) >> 3);
                                g = (byte)(((packed >> 11) & 0x7FF) >> 3);
                                b = (byte)(((packed >> 22) & 0x3FF) >> 2);
                            }
                            else if (format == Format.B8G8R8A8Unorm)
                            {
                                b = span[offset];
                                g = span[offset + 1];
                                r = span[offset + 2];
                            }
                            else
                            {
                                r = span[offset];
                                g = span[offset + 1];
                                b = span[offset + 2];
                            }

                            bw.Write(b);
                            bw.Write(g);
                            bw.Write(r);
                        }

                        if (rowPadding > 0)
                        {
                            bw.Write(pad);
                        }
                    }
                }

                var psi = new ProcessStartInfo("/usr/bin/sips", $"-s format png \"{tempBmp}\" --out \"{finalPng}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                var proc = Process.Start(psi);
                proc?.WaitForExit(2000);

                try
                {
                    File.Delete(tempBmp);
                    File.Copy(finalPng, latestPng, true);
                }
                catch { }

                Logger.Warning?.Print(LogClass.Gpu, $"[FRAME_CAPTURE] Saved {finalPng} ({w}x{h}, {tag})");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"[FRAME_CAPTURE] Failed to save frame {frameIndex}: {ex.Message}");
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
            _readbackRunning = false;
            _readbackQueue.CompleteAdding();

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

            _upscaler?.Dispose();
            _upscaler = null;
        }
    }
}
