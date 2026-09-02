using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Metal.Interop;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Maps GAL <see cref="Format"/> to Metal pixel formats.
    /// Returns 0 for formats that are not yet supported by the native backend.
    /// </summary>
    public static class MetalFormats
    {
        public static ulong ToMtlPixelFormat(Format format)
        {
            return format switch
            {
                // ── R8 ──────────────────────────────────────────────────────
                Format.R8Unorm    => MetalBindings.MTLPixelFormatR8Unorm,
                Format.R8Snorm    => MetalBindings.MTLPixelFormatR8Snorm,
                Format.R8Uint     => MetalBindings.MTLPixelFormatR8Uint,
                Format.R8Sint     => MetalBindings.MTLPixelFormatR8Sint,
                // ── R8G8 ────────────────────────────────────────────────────
                Format.R8G8Unorm  => MetalBindings.MTLPixelFormatRG8Unorm,
                Format.R8G8Snorm  => MetalBindings.MTLPixelFormatRG8Snorm,
                Format.R8G8Uint   => MetalBindings.MTLPixelFormatRG8Uint,
                Format.R8G8Sint   => MetalBindings.MTLPixelFormatRG8Sint,
                // ── R8G8B8A8 ────────────────────────────────────────────────
                Format.R8G8B8A8Unorm => MetalBindings.MTLPixelFormatRGBA8Unorm,
                Format.R8G8B8A8Snorm => MetalBindings.MTLPixelFormatRGBA8Snorm,
                Format.R8G8B8A8Uint  => MetalBindings.MTLPixelFormatRGBA8Uint,
                Format.R8G8B8A8Sint  => MetalBindings.MTLPixelFormatRGBA8Sint,
                Format.R8G8B8A8Srgb  => MetalBindings.MTLPixelFormatRGBA8Srgb,
                // ── B8G8R8A8 ────────────────────────────────────────────────
                Format.B8G8R8A8Unorm => MetalBindings.MTLPixelFormatBGRA8Unorm,
                Format.B8G8R8A8Srgb  => MetalBindings.MTLPixelFormatBGRA8Srgb,
                // ── R16 ─────────────────────────────────────────────────────
                Format.R16Unorm   => MetalBindings.MTLPixelFormatR16Unorm,
                Format.R16Snorm   => MetalBindings.MTLPixelFormatR16Snorm,
                Format.R16Uint    => MetalBindings.MTLPixelFormatR16Uint,
                Format.R16Sint    => MetalBindings.MTLPixelFormatR16Sint,
                Format.R16Float   => MetalBindings.MTLPixelFormatR16Float,
                // ── R16G16 ──────────────────────────────────────────────────
                Format.R16G16Unorm => MetalBindings.MTLPixelFormatRG16Unorm,
                Format.R16G16Snorm => MetalBindings.MTLPixelFormatRG16Snorm,
                Format.R16G16Uint  => MetalBindings.MTLPixelFormatRG16Uint,
                Format.R16G16Sint  => MetalBindings.MTLPixelFormatRG16Sint,
                Format.R16G16Float => MetalBindings.MTLPixelFormatRG16Float,
                // ── R16G16B16A16 ─────────────────────────────────────────────
                Format.R16G16B16A16Unorm => MetalBindings.MTLPixelFormatRGBA16Unorm,
                Format.R16G16B16A16Snorm => MetalBindings.MTLPixelFormatRGBA16Snorm,
                Format.R16G16B16A16Uint  => MetalBindings.MTLPixelFormatRGBA16Uint,
                Format.R16G16B16A16Sint  => MetalBindings.MTLPixelFormatRGBA16Sint,
                Format.R16G16B16A16Float => MetalBindings.MTLPixelFormatRGBA16Float,
                // ── R32 ─────────────────────────────────────────────────────
                Format.R32Uint  => MetalBindings.MTLPixelFormatR32Uint,
                Format.R32Sint  => MetalBindings.MTLPixelFormatR32Sint,
                Format.R32Float => MetalBindings.MTLPixelFormatR32Float,
                // ── R32G32 ──────────────────────────────────────────────────
                Format.R32G32Uint  => MetalBindings.MTLPixelFormatRG32Uint,
                Format.R32G32Sint  => MetalBindings.MTLPixelFormatRG32Sint,
                Format.R32G32Float => MetalBindings.MTLPixelFormatRG32Float,
                // ── R32G32B32A32 ─────────────────────────────────────────────
                Format.R32G32B32A32Float => MetalBindings.MTLPixelFormatRGBA32Float,
                Format.R32G32B32A32Uint  => MetalBindings.MTLPixelFormatRGBA32Uint,
                Format.R32G32B32A32Sint  => MetalBindings.MTLPixelFormatRGBA32Sint,
                // ── Packed ──────────────────────────────────────────────────
                Format.R11G11B10Float  => MetalBindings.MTLPixelFormatRG11B10Float,
                Format.R9G9B9E5Float   => MetalBindings.MTLPixelFormatRGB9E5Float,
                Format.R10G10B10A2Unorm => MetalBindings.MTLPixelFormatRGB10A2Unorm,
                Format.R10G10B10A2Uint  => MetalBindings.MTLPixelFormatRGB10A2Uint,
                Format.B5G6R5Unorm     => MetalBindings.MTLPixelFormatB5G6R5Unorm,
                Format.B5G5R5A1Unorm   => MetalBindings.MTLPixelFormatB5G5R5A1Unorm,
                // ── Depth / Stencil ──────────────────────────────────────────
                Format.S8Uint           => MetalBindings.MTLPixelFormatStencil8,
                Format.D16Unorm         => MetalBindings.MTLPixelFormatDepth16Unorm,
                Format.D32Float         => MetalBindings.MTLPixelFormatDepth32Float,
                Format.S8UintD24Unorm
                or Format.D24UnormS8Uint => MetalBindings.MTLPixelFormatDepth32FloatStencil8,
                Format.D32FloatS8Uint    => MetalBindings.MTLPixelFormatDepth32FloatStencil8,
                // ── BC / ASTC compressed (read-only, not render targets) ─────
                Format.Bc1RgbaUnorm  => MetalBindings.MTLPixelFormatBC1_RGBA,
                Format.Bc1RgbaSrgb   => MetalBindings.MTLPixelFormatBC1_RGBA_sRGB,
                Format.Bc2Unorm      => MetalBindings.MTLPixelFormatBC2_RGBA,
                Format.Bc2Srgb       => MetalBindings.MTLPixelFormatBC2_RGBA_sRGB,
                Format.Bc3Unorm      => MetalBindings.MTLPixelFormatBC3_RGBA,
                Format.Bc3Srgb       => MetalBindings.MTLPixelFormatBC3_RGBA_sRGB,
                Format.Bc4Unorm      => MetalBindings.MTLPixelFormatBC4_RUnorm,
                Format.Bc4Snorm      => MetalBindings.MTLPixelFormatBC4_RSnorm,
                Format.Bc5Unorm      => MetalBindings.MTLPixelFormatBC5_RGUnorm,
                Format.Bc5Snorm      => MetalBindings.MTLPixelFormatBC5_RGSnorm,
                Format.Bc6HSfloat    => MetalBindings.MTLPixelFormatBC6H_RGBFloat,
                Format.Bc6HUfloat    => MetalBindings.MTLPixelFormatBC6H_RGBUfloat,
                Format.Bc7Unorm      => MetalBindings.MTLPixelFormatBC7_RGBAUnorm,
                Format.Bc7Srgb       => MetalBindings.MTLPixelFormatBC7_RGBAUnorm_sRGB,
                Format.Astc4x4Unorm  => MetalBindings.MTLPixelFormatASTC_4x4_LDR,
                Format.Astc4x4Srgb   => MetalBindings.MTLPixelFormatASTC_4x4_sRGB,
                Format.Astc5x4Unorm  => MetalBindings.MTLPixelFormatASTC_5x4_LDR,
                Format.Astc5x4Srgb   => MetalBindings.MTLPixelFormatASTC_5x4_sRGB,
                Format.Astc5x5Unorm  => MetalBindings.MTLPixelFormatASTC_5x5_LDR,
                Format.Astc5x5Srgb   => MetalBindings.MTLPixelFormatASTC_5x5_sRGB,
                Format.Astc6x5Unorm  => MetalBindings.MTLPixelFormatASTC_6x5_LDR,
                Format.Astc6x5Srgb   => MetalBindings.MTLPixelFormatASTC_6x5_sRGB,
                Format.Astc6x6Unorm  => MetalBindings.MTLPixelFormatASTC_6x6_LDR,
                Format.Astc6x6Srgb   => MetalBindings.MTLPixelFormatASTC_6x6_sRGB,
                Format.Astc8x5Unorm  => MetalBindings.MTLPixelFormatASTC_8x5_LDR,
                Format.Astc8x5Srgb   => MetalBindings.MTLPixelFormatASTC_8x5_sRGB,
                Format.Astc8x6Unorm  => MetalBindings.MTLPixelFormatASTC_8x6_LDR,
                Format.Astc8x6Srgb   => MetalBindings.MTLPixelFormatASTC_8x6_sRGB,
                Format.Astc8x8Unorm  => MetalBindings.MTLPixelFormatASTC_8x8_LDR,
                Format.Astc8x8Srgb   => MetalBindings.MTLPixelFormatASTC_8x8_sRGB,
                Format.Astc10x5Unorm => MetalBindings.MTLPixelFormatASTC_10x5_LDR,
                Format.Astc10x5Srgb  => MetalBindings.MTLPixelFormatASTC_10x5_sRGB,
                Format.Astc10x6Unorm => MetalBindings.MTLPixelFormatASTC_10x6_LDR,
                Format.Astc10x6Srgb  => MetalBindings.MTLPixelFormatASTC_10x6_sRGB,
                Format.Astc10x8Unorm => MetalBindings.MTLPixelFormatASTC_10x8_LDR,
                Format.Astc10x8Srgb  => MetalBindings.MTLPixelFormatASTC_10x8_sRGB,
                Format.Astc10x10Unorm => MetalBindings.MTLPixelFormatASTC_10x10_LDR,
                Format.Astc10x10Srgb  => MetalBindings.MTLPixelFormatASTC_10x10_sRGB,
                Format.Astc12x10Unorm => MetalBindings.MTLPixelFormatASTC_12x10_LDR,
                Format.Astc12x10Srgb  => MetalBindings.MTLPixelFormatASTC_12x10_sRGB,
                Format.Astc12x12Unorm => MetalBindings.MTLPixelFormatASTC_12x12_LDR,
                Format.Astc12x12Srgb  => MetalBindings.MTLPixelFormatASTC_12x12_sRGB,
                _ => 0, // unsupported
            };
        }

        /// <summary>
        /// Maps GAL <see cref="Format"/> to a Metal vertex attribute format.
        /// Returns <see cref="MetalBindings.MTLVertexFormatInvalid"/> (0) for unsupported formats.
        /// </summary>
        public static ulong ToMtlVertexFormat(Format format)
        {
            return format switch
            {
                Format.R32Float => MetalBindings.MTLVertexFormatFloat,
                Format.R32G32Float => MetalBindings.MTLVertexFormatFloat2,
                Format.R32G32B32Float => MetalBindings.MTLVertexFormatFloat3,
                Format.R32G32B32A32Float => MetalBindings.MTLVertexFormatFloat4,
                Format.R32Sint => MetalBindings.MTLVertexFormatInt,
                Format.R32G32Sint => MetalBindings.MTLVertexFormatInt2,
                Format.R32G32B32Sint => MetalBindings.MTLVertexFormatInt3,
                Format.R32G32B32A32Sint => MetalBindings.MTLVertexFormatInt4,
                Format.R32Uint => MetalBindings.MTLVertexFormatUInt,
                Format.R32G32Uint => MetalBindings.MTLVertexFormatUInt2,
                Format.R32G32B32Uint => MetalBindings.MTLVertexFormatUInt3,
                Format.R32G32B32A32Uint => MetalBindings.MTLVertexFormatUInt4,
                Format.R8G8B8A8Unorm => MetalBindings.MTLVertexFormatUChar4Normalized,
                Format.R8G8B8A8Sint => MetalBindings.MTLVertexFormatChar4,
                Format.R8G8B8A8Uint => MetalBindings.MTLVertexFormatUChar4,
                Format.R16G16Float => MetalBindings.MTLVertexFormatFloat16_2,
                Format.R16G16B16A16Float => MetalBindings.MTLVertexFormatFloat16_4,
                _ => MetalBindings.MTLVertexFormatInvalid,
            };
        }
    }
}
