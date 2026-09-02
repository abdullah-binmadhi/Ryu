using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

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

        // SPVC_RESOURCE_TYPE_*
        private const int ResourceTypeUniformBuffer = 0;
        private const int ResourceTypeStorageBuffer = 1;
        private const int ResourceTypeStorageImage = 5;
        private const int ResourceTypeSampledImage = 6;
        private const int ResourceTypeSeparateImage = 7;
        private const int ResourceTypeSeparateSamplers = 8;

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

                output.Add(new MslBindingInfo(kind, set, binding, mslIndex, samplerIndex));
            }
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
