using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// M4: converts GLSL source to SPIR-V using Shaderc (the same toolchain the
    /// Vulkan backend uses), so MetalProgram can translate game shaders that arrive
    /// as GLSL (the fallback path when SPIR-V is not available) to MSL via SPIRV-Cross.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static unsafe class MetalShaderConverter
    {
        public static byte[] GlslToSpirv(string glsl, ShaderStage stage)
        {
            if (string.IsNullOrEmpty(glsl))
            {
                return null;
            }

            Silk.NET.Shaderc.Shaderc api = Silk.NET.Shaderc.Shaderc.GetApi();
            Silk.NET.Shaderc.Compiler* compiler = api.CompilerInitialize();
            Silk.NET.Shaderc.CompileOptions* options = api.CompileOptionsInitialize();

            try
            {
                api.CompileOptionsSetSourceLanguage(options, Silk.NET.Shaderc.SourceLanguage.Glsl);
                api.CompileOptionsSetTargetSpirv(options, Silk.NET.Shaderc.SpirvVersion.Shaderc15);
                api.CompileOptionsSetTargetEnv(options, Silk.NET.Shaderc.TargetEnv.Vulkan, Silk.NET.Vulkan.Vk.Version12);

                Silk.NET.Shaderc.ShaderKind kind = stage switch
                {
                    ShaderStage.Vertex => Silk.NET.Shaderc.ShaderKind.GlslVertexShader,
                    ShaderStage.Fragment => Silk.NET.Shaderc.ShaderKind.GlslFragmentShader,
                    ShaderStage.Compute => Silk.NET.Shaderc.ShaderKind.GlslComputeShader,
                    _ => Silk.NET.Shaderc.ShaderKind.GlslVertexShader,
                };

                Silk.NET.Shaderc.CompilationResult* result = api.CompileIntoSpv(
                    compiler,
                    glsl,
                    (nuint)glsl.Length,
                    kind,
                    "Ryu",
                    "main",
                    options);

                Silk.NET.Shaderc.CompilationStatus status = api.ResultGetCompilationStatus(result);

                if (status != Silk.NET.Shaderc.CompilationStatus.Success)
                {
                    return null;
                }

                Span<byte> spirvBytes = new(api.ResultGetBytes(result), (int)api.ResultGetLength(result));

                // SPIR-V must be 4-byte aligned; Shaderc already aligns, but pad defensively.
                byte[] code = new byte[(spirvBytes.Length + 3) & ~3];
                spirvBytes.CopyTo(code.AsSpan()[..spirvBytes.Length]);
                return code;
            }
            finally
            {
                api.CompilerRelease(compiler);
                api.CompileOptionsRelease(options);
            }
        }
    }
}
