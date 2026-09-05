using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Metal.Interop;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// M4: a real shader program for the native Metal backend.
    ///
    /// Takes the GAL ShaderSource[] (SPIR-V via Shaderc, or GLSL fallback), translates
    /// each stage to MSL with SPIRV-Cross, creates an MTLFunction per stage, and reflects
    /// the auto-assigned MSL resource bindings so MetalPipeline can bind buffers,
    /// textures and samplers at the correct indices at draw time.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public class MetalProgram : IProgram
    {
        private readonly nint _device;

        private nint _vertexFunction;
        private nint _fragmentFunction;
        private nint _computeFunction;

        // MTL4 libraries are separate from classic MTL libraries. MTL4 render
        // pipeline descriptors require MTL4LibraryFunctionDescriptor instances,
        // so retaining only classic MTLFunction objects is insufficient for an
        // MTL4 render encoder.
        private readonly Dictionary<ShaderStage, nint> _m4Libraries = new();
        private readonly Dictionary<ShaderStage, string> _m4EntryPoints = new();
        private nint _m4Compiler;
        private nint _m4TaskOptions;

        private readonly Dictionary<ShaderStage, List<Interop.SpirvCross.MslBindingInfo>> _stageBindings = new();

        private bool _disposed;

        public nint VertexFunction => _vertexFunction;
        public nint FragmentFunction => _fragmentFunction;
        public nint ComputeFunction => _computeFunction;

        public nint GetM4Library(ShaderStage stage)
        {
            return _m4Libraries.TryGetValue(stage, out nint library) ? library : nint.Zero;
        }

        public string GetM4EntryPoint(ShaderStage stage)
        {
            return _m4EntryPoints.TryGetValue(stage, out string entryPoint) ? entryPoint : string.Empty;
        }

        public nint M4Compiler => _m4Compiler;
        public nint M4TaskOptions => _m4TaskOptions;

        private nint _computePipelineState;

        public nint GetOrCreateComputePipelineState()
        {
            if (_computePipelineState != nint.Zero)
            {
                return _computePipelineState;
            }

            if (_computeFunction == nint.Zero)
            {
                return nint.Zero;
            }

            _computePipelineState = MetalBindings.objc_msgSend(
                _device,
                MetalBindings.SelNewComputePipelineStateWithFunctionError,
                _computeFunction,
                nint.Zero);

            return _computePipelineState;
        }

        public MetalProgram(nint device, ShaderSource[] shaders, ShaderInfo info)
        {
            _device = device;

            if (shaders != null)
            {
                foreach (ShaderSource source in shaders)
                {
                    if (!TryCompileStage(source, out string error))
                    {
                        Logger.Error?.Print(LogClass.Gpu, $"MetalProgram: failed to compile {source.Stage} stage: {error}");
                    }
                }
            }
        }

        public MetalProgram(nint device, byte[] programBinary, bool isFragment, ShaderInfo info)
        {
            _device = device;

            // LoadProgramBinary: the binary is SPIR-V for the given stage.
            if (programBinary != null && programBinary.Length > 0)
            {
                ShaderStage stage = isFragment ? ShaderStage.Fragment : ShaderStage.Vertex;
                ShaderSource source = new(programBinary, stage, TargetLanguage.Spirv);

                if (!TryCompileStage(source, out string error))
                {
                    Logger.Error?.Print(LogClass.Gpu, $"MetalProgram: failed to load program binary: {error}");
                }
            }
        }

        private static int _shaderLogCount = 0;

        private const string FixedFragmentShader = @"
#include <metal_stdlib>
using namespace metal;
struct FixedOut { float4 position [[position]]; };
fragment float4 main0(FixedOut in [[stage_in]]) {
    return float4(1.0, 0.0, 0.0, 1.0);
}
";

        private bool TryCompileStage(ShaderSource source, out string error)
        {
            error = string.Empty;

            string msl;
            List<Interop.SpirvCross.MslBindingInfo> bindings;

            // Diagnostic-only fallback. It deliberately removes texture and UBO
            // dependencies so a solid red frame proves geometry, viewport, attachment,
            // and presentation independently from game shader math/resources.
            if (source.Stage == ShaderStage.Fragment && Environment.GetEnvironmentVariable("RYU_METAL_FIXED_FRAGMENT") == "1")
            {
                msl = FixedFragmentShader;
                bindings = new List<Interop.SpirvCross.MslBindingInfo>();
            }
            else if (source.Language == TargetLanguage.Spirv && source.BinaryCode != null && source.BinaryCode.Length > 0)
            {
                ReadOnlySpan<uint> words = MemoryMarshal.Cast<byte, uint>(source.BinaryCode);
                msl = Interop.SpirvCross.SpirvToMsl(words, out bindings, out error);
            }
            else if (source.Code != null)
            {
                byte[] spirv = MetalShaderConverter.GlslToSpirv(source.Code, source.Stage);

                if (spirv == null)
                {
                    error = "GLSL→SPIR-V (Shaderc) failed";
                    return false;
                }

                msl = Interop.SpirvCross.SpirvToMsl(MemoryMarshal.Cast<byte, uint>(spirv), out bindings, out error);
            }
            else
            {
                error = "shader source has no code";
                return false;
            }

            if (msl == null)
            {
                return false;
            }

            // Log the first few shaders' MSL and bindings for diagnostics
            if (System.Threading.Interlocked.Increment(ref _shaderLogCount) <= 4)
            {
                string preview = msl.Length > 4000 ? msl.Substring(0, 4000) + "..." : msl;
                Logger.Warning?.Print(LogClass.Gpu, $"[SHADER_MSL] {source.Stage} MSL ({msl.Length} chars):\n{preview}");
                
                foreach (var b in bindings)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"[SHADER_BIND] {source.Stage}: kind={b.Kind} set={b.Set} binding={b.Binding} -> mslIndex={b.MslIndex} mslSamplerIdx={b.MslSamplerIndex}");
                }
            }

            // Safety guard: Ensure no [[sampler(n)]] with n >= 16 reaches the Metal compiler.
            // Fold overflow samplers into constexpr file-scope samplers without duplicate attributes.
            msl = Interop.SpirvCross.CompactSamplerAttributes(msl);

            nint sourceString = MetalBindings.CreateNSString(msl);
            nint nsError = nint.Zero;
            nint library;

            unsafe
            {
                library = MetalBindings.objc_msgSend(
                    _device,
                    MetalBindings.SelNewLibraryWithSourceOptionsError,
                    sourceString,
                    nint.Zero,
                    (nint)(&nsError));
            }

            MetalBindings.Release(sourceString);

            if (library == nint.Zero)
            {
                error = $"Metal MSL compiler rejected the generated {source.Stage} shader: {MetalBindings.GetErrorDescription(nsError)}";
                MetalTelemetryDump.DumpFailedShader(source.Stage, msl, error, bindings);
                return false;
            }

            try
            {
                string entryPoint = GetMslEntryPoint(msl);

                if (!TryCompileM4Library(msl, source.Stage, entryPoint, out string m4Error))
                {
                    error = m4Error;
                    MetalTelemetryDump.DumpFailedShader(source.Stage, msl, error, bindings);
                    return false;
                }

                nint function = MetalBindings.objc_msgSend(
                    library,
                    MetalBindings.SelNewFunctionWithName,
                    MetalBindings.CreateNSString(entryPoint));

                if (function == nint.Zero)
                {
                    error = $"newFunctionWithName(\"{entryPoint}\") returned nil";
                    return false;
                }

                switch (source.Stage)
                {
                    case ShaderStage.Vertex:
                        _vertexFunction = function;
                        break;
                    case ShaderStage.Fragment:
                        _fragmentFunction = function;
                        break;
                    case ShaderStage.Compute:
                        _computeFunction = function;
                        break;
                    default:
                        MetalBindings.Release(function);
                        error = $"unsupported stage {source.Stage}";
                        return false;
                }

                _stageBindings[source.Stage] = bindings;

                return true;
            }
            finally
            {
                MetalBindings.Release(library);
            }
        }

        private unsafe bool TryCompileM4Library(string msl, ShaderStage stage, string entryPoint, out string error)
        {
            error = string.Empty;

            nint compilerDescriptor = nint.Zero;
            nint compiler = nint.Zero;
            nint libraryDescriptor = nint.Zero;
            nint options = nint.Zero;
            nint sourceString = nint.Zero;
            nint nsError = nint.Zero;

            try
            {
                if (_m4Compiler == nint.Zero)
                {
                    compilerDescriptor = Metal4Bindings.Metal4New("MTL4CompilerDescriptor");
                    if (compilerDescriptor == nint.Zero)
                    {
                        error = "MTL4CompilerDescriptor creation failed";
                        return false;
                    }

                    MetalBindings.objc_msgSend_void(
                        compilerDescriptor,
                        MetalBindings.SelSetLabel,
                        MetalBindings.CreateNSString($"ryu-m4-{stage}"));

                    compiler = MetalBindings.objc_msgSend(
                        _device,
                        Metal4Bindings.SelNewCompilerWithDescriptorError,
                        compilerDescriptor,
                        nint.Zero);

                    if (compiler == nint.Zero)
                    {
                        error = $"MTL4Compiler creation failed for {stage}";
                        return false;
                    }

                    _m4Compiler = compiler;
                    compiler = nint.Zero;
                }

                if (_m4TaskOptions == nint.Zero)
                {
                    _m4TaskOptions = MetalBindings.objc_msgSend(
                        MetalBindings.objc_getClass("MTL4CompilerTaskOptions"),
                        MetalBindings.SelNew);
                }

                libraryDescriptor = Metal4Bindings.Metal4New("MTL4LibraryDescriptor");
                sourceString = MetalBindings.CreateNSString(msl);
                options = MetalBindings.objc_msgSend(
                    MetalBindings.objc_getClass("MTLCompileOptions"),
                    MetalBindings.SelNew);

                MetalBindings.objc_msgSend_void(libraryDescriptor, Metal4Bindings.SelSetSource, sourceString);
                MetalBindings.objc_msgSend_void(libraryDescriptor, Metal4Bindings.SelSetOptions, options);
                MetalBindings.objc_msgSend_void(libraryDescriptor, Metal4Bindings.SelSetName, MetalBindings.CreateNSString($"ryu-m4-{stage}"));
                MetalBindings.objc_msgSend_void(options, Metal4Bindings.SelSetLanguageVersion, (nuint)Metal4Bindings.MTLLanguageVersion4_0);

                nint library = MetalBindings.objc_msgSend(
                    _m4Compiler,
                    Metal4Bindings.SelNewLibraryWithDescriptorError,
                    libraryDescriptor,
                    (nint)(&nsError));

                if (library == nint.Zero)
                {
                    error = $"MTL4 MSL compiler rejected the generated {stage} shader: {MetalBindings.GetErrorDescription(nsError)}";
                    MetalTelemetryDump.DumpFailedShader(stage, msl, error, null);
                    return false;
                }

                if (_m4Libraries.TryGetValue(stage, out nint oldLibrary) && oldLibrary != nint.Zero)
                {
                    MetalBindings.Release(oldLibrary);
                }

                _m4Libraries[stage] = library;
                _m4EntryPoints[stage] = entryPoint;
                return true;
            }
            finally
            {
                MetalBindings.Release(compilerDescriptor);
                MetalBindings.Release(compiler);
                MetalBindings.Release(libraryDescriptor);
                MetalBindings.Release(options);
                MetalBindings.Release(sourceString);
            }
        }

        /// <summary>
        /// Looks up the auto-assigned MSL buffer/texture index for a (set, binding)
        /// resource in the given stage. For combined image samplers,
        /// <paramref name="samplerIndex"/> receives the sampler half.
        /// Returns uint.MaxValue when the resource is not used by the stage.
        ///
        /// When the exact (set, binding) is not found, falls back to a resource of the
        /// same <paramref name="kind"/> and binding number. This handles toolchains
        /// (e.g. Shaderc with auto-bind) that drop the descriptor-set decoration and
        /// report everything as set 0.
        /// </summary>
        public uint GetMslBinding(ShaderStage stage, uint set, uint binding, Interop.SpirvCross.MslResourceKind kind, out uint samplerIndex)
        {
            samplerIndex = uint.MaxValue;

            if (_stageBindings.TryGetValue(stage, out List<Interop.SpirvCross.MslBindingInfo> list))
            {
                foreach (Interop.SpirvCross.MslBindingInfo b in list)
                {
                    if (b.Set == set && b.Binding == binding)
                    {
                        samplerIndex = b.MslSamplerIndex;
                        return b.MslIndex;
                    }
                }

                foreach (Interop.SpirvCross.MslBindingInfo b in list)
                {
                    if (b.Binding == binding && b.Kind == kind)
                    {
                        samplerIndex = b.MslSamplerIndex;
                        return b.MslIndex;
                    }
                }
            }

            return uint.MaxValue;
        }

        /// <summary>
        /// SPIRV-Cross MSL renames entry points to "&lt;name&gt;&lt;index&gt;" (e.g. "main0").
        /// Extract the actual entry-point function name from the generated MSL by finding
        /// the vertex/fragment/kernel function declaration.
        /// </summary>
        private static string GetMslEntryPoint(string msl)
        {
            foreach (string rawLine in msl.Split('\n'))
            {
                string line = rawLine.Trim();

                if (!line.StartsWith("vertex ", StringComparison.Ordinal) &&
                    !line.StartsWith("fragment ", StringComparison.Ordinal) &&
                    !line.StartsWith("kernel ", StringComparison.Ordinal))
                {
                    continue;
                }

                int paren = line.IndexOf('(');

                if (paren < 0)
                {
                    continue;
                }

                string beforeParen = line[..paren].Trim();
                string[] parts = beforeParen.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length >= 2)
                {
                    return parts[^1];
                }
            }

            return "main";
        }

        public byte[] GetBinary() => Array.Empty<byte>();

        public ProgramLinkStatus CheckProgramLink(bool blocking)
        {
            return _vertexFunction != nint.Zero || _fragmentFunction != nint.Zero || _computeFunction != nint.Zero
                ? ProgramLinkStatus.Success
                : ProgramLinkStatus.Failure;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;

                if (_vertexFunction != nint.Zero)
                {
                    MetalBindings.Release(_vertexFunction);
                    _vertexFunction = nint.Zero;
                }

                if (_fragmentFunction != nint.Zero)
                {
                    MetalBindings.Release(_fragmentFunction);
                    _fragmentFunction = nint.Zero;
                }

                if (_computeFunction != nint.Zero)
                {
                    MetalBindings.Release(_computeFunction);
                    _computeFunction = nint.Zero;
                }

                foreach (nint library in _m4Libraries.Values)
                {
                    if (library != nint.Zero)
                    {
                        MetalBindings.Release(library);
                    }
                }

                _m4Libraries.Clear();
                _m4EntryPoints.Clear();

                if (_m4TaskOptions != nint.Zero)
                {
                    MetalBindings.Release(_m4TaskOptions);
                    _m4TaskOptions = nint.Zero;
                }

                if (_computePipelineState != nint.Zero)
                {
                    MetalBindings.Release(_computePipelineState);
                    _computePipelineState = nint.Zero;
                }

                if (_m4Compiler != nint.Zero)
                {
                    MetalBindings.Release(_m4Compiler);
                    _m4Compiler = nint.Zero;
                }

                GC.SuppressFinalize(this);
            }
        }
    }
}
