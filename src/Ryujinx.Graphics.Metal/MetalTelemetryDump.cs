using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Metal.Interop;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Automated diagnostic telemetry and artifact dump pipeline for the native Metal backend.
    /// Activated via environment variable RYU_METAL_DUMP_PASSES=1 or on pipeline/shader failures.
    /// Provides:
    /// 1. Per-pass color attachment image readback with HDR Reinhard tonemapping.
    /// 2. Compact JSON render graph telemetry per frame.
    /// 3. Automated failing MSL shader and reflection bundle dumps.
    /// 4. Hardware rasterizer state summarizer on draw.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal static class MetalTelemetryDump
    {
        public static readonly bool IsEnabled = Environment.GetEnvironmentVariable("RYU_METAL_DUMP_PASSES") == "1";
        public static readonly bool IsVerboseLoggingEnabled = Environment.GetEnvironmentVariable("RYU_METAL_VERBOSE_LOG") == "1";

        public static readonly string DumpDirectory = InitializeDumpDirectory();

        // Whether continuous per-pass frame dumping is explicitly enabled.
        // Defaults to off. Set RYU_METAL_DUMP_CONTINUOUS=1 to re-enable.
        public static readonly bool IsContinuousDumpEnabled =
            Environment.GetEnvironmentVariable("RYU_METAL_DUMP_CONTINUOUS") == "1";

        // Minimum frame interval between continuous samples (20 seconds at 30 FPS).
        private const int ContinuousDumpInterval = 600;

        private static string InitializeDumpDirectory()
        {
            // Default to system temp directory so new files are NOT inside the IDE
            // workspace and do not trigger the language server file watcher.
            string dir = Environment.GetEnvironmentVariable("RYU_METAL_DUMP_DIR") ??
                         Path.Combine(Path.GetTempPath(), "ryu_metal_dumps");
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "frames"));
                Directory.CreateDirectory(Path.Combine(dir, "failed_shaders"));
                Directory.CreateDirectory(Path.Combine(dir, "failed_pipelines"));
            }
            catch { }
            return dir;
        }

        public sealed class AttachmentRecord
        {
            public nint TextureHandle { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public Format Format { get; set; }
            public string LoadAction { get; set; } = "Load";
            public string StoreAction { get; set; } = "Store";
            public ColorF ClearColor { get; set; }
        }

        public sealed class DepthAttachmentRecord
        {
            public nint TextureHandle { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public Format Format { get; set; }
            public string LoadAction { get; set; } = "Load";
            public string StoreAction { get; set; } = "Store";
            public float DepthClear { get; set; }
        }

        public sealed class PassRecord
        {
            public int PassIndex { get; set; }
            public List<AttachmentRecord> ColorAttachments { get; } = new();
            public DepthAttachmentRecord DepthAttachment { get; set; }
            public int DrawCount { get; set; }
            public string PsoStatus { get; set; } = "Success";
            public List<string> DrawStates { get; } = new();
        }

        // ====================================================================
        // ====================================================================
        // Requirement 1 & 2: Per-Pass Readback, Image Dump & JSON Frame Graph
        // ====================================================================

        private struct FrameDumpRequest
        {
            public int FrameNumber;
            public ulong SignalValue;
            public nint CompletionEvent;
            public List<PassRecord> Passes;
        }

        private static readonly BlockingCollection<FrameDumpRequest> _dumpQueue =
            new(new ConcurrentQueue<FrameDumpRequest>(), 4);
        private static readonly Thread _dumpThread;
        private static volatile bool _dumpRunning;

        static MetalTelemetryDump()
        {
            if (IsEnabled)
            {
                _dumpRunning = true;
                _dumpThread = new Thread(DumpWorkerLoop)
                {
                    Name = "MetalTelemetryWorker",
                    IsBackground = true
                };
                _dumpThread.Start();
            }
        }

        public static void QueueFrameDump(int frameNumber, nint completionEvent, ulong signalValue, List<PassRecord> passes)
        {
            if (!IsEnabled || passes == null || passes.Count == 0)
            {
                return;
            }

            bool hasFailure = false;
            for (int i = 0; i < passes.Count; i++)
            {
                if (passes[i].PsoStatus != "Success")
                {
                    hasFailure = true;
                    break;
                }
            }

            // Only sample on explicit failure, or when continuous dumping is enabled
            // at a relaxed 600-frame (20-second) interval. This prevents the 464 MB/s
            // disk write storm and 185% language-server CPU spike seen during normal gameplay.
            bool shouldSample = hasFailure ||
                (IsContinuousDumpEnabled && (frameNumber <= 5 || frameNumber % ContinuousDumpInterval == 0));

            if (!shouldSample || _dumpQueue.Count >= 2)
            {
                return;
            }

            var clonedPasses = new List<PassRecord>(passes.Count);
            for (int i = 0; i < passes.Count; i++)
            {
                var p = passes[i];
                var clonedP = new PassRecord
                {
                    PassIndex = p.PassIndex,
                    DrawCount = p.DrawCount,
                    PsoStatus = p.PsoStatus,
                    DepthAttachment = p.DepthAttachment
                };
                clonedP.ColorAttachments.AddRange(p.ColorAttachments);
                clonedP.DrawStates.AddRange(p.DrawStates);
                clonedPasses.Add(clonedP);
            }

            _dumpQueue.TryAdd(new FrameDumpRequest
            {
                FrameNumber = frameNumber,
                SignalValue = signalValue,
                CompletionEvent = completionEvent,
                Passes = clonedPasses
            });
        }

        private static void DumpWorkerLoop()
        {
            while (_dumpRunning)
            {
                try
                {
                    if (!_dumpQueue.TryTake(out FrameDumpRequest req, 100))
                    {
                        continue;
                    }

                    if (req.CompletionEvent != nint.Zero && req.SignalValue > 0)
                    {
                        Metal4Bindings.m4_wait_event_bool(
                            req.CompletionEvent,
                            Metal4Bindings.SelWaitUntilSignaledValueTimeoutMS,
                            req.SignalValue,
                            5000);
                    }

                    ProcessFrameDump(req.FrameNumber, req.Passes);
                }
                catch { }
            }
        }

        public static void ProcessFrameDump(int frameNumber, IReadOnlyList<PassRecord> passes)
        {
            if (passes == null || passes.Count == 0)
            {
                return;
            }

            try
            {
                string framesDir = Path.Combine(DumpDirectory, "frames");
                Directory.CreateDirectory(framesDir);

                // 1. Emit JSON frame graph
                EmitFrameGraphJson(framesDir, frameNumber, passes);

                // 2. Dump per-pass attachments
                for (int p = 0; p < passes.Count; p++)
                {
                    PassRecord pass = passes[p];

                    for (int a = 0; a < pass.ColorAttachments.Count; a++)
                    {
                        AttachmentRecord att = pass.ColorAttachments[a];

                        if (att.TextureHandle == nint.Zero || att.Width <= 0 || att.Height <= 0)
                        {
                            continue;
                        }

                        DumpAttachmentImage(framesDir, frameNumber, pass.PassIndex, a, att);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"[TELEMETRY] Failed to process frame dump for frame {frameNumber}: {ex.Message}");
            }
        }

        private static void EmitFrameGraphJson(string framesDir, int frameNumber, IReadOnlyList<PassRecord> passes)
        {
            var jsonPasses = new List<object>();

            for (int i = 0; i < passes.Count; i++)
            {
                PassRecord p = passes[i];
                AttachmentRecord primary = p.ColorAttachments.Count > 0 ? p.ColorAttachments[0] : null;

                string targetHandle = primary != null ? $"0x{primary.TextureHandle:X}" : "None";
                string resolution = primary != null ? $"{primary.Width}x{primary.Height}" : "None";
                string format = primary != null ? primary.Format.ToString() : "None";
                string loadAction = primary != null ? primary.LoadAction : "None";
                string storeAction = primary != null ? primary.StoreAction : "None";
                double[] clearColor = primary != null
                    ? new double[] { primary.ClearColor.Red, primary.ClearColor.Green, primary.ClearColor.Blue, primary.ClearColor.Alpha }
                    : new double[] { 0.0, 0.0, 0.0, 1.0 };

                string depthFormat = p.DepthAttachment != null ? p.DepthAttachment.Format.ToString() : "None";

                jsonPasses.Add(new
                {
                    passIndex = p.PassIndex,
                    targetHandle,
                    resolution,
                    format,
                    loadAction,
                    storeAction,
                    clearColor,
                    depthFormat,
                    drawCount = p.DrawCount,
                    psoStatus = p.PsoStatus
                });
            }

            var frameGraph = new
            {
                frame = frameNumber,
                passCount = passes.Count,
                passes = jsonPasses
            };

            string jsonPath = Path.Combine(framesDir, $"frame_{frameNumber:D5}_graph.json");
            string jsonContent = JsonSerializer.Serialize(frameGraph, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(jsonPath, jsonContent);
            Logger.Warning?.Print(LogClass.Gpu, $"[TELEMETRY] Wrote frame graph: {jsonPath}");
        }

        private static unsafe void DumpAttachmentImage(string framesDir, int frameNumber, int passIndex, int attachmentIndex, AttachmentRecord att)
        {
            int w = att.Width;
            int h = att.Height;
            Format format = att.Format;

            int bytesPerPixel = GetBytesPerPixel(format);
            int bytesPerRow = w * bytesPerPixel;
            nuint totalBytes = (nuint)(bytesPerRow * h);

            if (totalBytes == 0)
            {
                return;
            }

            byte* buffer = (byte*)NativeMemory.AlignedAlloc(totalBytes, 16384);

            try
            {
                MTLRegion region = new(0, 0, 0, (nuint)w, (nuint)h, 1);

                MetalBindings.objc_msgSend_void(
                    att.TextureHandle,
                    MetalBindings.SelGetBytesBytesPerRowFromRegionMipmapLevel,
                    buffer,
                    (nuint)bytesPerRow,
                    &region,
                    (nuint)0);

                string attSuffix = attachmentIndex > 0 ? $"_att{attachmentIndex}" : string.Empty;
                string tempBmp = Path.Combine(framesDir, $"temp_f{frameNumber}_p{passIndex}_{w}x{h}.bmp");
                string finalPng = Path.Combine(framesDir, $"frame_{frameNumber:D5}_pass_{passIndex:D2}{attSuffix}_{w}x{h}_{format}.png");

                SaveTonemappedBmp(buffer, w, h, format, tempBmp);

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
                }
                catch { }

                Logger.Warning?.Print(LogClass.Gpu, $"[TELEMETRY] Saved pass attachment: {finalPng}");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"[TELEMETRY] Failed to dump pass {passIndex} image: {ex.Message}");
            }
            finally
            {
                NativeMemory.AlignedFree(buffer);
            }
        }

        private static int GetBytesPerPixel(Format format)
        {
            return format switch
            {
                Format.R8Unorm or Format.R8Uint or Format.R8Snorm or Format.R8Sint => 1,
                Format.R16Float or Format.R16Unorm or Format.R16Uint or Format.R8G8Unorm or Format.R8G8Snorm => 2,
                Format.R16G16Float or Format.R16G16Unorm or Format.R32Float or Format.R32Uint => 4,
                Format.R16G16B16A16Float or Format.R16G16B16A16Unorm or Format.R32G32Float => 8,
                Format.R32G32B32A32Float or Format.R32G32B32A32Uint => 16,
                _ => 4
            };
        }

        private static unsafe void SaveTonemappedBmp(byte* span, int w, int h, Format format, string destinationBmp)
        {
            int rowPadding = (4 - (w * 3) % 4) % 4;
            int imageSize = (w * 3 + rowPadding) * h;
            int fileSize = 54 + imageSize;

            using var fs = new FileStream(destinationBmp, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);

            // BMP Header
            bw.Write((byte)'B');
            bw.Write((byte)'M');
            bw.Write(fileSize);
            bw.Write(0);
            bw.Write(54);

            // DIB Header
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
            int bytesPerPixel = GetBytesPerPixel(format);

            // BMP stores scanlines bottom-to-top, BGR
            for (int y = h - 1; y >= 0; y--)
            {
                for (int x = 0; x < w; x++)
                {
                    int offset = (y * w + x) * bytesPerPixel;
                    byte r, g, b;

                    UnpackAndTonemapPixel(span + offset, format, out r, out g, out b);

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

        private static unsafe void UnpackAndTonemapPixel(byte* ptr, Format format, out byte r, out byte g, out byte b)
        {
            switch (format)
            {
                case Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb:
                    b = ptr[0];
                    g = ptr[1];
                    r = ptr[2];
                    break;

                case Format.R11G11B10Float:
                {
                    uint packed = *(uint*)ptr;
                    float rf = UnpackFloat11(packed & 0x7FF);
                    float gf = UnpackFloat11((packed >> 11) & 0x7FF);
                    float bf = UnpackFloat10((packed >> 22) & 0x3FF);

                    // Reinhard tone mapping: c / (1.0 + c)
                    rf = rf / (1.0f + MathF.Max(0f, rf));
                    gf = gf / (1.0f + MathF.Max(0f, gf));
                    bf = bf / (1.0f + MathF.Max(0f, bf));

                    // Gamma curve to sRGB
                    rf = MathF.Pow(rf, 1.0f / 2.2f);
                    gf = MathF.Pow(gf, 1.0f / 2.2f);
                    bf = MathF.Pow(bf, 1.0f / 2.2f);

                    r = (byte)Math.Clamp((int)(rf * 255f), 0, 255);
                    g = (byte)Math.Clamp((int)(gf * 255f), 0, 255);
                    b = (byte)Math.Clamp((int)(bf * 255f), 0, 255);
                    break;
                }

                case Format.R16Float:
                {
                    Half hVal = *(Half*)ptr;
                    float fVal = (float)hVal;
                    fVal = fVal / (1.0f + MathF.Max(0f, fVal));
                    fVal = MathF.Pow(MathF.Max(0f, fVal), 1.0f / 2.2f);
                    byte gray = (byte)Math.Clamp((int)(fVal * 255f), 0, 255);
                    r = gray;
                    g = gray;
                    b = gray;
                    break;
                }

                case Format.R16G16B16A16Float:
                {
                    Half rh = *(Half*)ptr;
                    Half gh = *(Half*)(ptr + 2);
                    Half bh = *(Half*)(ptr + 4);

                    float rf = (float)rh;
                    float gf = (float)gh;
                    float bf = (float)bh;

                    rf = rf / (1.0f + MathF.Max(0f, rf));
                    gf = gf / (1.0f + MathF.Max(0f, gf));
                    bf = bf / (1.0f + MathF.Max(0f, bf));

                    rf = MathF.Pow(MathF.Max(0f, rf), 1.0f / 2.2f);
                    gf = MathF.Pow(MathF.Max(0f, gf), 1.0f / 2.2f);
                    bf = MathF.Pow(MathF.Max(0f, bf), 1.0f / 2.2f);

                    r = (byte)Math.Clamp((int)(rf * 255f), 0, 255);
                    g = (byte)Math.Clamp((int)(gf * 255f), 0, 255);
                    b = (byte)Math.Clamp((int)(bf * 255f), 0, 255);
                    break;
                }

                case Format.R8Unorm:
                {
                    byte gray = ptr[0];
                    r = gray;
                    g = gray;
                    b = gray;
                    break;
                }

                default: // R8G8B8A8Unorm and other 4-byte standard formats
                    r = ptr[0];
                    g = ptr[1];
                    b = ptr[2];
                    break;
            }
        }

        private static float UnpackFloat11(uint val)
        {
            uint mantissa = val & 0x3F;
            uint exponent = (val >> 6) & 0x1F;

            if (exponent == 0)
            {
                return mantissa == 0 ? 0.0f : (mantissa / 64.0f) * MathF.Pow(2, -14);
            }
            if (exponent == 31)
            {
                return 1.0f;
            }

            return (1.0f + mantissa / 64.0f) * MathF.Pow(2, (int)exponent - 15);
        }

        private static float UnpackFloat10(uint val)
        {
            uint mantissa = val & 0x1F;
            uint exponent = (val >> 5) & 0x1F;

            if (exponent == 0)
            {
                return mantissa == 0 ? 0.0f : (mantissa / 32.0f) * MathF.Pow(2, -14);
            }
            if (exponent == 31)
            {
                return 1.0f;
            }

            return (1.0f + mantissa / 32.0f) * MathF.Pow(2, (int)exponent - 15);
        }

        // ====================================================================
        // Requirement 3: Failing Shader Auto-Dump Bundle
        // ====================================================================

        public static void DumpFailedShader(
            ShaderStage stage,
            string msl,
            string errorMessage,
            IReadOnlyList<SpirvCross.MslBindingInfo> bindings)
        {
            try
            {
                ulong hash = ComputeHash(msl ?? string.Empty);
                string shaderDir = Path.Combine(DumpDirectory, "failed_shaders", $"{hash:X16}");
                Directory.CreateDirectory(shaderDir);

                // 1. shader.metal
                File.WriteAllText(Path.Combine(shaderDir, "shader.metal"), msl ?? string.Empty);

                // 2. compiler_error.txt
                File.WriteAllText(Path.Combine(shaderDir, "compiler_error.txt"), errorMessage ?? "Unknown error");

                // 3. reflection_bindings.txt
                var sb = new StringBuilder();
                sb.AppendLine($"=== Maxwell Guest -> Metal MSL Reflection Bindings ===");
                sb.AppendLine($"Stage: {stage}");
                sb.AppendLine($"Shader Hash: {hash:X16}");
                sb.AppendLine();

                if (bindings != null && bindings.Count > 0)
                {
                    sb.AppendLine("--- Resource Bindings ---");
                    foreach (SpirvCross.MslBindingInfo b in bindings)
                    {
                        string hostSlot = b.MslSamplerIndex != uint.MaxValue ? $" [SamplerSlot: {b.MslSamplerIndex}]" : string.Empty;
                        sb.AppendLine($"Kind={b.Kind,-14} | Set={b.Set} | Binding={b.Binding,-3} -> MSL Index={b.MslIndex,-2}{hostSlot}");
                    }
                }
                else
                {
                    sb.AppendLine("No reflected bindings recorded.");
                }

                File.WriteAllText(Path.Combine(shaderDir, "reflection_bindings.txt"), sb.ToString());

                Logger.Error?.Print(LogClass.Gpu, $"[TELEMETRY] Dumped rejected {stage} shader to: {shaderDir}");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"[TELEMETRY] Failed to dump rejected shader: {ex.Message}");
            }
        }

        public static void DumpFailedPipeline(string key, nint pipelineDescriptor, string errorMessage)
        {
            try
            {
                ulong hash = ComputeHash(key ?? string.Empty);
                string pipelineDir = Path.Combine(DumpDirectory, "failed_pipelines", $"{hash:X16}");
                Directory.CreateDirectory(pipelineDir);

                File.WriteAllText(Path.Combine(pipelineDir, "pipeline_key.txt"), key ?? string.Empty);
                File.WriteAllText(Path.Combine(pipelineDir, "compiler_error.txt"), errorMessage ?? "Unknown pipeline compilation error");

                Logger.Error?.Print(LogClass.Gpu, $"[TELEMETRY] Dumped failing pipeline to: {pipelineDir}");
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"[TELEMETRY] Failed to dump failing pipeline: {ex.Message}");
            }
        }

        private static ulong ComputeHash(string str)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in str)
            {
                hash ^= (byte)c;
                hash *= 1099511628211UL;
                hash ^= (byte)(c >> 8);
                hash *= 1099511628211UL;
            }
            return hash;
        }

        // ====================================================================
        // Requirement 4: Hardware State Summarizer on Draw
        // ====================================================================

        public static string FormatDrawState(
            int passIndex,
            int drawIndex,
            Viewport vp,
            Rectangle<int> sc,
            string cull,
            string winding,
            bool depthTest,
            string depthFunc)
        {
            string windingStr = winding switch
            {
                "CounterClockwise" => "CCW",
                "Clockwise" => "CW",
                _ => winding
            };
            return $"[DRAW_STATE] Pass #{passIndex} Draw #{drawIndex}: Viewport=[{vp.Region.X:F0}, {vp.Region.Y:F0}, {vp.Region.Width:F0}, {vp.Region.Height:F0}] Scissor=[{sc.X}, {sc.Y}, {sc.Width}, {sc.Height}] Cull={cull} Winding={windingStr} DepthTest={depthTest} DepthFunc={depthFunc}";
        }
    }
}
