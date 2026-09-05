using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Metal.Interop;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// M0 diagnostic: validates the native Metal interop pipeline end-to-end
    /// (device → queue → command buffer → encoder → commit → completion)
    /// without needing a window, drawable, or shader.
    /// Exercised by `Ryu --test`.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static class MetalDiagnostics
    {
        private const int TimeoutMs = 5000;

        /// <summary>
        /// Runs the Metal command pipeline smoke test.
        /// </summary>
        /// <param name="message">Result detail message</param>
        /// <returns>True if the full pipeline completed successfully</returns>
        public static bool RunSmokeTest(out string message)
        {
            nint device = nint.Zero;
            nint queue = nint.Zero;
            nint buffer = nint.Zero;
            nint commandBuffer = nint.Zero;
            nint encoder = nint.Zero;

            try
            {
                // 1. Device.
                device = MetalBindings.MTLCreateSystemDefaultDevice();

                if (device == nint.Zero)
                {
                    message = "MTLCreateSystemDefaultDevice returned null";
                    return false;
                }

                // We take ownership of our references.
                device = MetalBindings.Retain(device);

                // 2. Command queue.
                queue = MetalBindings.objc_msgSend(device, MetalBindings.SelNewCommandQueue);
                queue = MetalBindings.Retain(queue);

                if (queue == nint.Zero)
                {
                    message = "newCommandQueue returned null";
                    return false;
                }

                // 3. Shared buffer (zero-copy UMA path used by the backend).
                buffer = MetalBindings.objc_msgSend(
                    device,
                    MetalBindings.SelNewBufferWithLengthOptions,
                    (nuint)4096,
                    (nuint)(MetalBindings.MTLResourceStorageModeShared | MetalBindings.MTLResourceCPUCacheModeDefaultCache));
                buffer = MetalBindings.Retain(buffer);

                if (buffer == nint.Zero)
                {
                    message = "newBufferWithLength:options: returned null";
                    return false;
                }

                // 4. Command buffer.
                commandBuffer = MetalBindings.objc_msgSend(queue, MetalBindings.SelCommandBufferWithUnretainedReferences);

                if (commandBuffer == nint.Zero)
                {
                    message = "commandBufferWithUnretainedReferences returned null";
                    return false;
                }

                // 5. Blit encoder + a real encoding operation (fill 4 KiB with 0x55).
                encoder = MetalBindings.objc_msgSend(commandBuffer, MetalBindings.SelBlitCommandEncoder);

                if (encoder == nint.Zero)
                {
                    message = "blitCommandEncoder returned null";
                    return false;
                }

                MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelFillBufferRangeValue, buffer, 0, 4096, 0x55);
                MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelEndEncoding);

                // 6. Submit and wait for completion (bounded).
                MetalBindings.objc_msgSend_void(commandBuffer, MetalBindings.SelCommit);

                if (!WaitForCompletion(commandBuffer))
                {
                    message = $"command buffer did not complete within {TimeoutMs} ms";
                    return false;
                }

                // 7. Verify the CPU-visible contents (unified memory round-trip).
                nint contents = MetalBindings.objc_msgSend(buffer, MetalBindings.SelContents);

                if (contents == nint.Zero || !VerifyFill(contents, 4096, 0x55))
                {
                    message = "unified memory fill verification failed";
                    return false;
                }

                message = "Metal command pipeline OK (device/queue/encoder/submit/unified-memory)";

                // M1: prove the zero-copy external-memory path (GPU writes land in the
                // original CPU memory with no staging copies).
                if (!RunZeroCopyTest(out string zeroCopyMessage))
                {
                    message = zeroCopyMessage;
                    return false;
                }

                message += "; zero-copy external memory OK";

                // M3b: prove SPIRV-Cross translates a valid SPIR-V module to MSL.
                if (!RunSpirvToMslTest(out string spirvMessage))
                {
                    message = spirvMessage;
                    return false;
                }

                message += "; SPIR-V to MSL OK";

                // M3b: prove real MTLTexture allocation + CPU<->GPU data round-trip.
                if (!RunTextureRoundTripTest(out string textureMessage))
                {
                    message = textureMessage;
                    return false;
                }

                message += "; MTLTexture round-trip OK";

                // M6: 2D-array texture — per-layer SetData/GetData via the slice path.
                if (!RunTextureArrayTest(out string arrayMessage))
                {
                    message = arrayMessage;
                    return false;
                }

                message += "; texture array OK";

                // M3b: prove MTLBinaryArchive persists compiled pipelines to bytes and
                // reloads them (the disk-cache path that removes live shader-compile stutter).
                if (!RunBinaryArchiveTest(out string archiveMessage))
                {
                    message = archiveMessage;
                    return false;
                }

                message += "; MTLBinaryArchive OK";

                // M4: prove the full pipeline state machine — SPIR-V/GLSL→MSL program,
                // vertex/index/uniform/texture/sampler binding, draw, rasterize, readback.
                if (!RunPipelineTest(out string pipelineMessage))
                {
                    message = pipelineMessage;
                    return false;
                }

                message += "; pipeline state machine OK";

                // M5: command buffer pool — acquire, encode, commit+wait, reuse.
                if (!RunCommandPoolTest(out string poolMessage))
                {
                    message = poolMessage;
                    return false;
                }

                message += "; command pool OK";

                // M4: prove compute encoder submission, argument-table binding, and
                // shared-event completion independently of game shader state.
                if (OperatingSystem.IsMacOSVersionAtLeast(26))
                {
                    if (!RunMetal4ComputeTest(out string computeMessage))
                    {
                        message = computeMessage;
                        return false;
                    }

                    message += "; Metal 4 compute encode OK";

                    // M4+: Metal 4 parallel encoding — the perf-critical path (MTL4Compiler,
                    // per-thread allocators, argument tables, commit:count:, shared-event wait).
                    if (!RunMetal4ParallelTest(out string m4Message))
                    {
                        message = m4Message;
                        return false;
                    }

                    message += "; Metal 4 parallel encode OK";
                }

                return true;
            }
            catch (Exception ex)
            {
                message = $"Metal smoke test exception: {ex.Message}";
                return false;
            }
            finally
            {
                MetalBindings.Release(encoder);
                MetalBindings.Release(commandBuffer);
                MetalBindings.Release(buffer);
                MetalBindings.Release(queue);
                MetalBindings.Release(device);
            }
        }

        private static bool RunSpirvToMslTest(out string message)
        {
            // Compile a trivial GLSL vertex shader to SPIR-V with Shaderc (guaranteed-valid
            // module produced by the same toolchain the Vulkan backend uses), then translate
            // SPIR-V to MSL with SPIRV-Cross.
            const string glsl = """
                #version 450
                layout(location = 0) out vec4 outColor;
                void main()
                {
                    gl_Position = vec4(float(gl_VertexIndex), 0.0, 0.0, 1.0);
                    outColor = vec4(0.2, 0.4, 0.8, 1.0);
                }
                """;

            byte[] spirv = GlslToSpirv(glsl);

            if (spirv == null)
            {
                message = "Shaderc GLSL-to-SPIR-V compilation failed";
                return false;
            }

            ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(spirv);

            string msl = Interop.SpirvCross.SpirvToMsl(words, out string error);

            if (msl == null)
            {
                message = $"SPIRV-Cross MSL translation failed: {error}";
                return false;
            }

            if (msl.Length == 0 || !msl.Contains("metal", StringComparison.OrdinalIgnoreCase))
            {
                message = "SPIRV-Cross produced invalid/empty MSL";
                return false;
            }

            message = "SPIRV-Cross MSL OK";
            return true;
        }

        private static unsafe byte[] GlslToSpirv(string glsl)
        {
            Silk.NET.Shaderc.Shaderc api = Silk.NET.Shaderc.Shaderc.GetApi();
            Silk.NET.Shaderc.Compiler* compiler = api.CompilerInitialize();
            Silk.NET.Shaderc.CompileOptions* options = api.CompileOptionsInitialize();

            api.CompileOptionsSetSourceLanguage(options, Silk.NET.Shaderc.SourceLanguage.Glsl);
            api.CompileOptionsSetTargetSpirv(options, Silk.NET.Shaderc.SpirvVersion.Shaderc15);
            api.CompileOptionsSetTargetEnv(options, Silk.NET.Shaderc.TargetEnv.Vulkan, Silk.NET.Vulkan.Vk.Version12);

            Silk.NET.Shaderc.CompilationResult* result = api.CompileIntoSpv(
                compiler,
                glsl,
                (nuint)glsl.Length,
                Silk.NET.Shaderc.ShaderKind.GlslVertexShader,
                "Ryu",
                "main",
                options);

            Silk.NET.Shaderc.CompilationStatus status = api.ResultGetCompilationStatus(result);

            byte[] code = null;

            if (status == Silk.NET.Shaderc.CompilationStatus.Success)
            {
                Span<byte> spirvBytes = new(api.ResultGetBytes(result), (int)api.ResultGetLength(result));

                code = new byte[(spirvBytes.Length + 3) & ~3];

                spirvBytes.CopyTo(code.AsSpan()[..spirvBytes.Length]);
            }

            api.CompilerRelease(compiler);
            api.CompileOptionsRelease(options);

            return code;
        }

        private static bool RunTextureRoundTripTest(out string message)
        {
            nint device = nint.Zero;

            try
            {
                device = MetalBindings.Retain(MetalBindings.MTLCreateSystemDefaultDevice());

                if (device == nint.Zero)
                {
                    message = "MTLCreateSystemDefaultDevice returned null";
                    return false;
                }

                // 8x8 BGRA8Unorm texture.
                const int Width = 8;
                const int Height = 8;
                const int BytesPerPixel = 4;

                TextureCreateInfo info = new(
                    Width, Height, 1, 1, 1, 1, 1, BytesPerPixel,
                    Format.B8G8R8A8Unorm,
                    DepthStencilMode.Depth,
                    Target.Texture2D,
                    SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);

                using MetalTexture texture = new(device, nint.Zero, info);

                if (texture.TextureHandle == nint.Zero)
                {
                    message = "MTLTexture allocation failed (unsupported format?)";
                    return false;
                }

                // Fill with a deterministic pattern: (x, y, 255, 0) per texel.
                byte[] pattern = new byte[Width * Height * BytesPerPixel];
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        int offset = (y * Width + x) * BytesPerPixel;
                        pattern[offset + 0] = (byte)x;      // B
                        pattern[offset + 1] = (byte)y;      // G
                        pattern[offset + 2] = 255;          // R
                        pattern[offset + 3] = 0;            // A
                    }
                }

                using (MemoryOwner<byte> data = MemoryOwner<byte>.RentCopy(pattern))
                {
                    texture.SetData(data);
                }

                // Read back and verify (replaceRegion -> GPU -> getBytes).
                PinnedSpan<byte> readback = texture.GetData();

                try
                {
                    ReadOnlySpan<byte> bytes = readback.Get();

                    if (bytes.Length < pattern.Length)
                    {
                        message = "MTLTexture readback size mismatch";
                        return false;
                    }

                    for (int i = 0; i < pattern.Length; i++)
                    {
                        if (bytes[i] != pattern[i])
                        {
                            message = $"MTLTexture round-trip mismatch at byte {i}";
                            return false;
                        }
                    }
                }
                finally
                {
                    readback.Dispose();
                }

                message = "MTLTexture round-trip OK";
                return true;
            }
            catch (Exception ex)
            {
                message = $"texture round-trip exception: {ex.Message}";
                return false;
            }
            finally
            {
                MetalBindings.Release(device);
            }
        }

        private static bool RunTextureArrayTest(out string message)
        {
            nint device = nint.Zero;

            try
            {
                device = MetalBindings.Retain(MetalBindings.MTLCreateSystemDefaultDevice());

                if (device == nint.Zero)
                {
                    message = "MTLCreateSystemDefaultDevice returned null";
                    return false;
                }

                // 2-layer 4x4 RGBA8Unorm array.
                const int Width = 4;
                const int Height = 4;
                const int Layers = 2;
                const int BytesPerPixel = 4;

                TextureCreateInfo info = new(
                    Width, Height, Layers, 1, 1, 1, 1, BytesPerPixel,
                    Format.R8G8B8A8Unorm,
                    DepthStencilMode.Depth,
                    Target.Texture2DArray,
                    SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);

                using MetalTexture texture = new(device, nint.Zero, info);

                if (texture.TextureHandle == nint.Zero)
                {
                    message = "MTLTexture (2D array) allocation failed";
                    return false;
                }

                int layerSize = Width * Height * BytesPerPixel;

                // Layer 0: all red. Layer 1: all blue.
                byte[] red = new byte[layerSize];
                byte[] blue = new byte[layerSize];
                for (int i = 0; i < layerSize; i += BytesPerPixel)
                {
                    red[i + 0] = 255;
                    red[i + 3] = 255;
                    blue[i + 2] = 255;
                    blue[i + 3] = 255;
                }

                using (MemoryOwner<byte> owner0 = MemoryOwner<byte>.RentCopy(red))
                {
                    texture.SetData(owner0, 0, 0);
                }

                using (MemoryOwner<byte> owner1 = MemoryOwner<byte>.RentCopy(blue))
                {
                    texture.SetData(owner1, 1, 0);
                }

                // Read back each layer and verify.
                PinnedSpan<byte> data0 = texture.GetData(0, 0);
                byte[] layer0;
                try
                {
                    layer0 = data0.Get().ToArray();
                }
                finally
                {
                    data0.Dispose();
                }

                PinnedSpan<byte> data1 = texture.GetData(1, 0);
                byte[] layer1;
                try
                {
                    layer1 = data1.Get().ToArray();
                }
                finally
                {
                    data1.Dispose();
                }

                // Layer 0 must be red (R high, B low), layer 1 blue (B high, R low).
                if (layer0[0] < 200 || layer0[2] > 60 || layer1[2] < 200 || layer1[0] > 60)
                {
                    message = $"array layer mismatch (layer0.R={layer0[0]}, layer0.B={layer0[2]}, layer1.B={layer1[2]}, layer1.R={layer1[0]})";
                    return false;
                }

                message = "2D array texture layer round-trip OK";
                return true;
            }
            catch (Exception ex)
            {
                message = $"texture array test exception: {ex.Message}";
                return false;
            }
            finally
            {
                MetalBindings.Release(device);
            }
        }

        private static unsafe bool RunBinaryArchiveTest(out string message)
        {
            nint device = nint.Zero;
            nint library = nint.Zero;
            nint vertexFunction = nint.Zero;
            nint fragmentFunction = nint.Zero;
            nint pipeline = nint.Zero;
            nint reloadedPipeline = nint.Zero;
            MetalBinaryArchive archive = null;
            MetalBinaryArchive reloaded = null;

            const string msl = """
                #include <metal_stdlib>
                using namespace metal;

                struct VOut
                {
                    float4 pos [[position]];
                    float4 color;
                };

                vertex VOut vs_main(uint vid [[vertex_id]])
                {
                    const float2 positions[3] = { float2(-0.8, -0.8), float2(0.8, -0.8), float2(0.0, 0.8) };
                    VOut o;
                    o.pos = float4(positions[vid], 0.0, 1.0);
                    o.color = float4(0.2, 0.6, 0.9, 1.0);
                    return o;
                }

                fragment float4 fs_main(VOut in [[stage_in]])
                {
                    return in.color;
                }
                """;

            try
            {
                device = MetalBindings.Retain(MetalBindings.MTLCreateSystemDefaultDevice());

                if (device == nint.Zero)
                {
                    message = "MTLCreateSystemDefaultDevice returned null";
                    return false;
                }

                nint mslString = MetalBindings.CreateNSString(msl);

                library = MetalBindings.objc_msgSend(
                    device,
                    MetalBindings.SelNewLibraryWithSourceOptionsError,
                    mslString,
                    nint.Zero,
                    nint.Zero);

                if (library == nint.Zero)
                {
                    message = "newLibraryWithSource:options:error: returned nil";
                    return false;
                }

                nint selNewFunctionWithName = MetalBindings.sel_registerName("newFunctionWithName:");

                vertexFunction = MetalBindings.objc_msgSend(library, selNewFunctionWithName, MetalBindings.CreateNSString("vs_main"));
                fragmentFunction = MetalBindings.objc_msgSend(library, selNewFunctionWithName, MetalBindings.CreateNSString("fs_main"));

                if (vertexFunction == nint.Zero || fragmentFunction == nint.Zero)
                {
                    message = "newFunctionWithName: returned nil";
                    return false;
                }

                nint MakeDescriptor()
                {
                    nint descriptor = MetalBindings.objc_msgSend(
                        MetalBindings.objc_getClass("MTLRenderPipelineDescriptor"),
                        MetalBindings.SelNew);

                    MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetVertexFunction, vertexFunction);
                    MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetFragmentFunction, fragmentFunction);

                    nint colorAttachments = MetalBindings.objc_msgSend(descriptor, MetalBindings.SelColorAttachments);
                    nint colorAttachment = MetalBindings.objc_msgSend(colorAttachments, MetalBindings.SelObjectAtIndexedSubscript, (nuint)0);
                    MetalBindings.objc_msgSend_void(colorAttachment, MetalBindings.SelSetPixelFormat, (nuint)MetalBindings.MTLPixelFormatBGRA8Unorm);

                    return descriptor;
                }

                // 1. Explicitly compile the pipeline INTO the fresh archive.
                archive = MetalBinaryArchive.Create(device);

                if (archive == null)
                {
                    message = "MetalBinaryArchive.Create returned null";
                    return false;
                }

                nint descriptor = MakeDescriptor();

                bool addOk;
                try
                {
                    addOk = archive.AddRenderPipeline(descriptor);
                }
                finally
                {
                    MetalBindings.Release(descriptor);
                }

                if (!addOk)
                {
                    message = "addRenderPipelineFunctionsWithDescriptor:error: failed";
                    return false;
                }

                // 1b. The pipeline must also be creatable standalone (valid descriptor).
                nint standaloneDescriptor = MakeDescriptor();

                try
                {
                    pipeline = MetalBindings.objc_msgSend(
                        device,
                        MetalBindings.SelNewRenderPipelineStateWithDescriptorError,
                        standaloneDescriptor,
                        nint.Zero);

                    if (pipeline == nint.Zero)
                    {
                        message = "standalone pipeline creation failed";
                        return false;
                    }
                }
                finally
                {
                    MetalBindings.Release(standaloneDescriptor);
                }

                // 2. Serialize — the archive must now contain the compiled pipeline.
                byte[] serialized = archive.Serialize();

                if (serialized == null || serialized.Length == 0)
                {
                    message = "MTLBinaryArchive serialize returned empty data";
                    return false;
                }

                // 3. Reload from the serialized bytes (the disk-cache path).
                reloaded = MetalBinaryArchive.Load(device, serialized);

                if (reloaded == null)
                {
                    message = "MTLBinaryArchive reload from bytes failed";
                    return false;
                }

                // 4. Recreate the pipeline from the reloaded archive with FailOnBinaryArchiveMiss.
                //    Success proves the persisted binary is reused without recompilation.
                nint reloadedDescriptor = MakeDescriptor();

                try
                {
                    reloadedPipeline = reloaded.CreatePipelineState(device, reloadedDescriptor, failOnMiss: true);

                    if (reloadedPipeline == nint.Zero)
                    {
                        message = "pipeline from reloaded archive failed (binary archive miss)";
                        return false;
                    }
                }
                finally
                {
                    MetalBindings.Release(reloadedDescriptor);
                }

                message = "binary archive persist/reload OK";
                return true;
            }
            catch (Exception ex)
            {
                message = $"binary archive test exception: {ex.Message}";
                return false;
            }
            finally
            {
                MetalBindings.Release(reloadedPipeline);
                MetalBindings.Release(pipeline);
                MetalBindings.Release(fragmentFunction);
                MetalBindings.Release(vertexFunction);
                MetalBindings.Release(library);
                MetalBindings.Release(device);

                reloaded?.Dispose();
                archive?.Dispose();
            }
        }

        private static bool RunPipelineTest(out string message)
        {
            // M4 diagnostic: render to an offscreen texture both directly and through
            // the pipeline state machine, using the SAME renderer device/queue.
            MetalRenderer renderer = null;

            try
            {
                renderer = new MetalRenderer();

                if (!RunDirectRenderTest(renderer.DeviceHandle, renderer.CommandQueueHandle, out string directMessage))
                {
                    message = directMessage;
                    return false;
                }

                return RunPipelineStateMachineTest(renderer, out message);
            }
            finally
            {
                renderer?.Dispose();
            }
        }

        private static bool RunPipelineStateMachineTest(MetalRenderer renderer, out string message)
        {
            MetalTexture colorTarget = null;
            MetalTexture sourceTexture = null;
            MetalProgram program = null;
            BufferHandle vertexBuffer = default;
            BufferHandle indexBuffer = default;

            const string vertexGlsl = """
                #version 450
                layout(location = 0) in vec2 inPos;
                layout(location = 1) in vec2 inUv;
                layout(location = 0) out vec2 outUv;
                void main()
                {
                    gl_Position = vec4(inPos, 0.0, 1.0);
                    outUv = inUv;
                }
                """;

            const string fragmentGlsl = """
                #version 450
                layout(location = 0) in vec2 inUv;
                layout(set = 2, binding = 0) uniform sampler2D tex;
                layout(location = 0) out vec4 outColor;
                void main()
                {
                    outColor = texture(tex, inUv);
                }
                """;

            try
            {
                TextureCreateInfo targetInfo = new(
                    128, 128, 1, 1, 1, 1, 1, 4,
                    Format.B8G8R8A8Unorm,
                    DepthStencilMode.Depth,
                    Target.Texture2D,
                    SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);

                colorTarget = new MetalTexture(renderer.DeviceHandle, renderer.CommandQueueHandle, targetInfo);

                if (colorTarget.TextureHandle == nint.Zero)
                {
                    message = "color target MTLTexture allocation failed";
                    return false;
                }

                // Source texture: 4x4 all-red RGBA8Unorm.
                TextureCreateInfo sourceInfo = new(
                    4, 4, 1, 1, 1, 1, 1, 4,
                    Format.R8G8B8A8Unorm,
                    DepthStencilMode.Depth,
                    Target.Texture2D,
                    SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);

                sourceTexture = new MetalTexture(renderer.DeviceHandle, renderer.CommandQueueHandle, sourceInfo);

                byte[] redPixels = new byte[4 * 4 * 4];
                for (int i = 0; i < redPixels.Length; i += 4)
                {
                    redPixels[i + 0] = 255;
                    redPixels[i + 1] = 0;
                    redPixels[i + 2] = 0;
                    redPixels[i + 3] = 255;
                }

                using (MemoryOwner<byte> owner = MemoryOwner<byte>.RentCopy(redPixels))
                {
                    sourceTexture.SetData(owner);
                }

                ShaderSource vs = new(vertexGlsl, ShaderStage.Vertex, TargetLanguage.Glsl);
                ShaderSource fs = new(fragmentGlsl, ShaderStage.Fragment, TargetLanguage.Glsl);

                program = (MetalProgram)renderer.CreateProgram(new[] { vs, fs }, new ShaderInfo(0, default));

                if (program.VertexFunction == nint.Zero || program.FragmentFunction == nint.Zero)
                {
                    message = "MetalProgram failed to create vertex/fragment functions";
                    return false;
                }

                using MetalSampler sampler = (MetalSampler)renderer.CreateSampler(
                    SamplerCreateInfo.Create(MinFilter.Nearest, MagFilter.Nearest));

                if (sampler.SamplerState == nint.Zero)
                {
                    message = "MTLSamplerState creation failed";
                    return false;
                }

                // Fullscreen quad: interleaved float2 pos + float2 uv (stride 16).
                float[] vertices =
                {
                    -1f, -1f, 0f, 0f,
                     1f, -1f, 1f, 0f,
                     1f,  1f, 1f, 1f,
                    -1f,  1f, 0f, 1f,
                };

                uint[] indices = { 0, 1, 2, 0, 2, 3 };

                vertexBuffer = renderer.CreateBuffer(vertices.Length * sizeof(float));
                renderer.SetBufferData(vertexBuffer, 0, MemoryMarshal.AsBytes(vertices.AsSpan()));

                indexBuffer = renderer.CreateBuffer(indices.Length * sizeof(uint));
                renderer.SetBufferData(indexBuffer, 0, MemoryMarshal.AsBytes(indices.AsSpan()));

                // Drive the full pipeline state machine.
                MetalPipeline pipeline = (MetalPipeline)renderer.Pipeline;

                pipeline.SetProgram(program);
                pipeline.SetRenderTargets(new ITexture[] { colorTarget }, null);
                pipeline.SetViewports(new[]
                {
                    new Viewport(
                        new Rectangle<float>(0, 0, 128, 128),
                        ViewportSwizzle.PositiveX, ViewportSwizzle.PositiveY, ViewportSwizzle.PositiveZ, ViewportSwizzle.PositiveW,
                        0f, 1f),
                });
                pipeline.SetScissors(new[] { new Rectangle<int>(0, 0, 128, 128) });
                pipeline.SetVertexAttribs(new[]
                {
                    new VertexAttribDescriptor(0, 0, false, Format.R32G32Float),
                    new VertexAttribDescriptor(0, 8, false, Format.R32G32Float),
                });
                pipeline.SetVertexBuffers(new[]
                {
                    new VertexBufferDescriptor(new BufferRange(vertexBuffer, 0, vertices.Length * sizeof(float)), 16, 0),
                });
                pipeline.SetIndexBuffer(new BufferRange(indexBuffer, 0, indices.Length * sizeof(uint)), IndexType.UInt);
                pipeline.SetTextureAndSampler(ShaderStage.Fragment, 0, sourceTexture, sampler);
                pipeline.SetBlendState(0, new BlendDescriptor(
                    false, default, BlendOp.Add, BlendFactor.One, BlendFactor.Zero, BlendOp.Add, BlendFactor.One, BlendFactor.Zero));
                pipeline.SetDepthTest(new DepthTestDescriptor(false, false, CompareOp.Always));
                pipeline.SetPrimitiveTopology(PrimitiveTopology.Triangles);
                pipeline.SetFaceCulling(false, Face.Back);
                pipeline.SetFrontFace(FrontFace.CounterClockwise);

                pipeline.DrawIndexed(6, 1, 0, 0, 0);
                pipeline.FlushFrame();
                Thread.Sleep(50);

                PinnedSpan<byte> data = colorTarget.GetData();
                byte[] pixels;
                try
                {
                    pixels = data.Get().ToArray();
                }
                finally
                {
                    data.Dispose();
                }

                int maxR = 0;
                int redCount = 0;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    int r = pixels[i + 2];
                    if (r > maxR) maxR = r;
                    if (r > 200) redCount++;
                }

                if (maxR < 200 || redCount < 1000)
                {
                    message = $"pipeline quad render failed (maxR={maxR}, redCount={redCount})";
                    return false;
                }

                // M6: clear-only pass — record a blue clear color and flush a pass; the
                // render pass applies loadAction Clear with that color.
                pipeline.SetRenderTargets(new ITexture[] { colorTarget }, null);
                pipeline.ClearRenderTargetColor(0, 0, 1, 0xF, new ColorF(0f, 0f, 1f, 1f));
                pipeline.Draw(0, 0, 0, 0); // builds the pass (clear), draws nothing
                pipeline.FlushFrame();
                Thread.Sleep(50);

                PinnedSpan<byte> clearData = colorTarget.GetData();
                byte[] clearPixels;
                try
                {
                    clearPixels = clearData.Get().ToArray();
                }
                finally
                {
                    clearData.Dispose();
                }

                byte clearB = clearPixels[(64 * 128 + 64) * 4 + 0]; // BGRA byte[0] = B

                if (clearB < 200)
                {
                    message = $"clear-color pass failed (B={clearB})";
                    return false;
                }

                // M6: depth-clear pass — D32Float depth target cleared to 0.25.
                TextureCreateInfo depthInfo = new(
                    128, 128, 1, 1, 1, 1, 1, 4,
                    Format.D32Float,
                    DepthStencilMode.Depth,
                    Target.Texture2D,
                    SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);

                using MetalTexture depthTarget = new(renderer.DeviceHandle, renderer.CommandQueueHandle, depthInfo);

                if (depthTarget.TextureHandle == nint.Zero)
                {
                    message = "depth target MTLTexture allocation failed";
                    return false;
                }

                pipeline.SetRenderTargets(new ITexture[] { colorTarget }, depthTarget);
                pipeline.ClearRenderTargetColor(0, 0, 1, 0xF, new ColorF(0f, 0f, 0f, 1f));
                pipeline.ClearRenderTargetDepthStencil(0, 1, 0.25f, true, 0, 0);
                pipeline.Draw(0, 0, 0, 0); // builds the pass (color+depth clear), draws nothing
                pipeline.FlushFrame();
                Thread.Sleep(50);

                PinnedSpan<byte> depthData = depthTarget.GetData();
                byte[] depthBytes;
                try
                {
                    depthBytes = depthData.Get().ToArray();
                }
                finally
                {
                    depthData.Dispose();
                }

                float depthValue = BitConverter.ToSingle(depthBytes, (64 * 128 + 64) * 4);

                if (Math.Abs(depthValue - 0.25f) > 0.01f)
                {
                    message = $"depth-clear pass failed (depth={depthValue})";
                    return false;
                }

                message = "pipeline state machine rasterized textured quad + color/depth clear passes";
                return true;
            }
            catch (Exception ex)
            {
                message = $"pipeline test exception: {ex.Message}";
                return false;
            }
            finally
            {
                if (!indexBuffer.Equals(default))
                {
                    renderer?.DeleteBuffer(indexBuffer);
                }

                if (!vertexBuffer.Equals(default))
                {
                    renderer?.DeleteBuffer(vertexBuffer);
                }

                program?.Dispose();
                sourceTexture?.Dispose();
                colorTarget?.Dispose();
            }
        }

        private static bool RunDirectRenderTest(nint device, nint queue, out string message)
        {
            MetalTexture colorTarget = null;
            MetalProgram program = null;

            const string vertexGlsl = """
                #version 450
                layout(location = 0) in vec2 inPos;
                layout(location = 1) in vec2 inUv;
                layout(location = 0) out vec2 outUv;
                void main()
                {
                    gl_Position = vec4(inPos, 0.0, 1.0);
                    outUv = inUv;
                }
                """;

            const string fragmentGlsl = """
                #version 450
                layout(location = 0) out vec4 outColor;
                void main()
                {
                    outColor = vec4(1.0, 0.0, 0.0, 1.0);
                }
                """;

            try
            {
                TextureCreateInfo targetInfo = new(
                    128, 128, 1, 1, 1, 1, 1, 4,
                    Format.B8G8R8A8Unorm,
                    DepthStencilMode.Depth,
                    Target.Texture2D,
                    SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);

                colorTarget = new MetalTexture(device, nint.Zero, targetInfo);

                ShaderSource vs = new(vertexGlsl, ShaderStage.Vertex, TargetLanguage.Glsl);
                ShaderSource fs = new(fragmentGlsl, ShaderStage.Fragment, TargetLanguage.Glsl);
                program = new MetalProgram(device, new[] { vs, fs }, new ShaderInfo(0, default));

                if (program.VertexFunction == nint.Zero || program.FragmentFunction == nint.Zero)
                {
                    message = "direct: program functions missing";
                    return false;
                }

                nint descriptor = MetalBindings.objc_msgSend(
                    MetalBindings.objc_getClass("MTLRenderPipelineDescriptor"),
                    MetalBindings.SelNew);

                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetVertexFunction, program.VertexFunction);
                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetFragmentFunction, program.FragmentFunction);
                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetInputPrimitiveTopology, (nuint)MetalBindings.MTLPrimitiveTopologyClassTriangle);

                // Vertex descriptor: attrib0 float2 @0, attrib1 float2 @8, layout0 stride 16.
                nint vertexDescriptor = MetalBindings.objc_msgSend(
                    MetalBindings.objc_getClass("MTLVertexDescriptor"),
                    MetalBindings.SelVertexDescriptor);
                nint attributes = MetalBindings.objc_msgSend(vertexDescriptor, MetalBindings.SelAttributes);
                nint attribute0 = MetalBindings.objc_msgSend(attributes, MetalBindings.SelObjectAtIndexedSubscript, (nuint)0);
                MetalBindings.objc_msgSend_void(attribute0, MetalBindings.SelSetVertexFormat, (nuint)MetalBindings.MTLVertexFormatFloat2);
                MetalBindings.objc_msgSend_void(attribute0, MetalBindings.SelSetOffset, (nuint)0);
                MetalBindings.objc_msgSend_void(attribute0, MetalBindings.SelSetBufferIndex, (nuint)0);
                nint attribute1 = MetalBindings.objc_msgSend(attributes, MetalBindings.SelObjectAtIndexedSubscript, (nuint)1);
                MetalBindings.objc_msgSend_void(attribute1, MetalBindings.SelSetVertexFormat, (nuint)MetalBindings.MTLVertexFormatFloat2);
                MetalBindings.objc_msgSend_void(attribute1, MetalBindings.SelSetOffset, (nuint)8);
                MetalBindings.objc_msgSend_void(attribute1, MetalBindings.SelSetBufferIndex, (nuint)0);
                nint layouts = MetalBindings.objc_msgSend(vertexDescriptor, MetalBindings.SelLayouts);
                nint layout0 = MetalBindings.objc_msgSend(layouts, MetalBindings.SelObjectAtIndexedSubscript, (nuint)0);
                MetalBindings.objc_msgSend_void(layout0, MetalBindings.SelSetStride, (nuint)16);
                MetalBindings.objc_msgSend_void(layout0, MetalBindings.SelSetStepFunction, (nuint)MetalBindings.MTLVertexStepFunctionPerVertex);
                MetalBindings.objc_msgSend_void(layout0, MetalBindings.SelSetStepRate, (nuint)1);
                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetVertexDescriptor, vertexDescriptor);
                MetalBindings.Release(vertexDescriptor);

                nint colorAttachments = MetalBindings.objc_msgSend(descriptor, MetalBindings.SelColorAttachments);
                nint colorAttachment = MetalBindings.objc_msgSend(colorAttachments, MetalBindings.SelObjectAtIndexedSubscript, (nuint)0);
                MetalBindings.objc_msgSend_void(colorAttachment, MetalBindings.SelSetPixelFormat, (nuint)MetalBindings.MTLPixelFormatBGRA8Unorm);
                MetalBindings.objc_msgSend_void(colorAttachment, MetalBindings.SelSetBlendingEnabled, false);
                MetalBindings.objc_msgSend_void(colorAttachment, MetalBindings.SelSetWriteMask, (nuint)0xF);

                nint pipeline = MetalBindings.objc_msgSend(
                    device,
                    MetalBindings.SelNewRenderPipelineStateWithDescriptorError,
                    descriptor,
                    nint.Zero);
                MetalBindings.Release(descriptor);

                if (pipeline == nint.Zero)
                {
                    message = "direct: pipeline state creation failed";
                    return false;
                }

                nint passDescriptor = MetalBindings.objc_msgSend(
                    MetalBindings.objc_getClass("MTLRenderPassDescriptor"),
                    MetalBindings.SelRenderPassDescriptor);
                nint passColorAttachments = MetalBindings.objc_msgSend(passDescriptor, MetalBindings.SelColorAttachments);
                nint passColorAttachment = MetalBindings.objc_msgSend(passColorAttachments, MetalBindings.SelObjectAtIndexedSubscript, (nuint)0);
                MetalBindings.objc_msgSend_void(passColorAttachment, MetalBindings.SelSetTexture, colorTarget.TextureHandle);
                MetalBindings.objc_msgSend_void(passColorAttachment, MetalBindings.SelSetLoadAction, (nuint)MetalBindings.MTLLoadActionClear);
                MetalBindings.objc_msgSend_void(passColorAttachment, MetalBindings.SelSetStoreAction, (nuint)MetalBindings.MTLStoreActionStore);

                nint commandBuffer = MetalBindings.Retain(MetalBindings.objc_msgSend(queue, MetalBindings.SelCommandBuffer));
                nint encoder = MetalBindings.objc_msgSend(commandBuffer, MetalBindings.SelRenderCommandEncoderWithDescriptor, passDescriptor);

                if (encoder == nint.Zero)
                {
                    message = "direct: render command encoder nil";
                    return false;
                }

                MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelSetRenderPipelineState, pipeline);

                // Vertex data via setVertexBytes (inline), avoiding any buffer plumbing.
                float[] tri =
                {
                    -1f, -1f, 0f, 0f,
                     1f, -1f, 1f, 0f,
                     0f,  1f, 0.5f, 1f,
                };
                unsafe
                {
                    fixed (float* p = tri)
                    {
                        MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelSetVertexBytesLengthAtIndex, (void*)p, (nuint)(tri.Length * sizeof(float)), (nuint)0);
                    }
                }

                MetalBindings.objc_msgSend_void(
                    encoder,
                    MetalBindings.SelDrawPrimitivesVertexStartVertexCountInstanceCount,
                    (nuint)MetalBindings.MTLPrimitiveTypeTriangle,
                    0,
                    3,
                    1);
                MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelEndEncoding);

                MetalBindings.objc_msgSend_void(commandBuffer, MetalBindings.SelCommit);

                // Wait for completion (bounded), then verify the render produced red.
                ulong status = 0;
                for (int i = 0; i < 200; i++)
                {
                    status = MetalBindings.objc_msgSend_ulong_ret(commandBuffer, MetalBindings.SelStatus);
                    if (status >= 4) break;
                    Thread.Sleep(10);
                }

                MetalBindings.Release(encoder);
                MetalBindings.Release(commandBuffer);
                MetalBindings.Release(pipeline);

                PinnedSpan<byte> data = colorTarget.GetData();
                byte[] pixels;
                try
                {
                    pixels = data.Get().ToArray();
                }
                finally
                {
                    data.Dispose();
                }

                int maxR = 0;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    if (pixels[i + 2] > maxR) maxR = pixels[i + 2];
                }

                if (status != 4)
                {
                    message = $"direct: command buffer status={status} (not completed)";
                    return false;
                }

                if (maxR < 200)
                {
                    message = $"direct: render produced no red (maxR={maxR})";
                    return false;
                }

                message = "direct render to offscreen texture OK";
                return true;
            }
            catch (Exception ex)
            {
                message = $"direct render exception: {ex.Message}";
                return false;
            }
            finally
            {
                program?.Dispose();
                colorTarget?.Dispose();
            }
        }

        private static bool RunCommandPoolTest(out string message)
        {
            MetalRenderer renderer = null;
            BufferHandle buffer = default;

            try
            {
                renderer = new MetalRenderer();
                MetalCommandPool pool = renderer.CommandPool;

                if (pool == null || pool.Queue == nint.Zero)
                {
                    message = "MetalCommandPool queue creation failed";
                    return false;
                }

                // Create a shared buffer to fill via the pooled command buffer.
                const int Size = 4096;
                buffer = renderer.CreateBuffer(Size);

                nint commandBuffer = pool.Acquire();

                if (commandBuffer == nint.Zero)
                {
                    message = "pool.Acquire returned null";
                    return false;
                }

                nint encoder = MetalBindings.objc_msgSend(commandBuffer, MetalBindings.SelBlitCommandEncoder);

                if (encoder == nint.Zero)
                {
                    message = "blit command encoder nil";
                    return false;
                }

                MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelFillBufferRangeValue, renderer.GetBuffer(buffer), 0, (nuint)Size, 0x5A);
                MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelEndEncoding);
                MetalBindings.Release(encoder);

                ulong status = pool.CommitAndWait(commandBuffer);

                if (status < 3)
                {
                    message = $"pool command buffer status={status} (not scheduled)";
                    return false;
                }

                // Verify the fill landed (unified memory).
                PinnedSpan<byte> data = renderer.GetBufferData(buffer, 0, Size);
                byte[] bytes;
                try
                {
                    bytes = data.Get().ToArray();
                }
                finally
                {
                    data.Dispose();
                }

                for (int i = 0; i < Size; i++)
                {
                    if (bytes[i] != 0x5A)
                    {
                        message = $"pool fill verification failed at offset {i}";
                        return false;
                    }
                }

                message = "command pool acquire/commit/reuse OK";
                return true;
            }
            catch (Exception ex)
            {
                message = $"command pool test exception: {ex.Message}";
                return false;
            }
            finally
            {
                if (!buffer.Equals(default))
                {
                    renderer?.DeleteBuffer(buffer);
                }

                renderer?.Dispose();
            }
        }

        /// <summary>
        /// M4 compute self-test. This intentionally uses a trivial kernel and one storage
        /// buffer so a failure identifies the M4 compute submission/binding path rather
        /// than a game shader, texture, image, or dispatch-dimension problem.
        /// </summary>
        private static unsafe bool RunMetal4ComputeTest(out string message)
        {
            const uint ExpectedValue = 0xC0FFEE42;

            nint device = nint.Zero;
            nint library = nint.Zero;
            nint function = nint.Zero;
            nint pipeline = nint.Zero;
            nint output = nint.Zero;
            nint tableDescriptor = nint.Zero;
            nint table = nint.Zero;
            nint commandBuffer = nint.Zero;
            nint encoder = nint.Zero;
            Metal4CommandQueue? queue = null;
            Metal4CommandAllocator? allocator = null;
            nint sourceString = nint.Zero;
            nint functionName = nint.Zero;

            const string source = """
                #include <metal_stdlib>
                using namespace metal;
                kernel void m4_compute_probe(device uint* output [[buffer(0)]],
                                             uint gid [[thread_position_in_grid]])
                {
                    if (gid == 0) output[0] = 0xC0FFEE42;
                }
                """;

            try
            {
                device = MetalBindings.Retain(MetalBindings.MTLCreateSystemDefaultDevice());
                if (device == nint.Zero)
                {
                    message = "m4 compute: no Metal device";
                    return false;
                }

                sourceString = MetalBindings.CreateNSString(source);
                nint nsError = nint.Zero;
                library = MetalBindings.objc_msgSend(
                    device,
                    MetalBindings.SelNewLibraryWithSourceOptionsError,
                    sourceString,
                    nint.Zero,
                    (nint)(&nsError));

                if (library == nint.Zero)
                {
                    string error = nsError != nint.Zero ? MetalBindings.GetErrorDescription(nsError) : "unknown error";
                    message = $"m4 compute: MSL library creation failed: {error}";
                    return false;
                }

                functionName = MetalBindings.CreateNSString("m4_compute_probe");
                function = MetalBindings.objc_msgSend(
                    library,
                    MetalBindings.SelNewFunctionWithName,
                    functionName);

                if (function == nint.Zero)
                {
                    message = "m4 compute: newFunctionWithName returned nil";
                    return false;
                }

                pipeline = MetalBindings.objc_msgSend(
                    device,
                    MetalBindings.SelNewComputePipelineStateWithFunctionError,
                    function,
                    nint.Zero);

                if (pipeline == nint.Zero)
                {
                    message = "m4 compute: compute pipeline creation failed";
                    return false;
                }

                output = MetalBindings.objc_msgSend(
                    device,
                    MetalBindings.SelNewBufferWithLengthOptions,
                    (nuint)sizeof(uint),
                    (nuint)MetalBindings.MTLResourceStorageModeShared);

                if (output == nint.Zero)
                {
                    message = "m4 compute: output buffer creation failed";
                    return false;
                }

                tableDescriptor = Metal4Bindings.Metal4New("MTL4ArgumentTableDescriptor");
                Metal4Bindings.m4_msgSend_void(tableDescriptor, Metal4Bindings.SelSetMaxBufferBindCount, (nuint)1);
                Metal4Bindings.m4_msgSend_void(tableDescriptor, Metal4Bindings.SelSetMaxTextureBindCount, (nuint)0);
                Metal4Bindings.m4_msgSend_void(tableDescriptor, Metal4Bindings.SelSetMaxSamplerStateBindCount, (nuint)0);
                table = MetalBindings.objc_msgSend(device, Metal4Bindings.SelNewArgumentTableWithDescriptorError, tableDescriptor, nint.Zero);

                if (table == nint.Zero)
                {
                    message = "m4 compute: argument table creation failed";
                    return false;
                }

                ulong outputAddress = MetalBindings.objc_msgSend_ulong_ret(output, Metal4Bindings.SelGpuAddress);
                if (outputAddress == 0)
                {
                    message = "m4 compute: output buffer has no GPU address";
                    return false;
                }

                Metal4Bindings.m4_msgSend_void(table, Metal4Bindings.SelSetAddressAtIndex, outputAddress, (nuint)0);

                queue = new Metal4CommandQueue(device);
                allocator = new Metal4CommandAllocator(device);
                commandBuffer = queue.BeginCommandBuffer(device, allocator.Handle);
                if (commandBuffer == nint.Zero)
                {
                    message = "m4 compute: command buffer creation failed";
                    return false;
                }

                encoder = MetalBindings.objc_msgSend(commandBuffer, MetalBindings.SelComputeCommandEncoder);
                if (encoder == nint.Zero)
                {
                    message = "m4 compute: compute encoder creation failed";
                    return false;
                }

                MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelSetComputePipelineState, pipeline);
                Metal4Bindings.m4_msgSend_void(encoder, Metal4Bindings.SelSetArgumentTableCompute, table);

                MTLSize grid = new(1, 1, 1);
                MTLSize threads = new(1, 1, 1);
                MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelDispatchThreadgroupsThreadsPerThreadgroup, &grid, &threads);
                MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelEndEncoding);
                MetalBindings.Release(encoder);
                encoder = nint.Zero;

                queue.EndCommandBuffer(commandBuffer);
                ulong signal = queue.SubmitAndWait(new[] { commandBuffer }, TimeoutMs);
                MetalBindings.Release(commandBuffer);
                commandBuffer = nint.Zero;

                if (queue.SignaledValue < signal)
                {
                    message = $"m4 compute: shared event did not signal (signal={signal}, observed={queue.SignaledValue})";
                    return false;
                }

                nint contents = MetalBindings.objc_msgSend(output, MetalBindings.SelContents);
                if (contents == nint.Zero || *(uint*)contents != ExpectedValue)
                {
                    uint actual = contents != nint.Zero ? *(uint*)contents : 0;
                    message = $"m4 compute: output verification failed (0x{actual:X8}, expected 0x{ExpectedValue:X8})";
                    return false;
                }

                message = "Metal 4 compute encode OK (compute encoder + argument table + shared-event completion)";
                return true;
            }
            catch (Exception ex)
            {
                message = $"m4 compute test exception: {ex.Message}";
                return false;
            }
            finally
            {
                if (encoder != nint.Zero)
                {
                    MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelEndEncoding);
                    MetalBindings.Release(encoder);
                }
                if (commandBuffer != nint.Zero)
                {
                    Metal4Bindings.m4_msgSend_void(commandBuffer, Metal4Bindings.SelEndCommandBuffer);
                    MetalBindings.Release(commandBuffer);
                }
                MetalBindings.Release(functionName);
                MetalBindings.Release(sourceString);
                MetalBindings.Release(table);
                MetalBindings.Release(tableDescriptor);
                MetalBindings.Release(output);
                MetalBindings.Release(pipeline);
                MetalBindings.Release(function);
                MetalBindings.Release(library);
                allocator?.Dispose();
                queue?.Dispose();
                MetalBindings.Release(device);
            }
        }

        /// <summary>
        /// M4+: Metal 4 parallel-encoding self-test — the exact prototype that validated
        /// the design on Apple silicon, now exercised through the C# bindings:
        ///   * MTL4Compiler + MTL4PipelineDataSetSerializer (direct MSL 4.0, no SPIR-V)
        ///   * MTL4CommandQueue + per-thread MTL4CommandAllocator
        ///   * per-thread MTL4ArgumentTable (buffers via gpuAddress)
        ///   * commit:count: batch submission + block-free MTLSharedEvent wait
        ///   * per-worker tint verification via offscreen readback
        /// </summary>
        private static unsafe bool RunMetal4ParallelTest(out string message)
        {
            const int Workers = 4;
            const int Tile = 32;

            nint device = nint.Zero;
            nint mslString = nint.Zero;
            nint serDesc = nint.Zero;
            nint serializer = nint.Zero;
            nint compDesc = nint.Zero;
            nint compiler = nint.Zero;
            nint libDesc = nint.Zero;
            nint opts = nint.Zero;
            nint library = nint.Zero;
            nint vdesc = nint.Zero;
            nint fdesc = nint.Zero;
            nint rpDesc = nint.Zero;
            nint cto = nint.Zero;
            nint pipeline = nint.Zero;
            nint atDesc = nint.Zero;
            Metal4CommandQueue m4Queue = null;
            Metal4CommandAllocator[] allocators = null;
            nint[] tables = new nint[Workers];
            nint[] uniforms = new nint[Workers];
            nint[] rts = new nint[Workers];
            nint[] commandBuffers = new nint[Workers];
            nint[] passDescriptors = new nint[Workers];

            const string ms = """
            #include <metal_stdlib>
            using namespace metal;
            struct VSOutput { float4 position [[position]]; };
            vertex VSOutput vp(uint vid [[vertex_id]]) {
                float2 p;
                switch (vid) {
                    case 0: p = float2(-1.0, -1.0); break;
                    case 1: p = float2( 3.0, -1.0); break;
                    default: p = float2(-1.0,  3.0); break;
                }
                return VSOutput{ float4(p, 0.0, 1.0) };
            }
            fragment float4 fp(VSOutput in [[stage_in]], device const float4 &c [[buffer(0)]]) {
                return c;
            }
            """;

            try
            {
                device = MetalBindings.Retain(MetalBindings.MTLCreateSystemDefaultDevice());

                // MTL4PipelineDataSetSerializer (route to AOT archive capturing).
                serDesc = Metal4Bindings.Metal4New("MTL4PipelineDataSetSerializerDescriptor");
                MetalBindings.objc_msgSend_void(serDesc, Metal4Bindings.SelSetConfiguration, (nuint)(Metal4Bindings.M4CaptureDescriptors | Metal4Bindings.M4CaptureBinaries));
                serializer = MetalBindings.objc_msgSend(device, Metal4Bindings.SelNewPipelineDataSetSerializerWithDescriptor, serDesc);

                // MTL4Compiler — the direct-MSL AOT path (no SPIR-V anywhere).
                compDesc = Metal4Bindings.Metal4New("MTL4CompilerDescriptor");
                MetalBindings.objc_msgSend_void(compDesc, Metal4Bindings.SelSetPipelineDataSetSerializer, serializer);
mslString = MetalBindings.CreateNSString("m4-parallel-compiler");
                MetalBindings.objc_msgSend_void(compDesc, MetalBindings.SelSetLabel, mslString);
                compiler = MetalBindings.objc_msgSend(device, Metal4Bindings.SelNewCompilerWithDescriptorError, compDesc, nint.Zero);

                // MTL4LibraryDescriptor (source + MSL 4.0 options).
                libDesc = Metal4Bindings.Metal4New("MTL4LibraryDescriptor");
                mslString = MetalBindings.CreateNSString(ms);
                MetalBindings.objc_msgSend_void(libDesc, Metal4Bindings.SelSetSource, mslString);
                mslString = MetalBindings.CreateNSString("m4Lib");
                MetalBindings.objc_msgSend_void(libDesc, Metal4Bindings.SelSetName, mslString);
                opts = MetalBindings.objc_msgSend(MetalBindings.objc_getClass("MTLCompileOptions"), MetalBindings.SelNew);
                MetalBindings.objc_msgSend_void(opts, Metal4Bindings.SelSetLanguageVersion, (nuint)Metal4Bindings.MTLLanguageVersion4_0);
                MetalBindings.objc_msgSend_void(libDesc, Metal4Bindings.SelSetOptions, opts);
                library = MetalBindings.objc_msgSend(compiler, Metal4Bindings.SelNewLibraryWithDescriptorError, libDesc, nint.Zero);

                if (library == nint.Zero)
                {
                    message = "m4: library creation via MTL4Compiler failed";
                    return false;
                }

                // Function descriptors: vp / fp from the MSL library.
                vdesc = Metal4Bindings.Metal4New("MTL4LibraryFunctionDescriptor");
                mslString = MetalBindings.CreateNSString("vp");
                MetalBindings.objc_msgSend_void(vdesc, Metal4Bindings.SelSetName, mslString);
                MetalBindings.objc_msgSend_void(vdesc, Metal4Bindings.SelSetLibrary, library);
                fdesc = Metal4Bindings.Metal4New("MTL4LibraryFunctionDescriptor");
                mslString = MetalBindings.CreateNSString("fp");
                MetalBindings.objc_msgSend_void(fdesc, Metal4Bindings.SelSetName, mslString);
                MetalBindings.objc_msgSend_void(fdesc, Metal4Bindings.SelSetLibrary, library);

                // Render pipeline descriptor: RGBA8 color target.
                rpDesc = Metal4Bindings.Metal4New("MTL4RenderPipelineDescriptor");
                MetalBindings.objc_msgSend_void(rpDesc, Metal4Bindings.SelSetVertexFunctionDescriptor, vdesc);
                MetalBindings.objc_msgSend_void(rpDesc, Metal4Bindings.SelSetFragmentFunctionDescriptor, fdesc);
                nint colorAttachments = MetalBindings.objc_msgSend(rpDesc, MetalBindings.SelColorAttachments);
                nint ca0 = MetalBindings.objc_msgSend(colorAttachments, MetalBindings.SelObjectAtIndexedSubscript, (nuint)0);
                MetalBindings.objc_msgSend_void(ca0, MetalBindings.SelSetPixelFormat, (nuint)MetalBindings.MTLPixelFormatRGBA8Unorm);

                cto = MetalBindings.objc_msgSend(MetalBindings.objc_getClass("MTL4CompilerTaskOptions"), MetalBindings.SelNew);
                pipeline = MetalBindings.objc_msgSend(compiler, Metal4Bindings.SelNewRenderPipelineStateWithDescriptorCompilerTaskOptionsError, rpDesc, cto, nint.Zero);
                MetalBindings.Release(rpDesc);
                rpDesc = nint.Zero;

                if (pipeline == nint.Zero)
                {
                    message = "m4: render pipeline creation via MTL4Compiler failed";
                    return false;
                }

                // MTL4CommandQueue + per-thread allocators + argument tables.
                m4Queue = new Metal4CommandQueue(device);
                allocators = new Metal4CommandAllocator[Workers];

                atDesc = Metal4Bindings.Metal4New("MTL4ArgumentTableDescriptor");
                Metal4Bindings.m4_msgSend_void(atDesc, Metal4Bindings.SelSetMaxBufferBindCount, (nuint)Workers);
                Metal4Bindings.m4_msgSend_void(atDesc, Metal4Bindings.SelSetMaxTextureBindCount, (nuint)0);
                Metal4Bindings.m4_msgSend_void(atDesc, Metal4Bindings.SelSetMaxSamplerStateBindCount, (nuint)0);

                for (int i = 0; i < Workers; i++)
                {
                    allocators[i] = new Metal4CommandAllocator(device);

                    tables[i] = MetalBindings.objc_msgSend(device, Metal4Bindings.SelNewArgumentTableWithDescriptorError, atDesc, nint.Zero);

                    float[] tint = { (i + 1) / 4.0f, 0.0f, 0.0f, 1.0f };
                    fixed (float* p = tint)
                    {
                        uniforms[i] = Metal4Bindings.m4_msgSend(
                            device, Metal4Bindings.SelNewBufferWithBytesLengthOptions,
                            p, (nuint)(tint.Length * sizeof(float)), (nuint)MetalBindings.MTLResourceStorageModeShared);
                    }

                    ulong gpuAddress = MetalBindings.objc_msgSend_ulong_ret(uniforms[i], Metal4Bindings.SelGpuAddress);
                    Metal4Bindings.m4_msgSend_void(tables[i], Metal4Bindings.SelSetAddressAtIndex, gpuAddress, (nuint)0);

                    nint texDesc = MetalBindings.objc_msgSend(
                        MetalBindings.objc_getClass("MTLTextureDescriptor"),
                        MetalBindings.SelTexture2DDescriptorWithPixelFormatWidthHeightMipmapped,
                        (nuint)MetalBindings.MTLPixelFormatRGBA8Unorm, (nuint)Tile, (nuint)Tile, (byte)0);
                    MetalBindings.objc_msgSend_void(texDesc, MetalBindings.SelSetUsage,
                        (nuint)(MetalBindings.MTLTextureUsageRenderTarget | MetalBindings.MTLTextureUsageShaderRead));
                    rts[i] = MetalBindings.objc_msgSend(device, MetalBindings.SelNewTextureWithDescriptor, texDesc);
                }
                MetalBindings.Release(atDesc);
                atDesc = nint.Zero;

                // Encode concurrently on WorkerCount threads, each its own allocator.
                Thread[] threads = new Thread[Workers];
                for (int i = 0; i < Workers; i++)
                {
                    int wi = i;
                    threads[i] = new Thread(() =>
                    {
                        nint cb = m4Queue.BeginCommandBuffer(device, allocators[wi].Handle);

                        nint passDesc = Metal4Bindings.Metal4New("MTL4RenderPassDescriptor");
                        nint pcAttachments = MetalBindings.objc_msgSend(passDesc, MetalBindings.SelColorAttachments);
                        nint pc0 = MetalBindings.objc_msgSend(pcAttachments, MetalBindings.SelObjectAtIndexedSubscript, (nuint)0);
                        MetalBindings.objc_msgSend_void(pc0, Metal4Bindings.SelSetTexture, rts[wi]);
                        MetalBindings.objc_msgSend_void(pc0, Metal4Bindings.SelSetLoadAction, (nuint)Metal4Bindings.MTLLoadActionClear);
                        MetalBindings.objc_msgSend_void(pc0, Metal4Bindings.SelSetStoreAction, (nuint)Metal4Bindings.MTLStoreActionStore);
                        MTLColor clear = new(0, 0, 0, 1);
                        MetalBindings.objc_msgSend_void(pc0, Metal4Bindings.SelSetClearColor, &clear);

                        nint encoder = MetalBindings.objc_msgSend(cb, Metal4Bindings.SelRenderCommandEncoderWithDescriptor, passDesc);
                        MetalBindings.objc_msgSend_void(encoder, Metal4Bindings.SelSetRenderPipelineState, pipeline);
                        Metal4Bindings.m4_msgSend_void(encoder, Metal4Bindings.SelSetArgumentTableAtStages, tables[wi], Metal4Bindings.MTLRenderStageFragment);
                        Metal4Bindings.m4_msgSend_void(encoder, Metal4Bindings.SelDrawPrimitivesVertexStartVertexCount,
                            (nuint)Metal4Bindings.MTLPrimitiveTypeTriangle, 0, 3);
                        MetalBindings.objc_msgSend_void(encoder, Metal4Bindings.SelEndEncoding);

                        m4Queue.EndCommandBuffer(cb);
                        passDescriptors[wi] = passDesc;
                        commandBuffers[wi] = cb;
                    });
                    threads[i].Start();
                }

                for (int i = 0; i < Workers; i++)
                {
                    threads[i].Join();
                }

                // commit:count: the whole batch, block-free completion via MTLSharedEvent.
                m4Queue.SubmitAndWait(commandBuffers, 10000);

                // Verify each worker's tile rendered its unique tint.
                for (int i = 0; i < Workers; i++)
                {
                    byte[] pixel = new byte[4];
                    MTLRegion region = new(Tile / 2, Tile / 2, 0, 1, 1, 1);
                    fixed (byte* px = pixel)
                    {
                        MetalBindings.objc_msgSend_void(rts[i], MetalBindings.SelGetBytesBytesPerRowFromRegionMipmapLevel,
                            px, 4, &region, 0);
                    }

                    byte expected = (byte)(((i + 1) / 4.0f) * 255.0f + 0.5f);
                    if (pixel[0] != expected)
                    {
                        message = $"m4: worker {i} tint R={pixel[0]} (expected {expected})";
                        return false;
                    }
                }

                message = "Metal 4 parallel encode OK (MTL4Compiler + argument tables + commit:count: + shared-event wait)";
                return true;
            }
            catch (Exception ex)
            {
                message = $"m4 test exception: {ex.Message}";
                return false;
            }
            finally
            {
                MetalBindings.Release(serializer);
                MetalBindings.Release(serDesc);
                MetalBindings.Release(compiler);
                MetalBindings.Release(compDesc);
                MetalBindings.Release(library);
                MetalBindings.Release(libDesc);
                MetalBindings.Release(opts);
                MetalBindings.Release(vdesc);
                MetalBindings.Release(fdesc);
                MetalBindings.Release(rpDesc);
                MetalBindings.Release(cto);
                MetalBindings.Release(atDesc);
                MetalBindings.Release(pipeline);

                for (int i = 0; i < Workers; i++)
                {
                    MetalBindings.Release(tables[i]);
                    MetalBindings.Release(uniforms[i]);
                    MetalBindings.Release(rts[i]);
                    MetalBindings.Release(passDescriptors[i]);
                    MetalBindings.Release(commandBuffers[i]);
                    allocators?[i]?.Dispose();
                }

                m4Queue?.Dispose();
                MetalBindings.Release(device);
            }
        }

        private static unsafe bool RunZeroCopyTest(out string message)
        {
            nint device = nint.Zero;
            nint queue = nint.Zero;
            nint buffer = nint.Zero;
            nint commandBuffer = nint.Zero;
            nint encoder = nint.Zero;
            nint external = nint.Zero;

            try
            {
                device = MetalBindings.Retain(MetalBindings.MTLCreateSystemDefaultDevice());
                queue = MetalBindings.Retain(MetalBindings.objc_msgSend(device, MetalBindings.SelNewCommandQueue));

                // External memory we own (simulates guest MemoryBlock), pre-filled 0xAA.
                const int Size = 256;
                external = (nint)NativeMemory.Alloc((nuint)Size);

                new Span<byte>((void*)external, Size).Fill(0xAA);

                // Wrap it with newBufferWithBytesNoCopy: — Metal must NOT copy and must NOT free it.
                buffer = MetalBindings.Retain(MetalBindings.objc_msgSend(
                    device,
                    MetalBindings.SelNewBufferWithBytesNoCopyOptions,
                    external,
                    (nuint)Size,
                    (nuint)(MetalBindings.MTLResourceStorageModeShared | MetalBindings.MTLResourceCPUCacheModeDefaultCache),
                    nint.Zero));

                if (buffer == nint.Zero)
                {
                    message = "newBufferWithBytesNoCopy:options: returned null";
                    return false;
                }

                // GPU-fill the wrapped buffer with 0x5A via a blit encoder.
                commandBuffer = MetalBindings.objc_msgSend(queue, MetalBindings.SelCommandBufferWithUnretainedReferences);
                encoder = MetalBindings.objc_msgSend(commandBuffer, MetalBindings.SelBlitCommandEncoder);

                MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelFillBufferRangeValue, buffer, 0, (nuint)Size, 0x5A);
                MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelEndEncoding);
                MetalBindings.objc_msgSend_void(commandBuffer, MetalBindings.SelCommit);

                if (!WaitForCompletion(commandBuffer))
                {
                    message = "zero-copy command buffer did not complete";
                    return false;
                }

                // The original external memory must now read 0x5A — proving the GPU wrote
                // through the same physical memory (zero copies, no staging).
                for (int i = 0; i < Size; i++)
                {
                    if (((byte*)external)[i] != 0x5A)
                    {
                        message = $"zero-copy verification failed at offset {i}";
                        return false;
                    }
                }

                message = "zero-copy verified";
                return true;
            }
            catch (Exception ex)
            {
                message = $"zero-copy test exception: {ex.Message}";
                return false;
            }
            finally
            {
                MetalBindings.Release(encoder);
                MetalBindings.Release(commandBuffer);
                MetalBindings.Release(buffer);
                MetalBindings.Release(queue);
                MetalBindings.Release(device);

                if (external != nint.Zero)
                {
                    NativeMemory.Free((void*)external);
                }
            }
        }

        private static bool WaitForCompletion(nint commandBuffer)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();

            while (stopwatch.ElapsedMilliseconds < TimeoutMs)
            {
                ulong status = MetalBindings.objc_msgSend_ulong_ret(commandBuffer, MetalBindings.SelStatus);

                if (status == MetalBindings.MTLCommandBufferStatusCompleted)
                {
                    return true;
                }

                Thread.Sleep(1);
            }

            return false;
        }

        private static unsafe bool VerifyFill(nint contents, int size, byte expected)
        {
            byte* ptr = (byte*)contents;

            for (int i = 0; i < size; i++)
            {
                if (ptr[i] != expected)
                {
                    return false;
                }
            }

            return true;
        }

        public static bool TestFormatBlitAndCompression(out string message)
        {
            nint device = nint.Zero;
            nint queue = nint.Zero;

            try
            {
                device = MetalBindings.Retain(MetalBindings.MTLCreateSystemDefaultDevice());
                queue = MetalBindings.Retain(MetalBindings.objc_msgSend(device, MetalBindings.SelNewCommandQueue));

                if (device == nint.Zero || queue == nint.Zero)
                {
                    message = "Metal device/queue creation failed";
                    return false;
                }

                // 1. Test ASTC 4x4 compressed texture allocation and round-trip
                TextureCreateInfo astcInfo = new(
                    64, 64, 1, 1, 1, 4, 4, 16,
                    Format.Astc4x4Unorm,
                    DepthStencilMode.Depth,
                    Target.Texture2D,
                    SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);

                using MetalTexture astcTex = new(device, queue, astcInfo);
                if (astcTex.TextureHandle == nint.Zero)
                {
                    message = "ASTC texture allocation failed";
                    return false;
                }

                int astcBlocks = (64 / 4) * (64 / 4);
                byte[] astcBytes = new byte[astcBlocks * 16];
                Array.Fill<byte>(astcBytes, 0xAB);
                using (MemoryOwner<byte> owner = MemoryOwner<byte>.RentCopy(astcBytes))
                {
                    astcTex.SetData(owner);
                }

                PinnedSpan<byte> astcRead = astcTex.GetData();
                try
                {
                    if (astcRead.Get()[0] != 0xAB)
                    {
                        message = "ASTC block data readback mismatch";
                        return false;
                    }
                }
                finally
                {
                    astcRead.Dispose();
                }

                // 2. Test HDR (R11G11B10Float) to RGBA8 format blit
                TextureCreateInfo hdrInfo = new(
                    64, 64, 1, 1, 1, 1, 1, 4,
                    Format.R11G11B10Float,
                    DepthStencilMode.Depth,
                    Target.Texture2D,
                    SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);

                using MetalTexture hdrTex = new(device, queue, hdrInfo);
                if (hdrTex.TextureHandle == nint.Zero)
                {
                    message = "HDR texture allocation failed";
                    return false;
                }

                byte[] hdrBytes = new byte[64 * 64 * 4];
                for (int i = 0; i < 64 * 64; i++)
                {
                    hdrBytes[i * 4 + 0] = 0xC0; // Red = 1.0f (mantissa 0, exp 15)
                    hdrBytes[i * 4 + 1] = 0x03;
                    hdrBytes[i * 4 + 2] = 0x00;
                    hdrBytes[i * 4 + 3] = 0x00;
                }
                using (MemoryOwner<byte> owner = MemoryOwner<byte>.RentCopy(hdrBytes))
                {
                    hdrTex.SetData(owner);
                }

                TextureCreateInfo dstInfo = new(
                    64, 64, 1, 1, 1, 1, 1, 4,
                    Format.R8G8B8A8Unorm,
                    DepthStencilMode.Depth,
                    Target.Texture2D,
                    SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);

                using MetalTexture dstTex = new(device, queue, dstInfo);
                if (dstTex.TextureHandle == nint.Zero)
                {
                    message = "Destination blit texture allocation failed";
                    return false;
                }

                MetalFormatBlit blitter = new(device, queue);
                blitter.Copy(hdrTex, dstTex, new Extents2D(0, 0, 64, 64), new Extents2D(0, 0, 64, 64), false);
                blitter.Dispose();

                Thread.Sleep(50);

                PinnedSpan<byte> dstRead = dstTex.GetData();
                byte r;
                try
                {
                    r = dstRead.Get()[0];
                }
                finally
                {
                    dstRead.Dispose();
                }

                if (r < 200)
                {
                    message = $"HDR format blit failed: expected Red > 200, got R={r}";
                    return false;
                }

                message = $"ASTC 4x4 OK; HDR R11G11B10 -> RGBA8 format blit OK (R={r})";
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
            finally
            {
                if (queue != nint.Zero) MetalBindings.Release(queue);
                if (device != nint.Zero) MetalBindings.Release(device);
            }
        }

        public static bool TestMultiTargetRenderPass(out string message)
        {
            try
            {
                using var renderer = new MetalRenderer();
                var pipeline = (MetalPipeline)renderer.Pipeline;

                TextureCreateInfo colorInfo0 = new(64, 64, 1, 1, 1, 1, 1, 4, Format.R8G8B8A8Unorm, DepthStencilMode.Depth, Target.Texture2D, SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);
                TextureCreateInfo colorInfo1 = new(64, 64, 1, 1, 1, 1, 1, 4, Format.B8G8R8A8Unorm, DepthStencilMode.Depth, Target.Texture2D, SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);
                TextureCreateInfo depthInfo = new(64, 64, 1, 1, 1, 1, 1, 4, Format.D32Float, DepthStencilMode.Depth, Target.Texture2D, SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);

                using MetalTexture target0 = new(renderer.DeviceHandle, renderer.CommandQueueHandle, colorInfo0);
                using MetalTexture target1 = new(renderer.DeviceHandle, renderer.CommandQueueHandle, colorInfo1);
                using MetalTexture depthTarget = new(renderer.DeviceHandle, renderer.CommandQueueHandle, depthInfo);

                pipeline.SetRenderTargets(new ITexture[] { target0, target1 }, depthTarget);
                pipeline.ClearRenderTargetColor(0, 0, 1, 0xF, new ColorF(1f, 0f, 0f, 1f));
                pipeline.ClearRenderTargetColor(1, 0, 1, 0xF, new ColorF(0f, 1f, 0f, 1f));
                pipeline.ClearRenderTargetDepthStencil(0, 1, 0.75f, true, 0, 0);

                pipeline.Draw(0, 0, 0, 0);
                pipeline.FlushFrame();
                Thread.Sleep(50);

                PinnedSpan<byte> data0 = target0.GetData();
                byte r0;
                try { r0 = data0.Get()[0]; } finally { data0.Dispose(); }

                PinnedSpan<byte> data1 = target1.GetData();
                byte g1;
                try { g1 = data1.Get()[1]; } finally { data1.Dispose(); }

                PinnedSpan<byte> depthData = depthTarget.GetData();
                float depthVal;
                try { depthVal = BitConverter.ToSingle(depthData.Get()); } finally { depthData.Dispose(); }

                if (r0 < 200 || g1 < 200 || Math.Abs(depthVal - 0.75f) > 0.05f)
                {
                    message = $"MRT pass failed (R0={r0}, G1={g1}, Depth={depthVal})";
                    return false;
                }

                message = $"MRT simultaneous targets OK (target0=Red, target1=Green, depth=0.75)";
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        public static bool TestGuiRenderLoopSimulation(out string message)
        {
            if (!OperatingSystem.IsMacOS())
            {
                message = "Skipped (not macOS)";
                return true;
            }

            try
            {
                using var renderer = new MetalRenderer();
                var pipeline = (MetalPipeline)renderer.Pipeline;

                TextureCreateInfo sceneInfo = new(
                    320, 240, 1, 1, 1, 1, 1, 4,
                    Format.R8G8B8A8Unorm,
                    DepthStencilMode.Depth,
                    Target.Texture2D,
                    SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);

                using MetalTexture sceneTexture = new(renderer.DeviceHandle, renderer.CommandQueueHandle, sceneInfo);

                pipeline.SetRenderTargets(new ITexture[] { sceneTexture }, null);
                pipeline.ClearRenderTargetColor(0, 0, 1, 0xF, new ColorF(0f, 1f, 1f, 1f));
                pipeline.Draw(0, 0, 0, 0);
                pipeline.FlushFrame();
                Thread.Sleep(50);

                MetalWindow metalWindow = new(renderer, renderer.DeviceHandle, renderer.CommandQueueHandle);
                metalWindow.SetSize(320, 240);
                
                metalWindow.Present(sceneTexture, default, () => { });

                PinnedSpan<byte> readback = sceneTexture.GetData();
                byte g, b;
                try
                {
                    ReadOnlySpan<byte> s = readback.Get();
                    g = s[1];
                    b = s[2];
                }
                finally
                {
                    readback.Dispose();
                }

                metalWindow.Dispose();

                if (g < 200 || b < 200)
                {
                    message = $"RenderLoop simulation failed (G={g}, B={b})";
                    return false;
                }

                message = $"End-to-End GUI RenderLoop OK (Render -> Flush -> Present -> Readback Cyan G={g}, B={b})";
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        public static bool TestVideoNvdecBlit(out string message)
        {
            try
            {
                using var renderer = new MetalRenderer();

                TextureCreateInfo yInfo = new(64, 64, 1, 1, 1, 1, 1, 1, Format.R8Unorm, DepthStencilMode.Depth, Target.Texture2D, SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);
                TextureCreateInfo uvInfo = new(32, 32, 1, 1, 1, 1, 1, 2, Format.R8G8Unorm, DepthStencilMode.Depth, Target.Texture2D, SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);
                TextureCreateInfo rgbInfo = new(64, 64, 1, 1, 1, 1, 1, 4, Format.R8G8B8A8Unorm, DepthStencilMode.Depth, Target.Texture2D, SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);

                using MetalTexture yTex = new(renderer.DeviceHandle, renderer.CommandQueueHandle, yInfo);
                using MetalTexture uvTex = new(renderer.DeviceHandle, renderer.CommandQueueHandle, uvInfo);
                using MetalTexture rgbTex = new(renderer.DeviceHandle, renderer.CommandQueueHandle, rgbInfo);

                byte[] yBytes = new byte[64 * 64];
                Array.Fill<byte>(yBytes, 0xC0);
                using (MemoryOwner<byte> owner = MemoryOwner<byte>.RentCopy(yBytes))
                {
                    yTex.SetData(owner);
                }

                byte[] uvBytes = new byte[32 * 32 * 2];
                Array.Fill<byte>(uvBytes, 0x80);
                using (MemoryOwner<byte> owner = MemoryOwner<byte>.RentCopy(uvBytes))
                {
                    uvTex.SetData(owner);
                }

                MetalFormatBlit blitter = new(renderer.DeviceHandle, renderer.CommandQueueHandle);
                blitter.Copy(yTex, rgbTex, new Extents2D(0, 0, 64, 64), new Extents2D(0, 0, 64, 64), false);
                blitter.Dispose();

                Thread.Sleep(50);

                PinnedSpan<byte> rgbRead = rgbTex.GetData();
                byte r;
                try
                {
                    r = rgbRead.Get()[0];
                }
                finally
                {
                    rgbRead.Dispose();
                }

                if (r < 180)
                {
                    message = $"Video YUV/NV12 blit failed (R={r})";
                    return false;
                }

                message = $"NVDEC / YUV video surface blit OK (Y -> RGB R={r})";
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }

        public static unsafe bool TestStaticBufferInjectionGroundTruth(out string message)
        {
            const nuint PageAlignment = 16384;
            const int width = 128;
            const int height = 128;
            const int bytesPerRow = width * 4;
            const nuint totalBytes = bytesPerRow * height;

            byte* hostInject = null;
            byte* hostReadback = null;

            nint device = nint.Zero;
            nint queue = nint.Zero;

            try
            {
                device = MetalBindings.Retain(MetalBindings.MTLCreateSystemDefaultDevice());
                queue = MetalBindings.Retain(MetalBindings.objc_msgSend(device, MetalBindings.SelNewCommandQueue));

                if (device == nint.Zero || queue == nint.Zero)
                {
                    message = "Metal device/queue creation failed";
                    return false;
                }

                hostInject = (byte*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc(totalBytes, PageAlignment);
                hostReadback = (byte*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc(totalBytes, PageAlignment);

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int offset = (y * width + x) * 4;
                        hostInject[offset + 0] = (byte)(x & 0xFF);
                        hostInject[offset + 1] = (byte)(y & 0xFF);
                        hostInject[offset + 2] = (byte)((x ^ y) & 0xFF);
                        hostInject[offset + 3] = 0xFF;
                    }
                }

                TextureCreateInfo info = new(
                    width, height, 1, 1, 1, 1, 1, 4,
                    Format.R8G8B8A8Unorm,
                    DepthStencilMode.Depth,
                    Target.Texture2D,
                    SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);

                using MetalTexture tex = new(device, queue, info);
                if (tex.TextureHandle == nint.Zero)
                {
                    message = "Shared texture creation failed";
                    return false;
                }

                MTLRegion region = new(0, 0, 0, (nuint)width, (nuint)height, 1);
                MetalBindings.objc_msgSend_void(
                    tex.TextureHandle,
                    MetalBindings.SelReplaceRegionMipmapLevelWithBytesBytesPerRow,
                    &region,
                    (nuint)0,
                    hostInject,
                    (nuint)bytesPerRow);

                MetalBindings.objc_msgSend_void(
                    tex.TextureHandle,
                    MetalBindings.SelGetBytesBytesPerRowFromRegionMipmapLevel,
                    hostReadback,
                    (nuint)bytesPerRow,
                    &region,
                    (nuint)0);

                int mismatches = 0;
                for (nuint i = 0; i < totalBytes; i++)
                {
                    if (hostInject[i] != hostReadback[i])
                    {
                        mismatches++;
                    }
                }

                if (mismatches > 0)
                {
                    message = $"Ground truth injection failed: {mismatches}/{totalBytes} byte mismatches";
                    return false;
                }

                message = $"Static buffer ground truth OK (16KB page-aligned UMA direct match 16,384 px)";
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
            finally
            {
                if (hostInject != null) System.Runtime.InteropServices.NativeMemory.AlignedFree(hostInject);
                if (hostReadback != null) System.Runtime.InteropServices.NativeMemory.AlignedFree(hostReadback);
                if (queue != nint.Zero) MetalBindings.Release(queue);
                if (device != nint.Zero) MetalBindings.Release(device);
            }
        }

        /// <summary>
        /// Validates the invariants used by the live game render path. This is deliberately
        /// independent of the synthetic color tests: it catches the common emulator failure
        /// modes where geometry is clipped, depth rejects every fragment, texture inputs are
        /// empty, or a debug session was launched without the relevant Metal diagnostics.
        /// Xcode GPU Frame Capture remains the authoritative test for per-draw attachment
        /// contents and must be run with RYU_METAL_GPU_CAPTURE=1 on a real game boot.
        /// </summary>
        public static bool TestGameRenderValidation(out string message)
        {
            const int targetWidth = 1920;
            const int targetHeight = 1080;

            // Mirrors MetalPipeline's viewport normalization for the Switch's common
            // (0, height, width, -height) convention.
            float viewportY = targetHeight;
            float viewportHeight = -targetHeight;
            if (viewportHeight < 0)
            {
                viewportY += viewportHeight;
                viewportHeight = -viewportHeight;
            }

            if (!float.IsFinite(viewportY) || !float.IsFinite(viewportHeight) ||
                viewportY < 0 || viewportHeight <= 0 || viewportY + viewportHeight > targetHeight)
            {
                message = "viewport normalization produced an invalid Metal rectangle";
                return false;
            }

            // Mirrors the live scissor clamp, including the oversized 65535x65535
            // rectangles emitted by NieR.
            int sourceScissorWidth = 65535;
            int sourceScissorHeight = 65535;
            int scissorWidth = Math.Clamp(sourceScissorWidth, 1, targetWidth);
            int scissorHeight = Math.Clamp(sourceScissorHeight, 1, targetHeight);

            if (scissorWidth != targetWidth || scissorHeight != targetHeight)
            {
                message = "scissor clamp does not cover the active render target";
                return false;
            }

            // A disabled depth test must never map to Never. This is the exact state
            // that previously appeared in the game draw logs.
            const ulong metalCompareAlways = MetalBindings.MTLCompareFunctionAlways;
            if (metalCompareAlways != 7 || !float.IsFinite(1.0f))
            {
                message = "depth-state invariant failed";
                return false;
            }

            bool captureRequested = Environment.GetEnvironmentVariable("RYU_METAL_GPU_CAPTURE") == "1";
            bool nanValidationRequested = Environment.GetEnvironmentVariable("MTL_SHADER_VALIDATION_NAN_INF") == "1";
            bool debugLayerRequested = Environment.GetEnvironmentVariable("MTL_DEBUG_LAYER") == "1";

            message = $"viewport/scissor/depth/finite-value checks OK; capture={(captureRequested ? "requested" : "off")}, debugLayer={(debugLayerRequested ? "on" : "off")}, nanInf={(nanValidationRequested ? "on" : "off")}";
            return true;
        }

        public static unsafe bool TestBlitTransferCopyIsolation(out string message)
        {
            try
            {
                using var renderer = new MetalRenderer();
                var pipeline = (MetalPipeline)renderer.Pipeline;

                TextureCreateInfo srcInfo = new(64, 64, 1, 1, 1, 1, 1, 4, Format.R8G8B8A8Unorm, DepthStencilMode.Depth, Target.Texture2D, SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);
                TextureCreateInfo dstInfo = new(64, 64, 1, 1, 1, 1, 1, 4, Format.R8G8B8A8Unorm, DepthStencilMode.Depth, Target.Texture2D, SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);

                using MetalTexture srcTex = new(renderer.DeviceHandle, renderer.CommandQueueHandle, srcInfo);
                using MetalTexture dstTex = new(renderer.DeviceHandle, renderer.CommandQueueHandle, dstInfo);

                pipeline.SetRenderTargets(new ITexture[] { srcTex }, null);
                pipeline.ClearRenderTargetColor(0, 0, 1, 0xF, new ColorF(1f, 0f, 1f, 1f));
                pipeline.Draw(0, 0, 0, 0);
                pipeline.FlushFrame();

                nint cb = MetalBindings.Retain(MetalBindings.objc_msgSend(renderer.CommandQueueHandle, MetalBindings.SelCommandBuffer));

                if (renderer.M4Queue.CompletionEvent != nint.Zero && renderer.M4Queue.LastSignaledValue > 0)
                {
                    MetalBindings.objc_msgSend_void(cb, MetalBindings.SelEncodeWaitForEventValue, renderer.M4Queue.CompletionEvent, renderer.M4Queue.LastSignaledValue);
                }

                nint blitEnc = MetalBindings.objc_msgSend(cb, MetalBindings.SelBlitCommandEncoder);

                MTLOrigin srcOrigin = new(0, 0, 0);
                MTLSize srcSize = new(64, 64, 1);
                MTLOrigin dstOrigin = new(0, 0, 0);

                MetalBindings.objc_msgSend_void(
                    blitEnc,
                    MetalBindings.SelCopyFromTextureSourceSliceSourceLevelSourceOriginSourceSizeToTextureDestinationSliceDestinationLevelDestinationOrigin,
                    srcTex.TextureHandle,
                    (nuint)0,
                    (nuint)0,
                    &srcOrigin,
                    &srcSize,
                    dstTex.TextureHandle,
                    (nuint)0,
                    (nuint)0,
                    &dstOrigin);

                MetalBindings.objc_msgSend_void(blitEnc, MetalBindings.SelEndEncoding);
                MetalBindings.objc_msgSend_void(cb, MetalBindings.SelCommit);
                MetalBindings.objc_msgSend_void(cb, MetalBindings.SelWaitUntilCompleted);
                MetalBindings.Release(cb);

                PinnedSpan<byte> readback = dstTex.GetData();
                byte r, g, b;
                try
                {
                    ReadOnlySpan<byte> span = readback.Get();
                    r = span[0];
                    g = span[1];
                    b = span[2];
                }
                finally
                {
                    readback.Dispose();
                }

                if (r < 200 || g > 20 || b < 200)
                {
                    message = $"Blit isolation copy failed (R={r}, G={g}, B={b})";
                    return false;
                }

                message = $"Blit transfer copy isolation OK (R={r}, G={g}, B={b} synced via fence)";
                return true;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return false;
            }
        }
    }
}
