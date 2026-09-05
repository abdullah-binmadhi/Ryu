using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace Ryujinx.Graphics.Metal.Interop
{
    /// <summary>
    /// Minimal P/Invoke surface for the SPIRV-Cross C API (spvc_*), used to translate
    /// the shader translator's SPIR-V output into MSL for the native Metal backend.
    /// All handle types are opaque pointers, so the ABI is trivially safe.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public static unsafe partial class SpirvCross
    {
        private const string Library = "libspirv-cross.dylib";

        private const int BackendMsl = 3;               // SPVC_BACKEND_MSL
        private const int CaptureModeTakeOwnership = 0; // SPVC_CAPTURE_MODE_TAKE_OWNERSHIP

        // Metal 3.0 version for SPIRV-Cross (enables invariant position, subgroups, etc)
        private const uint MslVersion = 30000;

        // SPVC_RESOURCE_TYPE_* — must match the spvc_resource_type enum in spirv_cross_c.h
        private const int ResourceTypeUniformBuffer = 1;    // SPVC_RESOURCE_TYPE_UNIFORM_BUFFER
        private const int ResourceTypeStorageBuffer = 2;    // SPVC_RESOURCE_TYPE_STORAGE_BUFFER
        private const int ResourceTypeStorageImage = 6;     // SPVC_RESOURCE_TYPE_STORAGE_IMAGE
        private const int ResourceTypeSampledImage = 7;     // SPVC_RESOURCE_TYPE_SAMPLED_IMAGE
        private const int ResourceTypeSeparateImage = 10;   // SPVC_RESOURCE_TYPE_SEPARATE_IMAGE
        private const int ResourceTypeSeparateSamplers = 11;// SPVC_RESOURCE_TYPE_SEPARATE_SAMPLERS

        [LibraryImport(Library)]
        private static partial int spvc_context_create(out nint context);

        [LibraryImport(Library)]
        private static partial void spvc_context_destroy(nint context);

        [LibraryImport(Library)]
        private static partial int spvc_context_parse_spirv(nint context, uint* spirv, nuint wordCount, out nint parsedIr);

        [LibraryImport(Library)]
        private static partial int spvc_context_create_compiler(nint context, int backend, nint parsedIr, int captureMode, out nint compiler);

        [LibraryImport(Library)]
        private static partial int spvc_compiler_compile(nint compiler, out byte* source);

        [LibraryImport(Library)]
        private static partial byte* spvc_context_get_last_error_string(nint context);

        [LibraryImport(Library)]
        private static partial int spvc_compiler_create_shader_resources(nint compiler, out nint resources);

        [LibraryImport(Library)]
        private static partial int spvc_resources_get_resource_list_for_type(nint resources, int type, out SpvcReflectedResource* list, out nuint size);

        [LibraryImport(Library)]
        private static partial uint spvc_compiler_msl_get_automatic_resource_binding(nint compiler, uint id);

        [LibraryImport(Library)]
        private static partial uint spvc_compiler_msl_get_automatic_resource_binding_secondary(nint compiler, uint id);

        [LibraryImport(Library)]
        private static partial uint spvc_compiler_get_decoration(nint compiler, uint id, int decoration);

        // SPIR-V binding / descriptor-set decorations (SpvDecoration enum).
        private const int DecorationBinding = 33;
        private const int DecorationDescriptorSet = 34;


        [LibraryImport(Library)]
        private static partial int spvc_compiler_create_compiler_options(nint compiler, out nint options);

        [LibraryImport(Library)]
        private static partial int spvc_compiler_options_set_uint(nint options, int option, uint value);

        [LibraryImport(Library)]
        private static partial int spvc_compiler_install_compiler_options(nint compiler, nint options);

        /// <summary>
        /// The kind of a reflected MSL resource, used to pick the right index space
        /// (buffer vs texture vs sampler) when binding at draw time.
        /// </summary>
        public enum MslResourceKind
        {
            UniformBuffer,
            StorageBuffer,
            Texture,
            StorageImage,
            Sampler,
        }

        /// <summary>
        /// Sentinel value indicating that a texture's sampler has been converted to a
        /// file-scope constexpr sampler in MSL (because its index >= 16) and must not
        /// be bound into the MTL4ArgumentTable.
        /// </summary>
        public const uint ConstexprSampler = 0xFFFF_FFFEu;

        /// <summary>
        /// Maps a SPIR-V (set, binding) resource to its auto-assigned MSL index.
        /// For combined image samplers <see cref="MslSamplerIndex"/> carries the
        /// sampler half (texture half is in <see cref="MslIndex"/>); otherwise
        /// <see cref="MslSamplerIndex"/> is <see cref="uint.MaxValue"/>.
        /// </summary>
        public readonly struct MslBindingInfo
        {
            public readonly MslResourceKind Kind;
            public readonly uint Set;
            public readonly uint Binding;
            public readonly uint MslIndex;
            public readonly uint MslSamplerIndex;

            public MslBindingInfo(MslResourceKind kind, uint set, uint binding, uint mslIndex, uint mslSamplerIndex)
            {
                Kind = kind;
                Set = set;
                Binding = binding;
                MslIndex = mslIndex;
                MslSamplerIndex = mslSamplerIndex;
            }
        }

        /// <summary>
        /// Translates a SPIR-V module to Metal Shading Language and reflects the
        /// auto-assigned MSL resource bindings for each (set, binding).
        /// </summary>
        /// <param name="spirv">SPIR-V words (little-endian uint32 module)</param>
        /// <param name="bindings">Reflected MSL resource bindings (empty on failure)</param>
        /// <param name="error">Human-readable error on failure, empty on success</param>
        /// <returns>The MSL source, or null on failure</returns>
        public static string SpirvToMsl(ReadOnlySpan<uint> spirv, out List<MslBindingInfo> bindings, out string error)
        {
            bindings = new List<MslBindingInfo>();
            error = string.Empty;

            if (spirv.Length == 0)
            {
                error = "empty SPIR-V module";
                return null;
            }

            int result = spvc_context_create(out nint context);

            if (result != 0)
            {
                error = "spvc_context_create failed";
                return null;
            }

            try
            {
                nint parsedIr;

                fixed (uint* pSpirv = spirv)
                {
                    // spvc_context_parse_spirv takes the module size in 32-bit words.
                    result = spvc_context_parse_spirv(context, pSpirv, (nuint)spirv.Length, out parsedIr);
                }

                if (result != 0)
                {
                    error = $"[parse {result}] {GetError(context)}";
                    return null;
                }

                result = spvc_context_create_compiler(context, BackendMsl, parsedIr, CaptureModeTakeOwnership, out nint compiler);

                if (result != 0)
                {
                    error = $"[create_compiler {result}] {GetError(context)}";
                    return null;
                }

                if (spvc_compiler_create_compiler_options(compiler, out nint options) == 0)
                {
                    spvc_compiler_options_set_uint(options, 134217745, MslVersion); // SPVC_COMPILER_OPTION_MSL_VERSION = 134217745
                    spvc_compiler_install_compiler_options(compiler, options);
                }

                result = spvc_compiler_compile(compiler, out byte* source);

                if (result != 0 || source == null)
                {
                    error = $"[compile {result}] {GetError(context)}";
                    return null;
                }

                string msl = Marshal.PtrToStringUTF8((nint)source);

                if (msl == null)
                {
                    error = "SPIRV-Cross returned a null MSL source";
                    return null;
                }

                // MTL4 argument tables expose at most 16 sampler-state slots. The
                // current translation ABI does not provide host sampler descriptors
                // at this stage, so preserve shader validity by folding out-of-range
                // direct sampler attributes into the last legal slot. Reflection is
                // compacted identically below before MetalPipeline binds the table.
                msl = CompactSamplerAttributes(msl);

                CollectBindings(compiler, bindings);

                return msl;
            }
            finally
            {
                spvc_context_destroy(context);
            }
        }

        /// <summary>
        /// Translates a SPIR-V module to Metal Shading Language (binding reflection omitted).
        /// </summary>
        public static string SpirvToMsl(ReadOnlySpan<uint> spirv, out string error)
        {
            return SpirvToMsl(spirv, out _, out error);
        }

        private static void CollectBindings(nint compiler, List<MslBindingInfo> output)
        {
            if (spvc_compiler_create_shader_resources(compiler, out nint resources) != 0 || resources == nint.Zero)
            {
                return;
            }

            CollectResourceType(compiler, resources, ResourceTypeUniformBuffer, MslResourceKind.UniformBuffer, output);
            CollectResourceType(compiler, resources, ResourceTypeStorageBuffer, MslResourceKind.StorageBuffer, output);
            CollectResourceType(compiler, resources, ResourceTypeSampledImage, MslResourceKind.Texture, output);
            CollectResourceType(compiler, resources, ResourceTypeSeparateImage, MslResourceKind.Texture, output);
            CollectResourceType(compiler, resources, ResourceTypeStorageImage, MslResourceKind.StorageImage, output);
            CollectResourceType(compiler, resources, ResourceTypeSeparateSamplers, MslResourceKind.Sampler, output);
        }

        private static void CollectResourceType(
            nint compiler,
            nint resources,
            int resourceType,
            MslResourceKind kind,
            List<MslBindingInfo> output)
        {
            if (spvc_resources_get_resource_list_for_type(resources, resourceType, out SpvcReflectedResource* list, out nuint size) != 0)
            {
                return;
            }

            for (nuint i = 0; i < size; i++)
            {
                SpvcReflectedResource resource = list[i];

                uint mslIndex = spvc_compiler_msl_get_automatic_resource_binding(compiler, resource.Id);

                if (mslIndex == uint.MaxValue)
                {
                    continue;
                }

                uint samplerIndex = uint.MaxValue;

                if (kind == MslResourceKind.Texture)
                {
                    samplerIndex = spvc_compiler_msl_get_automatic_resource_binding_secondary(compiler, resource.Id);
                }

                // spvc_reflected_resource only carries (id, base_type_id, type_id, name);
                // the SPIR-V set/binding must be read via decorations.
                uint binding = spvc_compiler_get_decoration(compiler, resource.Id, DecorationBinding);
                uint set = spvc_compiler_get_decoration(compiler, resource.Id, DecorationDescriptorSet);

                if (samplerIndex >= 16 && samplerIndex != uint.MaxValue)
                {
                    samplerIndex = ConstexprSampler;
                }

                output.Add(new MslBindingInfo(kind, set, binding, mslIndex, samplerIndex));
            }
        }

        private static readonly Regex SamplerParamRegex = new(
            @"(?:const\s+)?(?:metal::)?sampler\s+(?<name>[A-Za-z0-9_]+)\s*\[\[\s*sampler\(\s*(?<idx>\d+)\s*\)\s*\]\]",
            RegexOptions.Compiled);

        /// <summary>
        /// Scans MSL for entry-point sampler parameters with binding index >= 16 (which exceed Metal's 16-sampler limit).
        /// Removes them from the entry-point parameter list and injects corresponding file-scope constexpr sampler declarations.
        /// </summary>
        public static string CompactSamplerAttributes(string msl)
        {
            var matches = SamplerParamRegex.Matches(msl);
            var overflowSamplers = new List<(string Name, int Start, int Length)>();

            foreach (Match m in matches)
            {
                if (uint.TryParse(m.Groups["idx"].Value, out uint idx) && idx >= 16)
                {
                    overflowSamplers.Add((m.Groups["name"].Value, m.Index, m.Length));
                }
            }

            if (overflowSamplers.Count == 0)
            {
                return msl;
            }

            var constexprSamplers = new List<string>();

            // Process in reverse order so character offsets preceding each match remain unchanged.
            for (int i = overflowSamplers.Count - 1; i >= 0; i--)
            {
                var (name, start, length) = overflowSamplers[i];
                if (!constexprSamplers.Contains(name))
                {
                    constexprSamplers.Add(name);
                }

                int removeStart = start;
                int removeLength = length;

                // Check if preceded by a comma
                int back = start - 1;
                while (back >= 0 && char.IsWhiteSpace(msl[back]))
                {
                    back--;
                }

                if (back >= 0 && msl[back] == ',')
                {
                    removeStart = back;
                    removeLength = (start + length) - back;
                }
                else
                {
                    // Check if followed by a comma
                    int fwd = start + length;
                    while (fwd < msl.Length && char.IsWhiteSpace(msl[fwd]))
                    {
                        fwd++;
                    }

                    if (fwd < msl.Length && msl[fwd] == ',')
                    {
                        removeLength = (fwd + 1) - start;
                    }
                }

                msl = msl.Remove(removeStart, removeLength);
            }

            var sb = new StringBuilder();
            foreach (string name in constexprSamplers)
            {
                if (!msl.Contains($"constexpr sampler {name}"))
                {
                    sb.AppendLine($"constexpr sampler {name}(coord::normalized, filter::linear, mip_filter::linear, address::clamp_to_edge);");
                }
            }

            int insertIdx = msl.IndexOf("using namespace metal;", StringComparison.Ordinal);
            if (insertIdx >= 0)
            {
                insertIdx = msl.IndexOf('\n', insertIdx);
                if (insertIdx >= 0)
                {
                    insertIdx += 1;
                    msl = msl.Insert(insertIdx, "\n" + sb.ToString());
                }
                else
                {
                    msl = sb.ToString() + "\n" + msl;
                }
            }
            else
            {
                msl = sb.ToString() + "\n" + msl;
            }

            return msl;
        }

        private static string GetError(nint context)
        {
            byte* error = spvc_context_get_last_error_string(context);

            return error != null ? Marshal.PtrToStringUTF8((nint)error) : "unknown SPIRV-Cross error";
        }

        /// <summary>
        /// spvc_reflected_resource — id, base_type_id, type_id + pointer (24 bytes on arm64).
        /// Note: the C struct carries no binding/set; those are read via
        /// spvc_compiler_get_decoration with SPV_DECORATION_BINDING/DESCRIPTOR_SET.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct SpvcReflectedResource
        {
            public uint Id;
            public uint BaseTypeId;
            public uint TypeId;
            public byte* Name;
        }
    }
}
