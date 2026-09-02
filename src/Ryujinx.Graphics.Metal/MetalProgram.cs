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

        private readonly Dictionary<ShaderStage, List<Interop.SpirvCross.MslBindingInfo>> _stageBindings = new();

        private bool _disposed;

        public nint VertexFunction => _vertexFunction;
        public nint FragmentFunction => _fragmentFunction;
        public nint ComputeFunction => _computeFunction;

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

        private bool TryCompileStage(ShaderSource source, out string error)
        {
            error = string.Empty;

            string msl;
            List<Interop.SpirvCross.MslBindingInfo> bindings;

            if (source.Language == TargetLanguage.Spirv && source.BinaryCode != null && source.BinaryCode.Length > 0)
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
                return false;
            }

            try
            {
                string entryPoint = GetMslEntryPoint(msl);

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

                GC.SuppressFinalize(this);
            }
        }
    }
}
