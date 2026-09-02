using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Metal.Interop;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    public class MetalTexture : ITexture, IDisposable
    {
        // Shared format-converting blit pass. The Metal backend has a single device +
        // command queue (owned by the MetalRenderer singleton), so one pass serves all
        // textures. Lazily created on first mismatch to avoid work when every copy is
        // format-identical (the overwhelmingly common case).
        private static MetalFormatBlit _formatBlit;
        private static readonly object _formatBlitLock = new();

        private readonly nint _device;
        private readonly nint _commandQueue;
        private readonly Action _flushPendingWork;
        private readonly TextureCreateInfo _info;
        private nint _mtlTexture;
        private bool _disposed;

        private static MetalFormatBlit GetFormatBlit(nint device, nint commandQueue)
        {
            MetalFormatBlit blit = _formatBlit;
            if (blit == null)
            {
                lock (_formatBlitLock)
                {
                    blit = _formatBlit ??= new MetalFormatBlit(device, commandQueue);
                }
            }

            return blit;
        }

        public int Width => _info.Width;
        public int Height => _info.Height;
        public Format Format => _info.Format;
        public float ScaleFactor => 1.0f;

        /// <summary>
        /// The underlying MTLTexture object, or 0 when the format/target is unsupported.
        /// </summary>
        public nint TextureHandle => _mtlTexture;

        public MetalTexture(nint device, nint commandQueue, TextureCreateInfo info)
            : this(device, commandQueue, info, null)
        {
        }

        public MetalTexture(MetalRenderer renderer, TextureCreateInfo info)
            : this(renderer.DeviceHandle, renderer.CommandQueueHandle, info, renderer.FlushBeforePresent)
        {
        }

        private MetalTexture(nint device, nint commandQueue, TextureCreateInfo info, Action flushPendingWork)
        {
            _device = device;
            _commandQueue = commandQueue;
            _info = info;
            _flushPendingWork = flushPendingWork;

            AllocateTexture();
        }

        public MetalTexture(nint device, nint commandQueue, TextureCreateInfo info, nint mtlTexture)
            : this(device, commandQueue, info, mtlTexture, null)
        {
        }

        private MetalTexture(nint device, nint commandQueue, TextureCreateInfo info, nint mtlTexture, Action flushPendingWork)
        {
            _device = device;
            _commandQueue = commandQueue;
            _info = info;
            _flushPendingWork = flushPendingWork;
            _mtlTexture = mtlTexture;

            if (_mtlTexture != nint.Zero)
            {
                MetalBindings.Retain(_mtlTexture);
            }
        }

        private void AllocateTexture()
        {
            if (_info.IsCompressed)
            {
                return; // compressed formats not supported yet
            }

            ulong pixelFormat = MetalFormats.ToMtlPixelFormat(_info.Format);

            if (pixelFormat == 0)
            {
                return;
            }

            // Map GAL target → MTLTextureType + valid arrayLength/depth values.
            ulong textureType;
            nuint arrayLength = 1;
            nuint depth = 1;

            switch (_info.Target)
            {
                case Target.Texture2D:
                    textureType = MetalBindings.MTLTextureType2D;
                    break;
                case Target.Texture2DArray:
                    textureType = MetalBindings.MTLTextureType2DArray;
                    arrayLength = (nuint)Math.Max(1, _info.Depth);
                    break;
                case Target.Cubemap:
                    textureType = MetalBindings.MTLTextureTypeCube;
                    arrayLength = 1;
                    break;
                case Target.CubemapArray:
                    textureType = MetalBindings.MTLTextureTypeCubeArray;
                    arrayLength = (nuint)Math.Max(1, _info.Depth);
                    break;
                case Target.Texture3D:
                    textureType = MetalBindings.MTLTextureType3D;
                    depth = (nuint)Math.Max(1, _info.Depth);
                    arrayLength = 1;
                    break;
                default:
                    return; // multisample / buffer etc. unsupported for now
            }

            nint descriptorCls = MetalBindings.objc_getClass("MTLTextureDescriptor");
            nint descriptor;
            bool descriptorOwned = false;

            if (textureType == MetalBindings.MTLTextureType2D || textureType == MetalBindings.MTLTextureType2DMultisample)
            {
                // For 2D textures, use the designated factory method to avoid uninitialized state bugs on Apple Silicon.
                // Returns an autoreleased (+0) object; do NOT release.
                nint selTexture2DDesc = MetalBindings.sel_registerName("texture2DDescriptorWithPixelFormat:width:height:mipmapped:");
                descriptor = MetalBindings.objc_msgSend(
                    descriptorCls,
                    selTexture2DDesc,
                    (nuint)pixelFormat,
                    (nuint)Math.Max(1, _info.Width),
                    (nuint)Math.Max(1, _info.Height),
                    (byte)(_info.Levels > 1 ? 1 : 0));
            }
            else if (textureType == MetalBindings.MTLTextureTypeCube)
            {
                // Autoreleased (+0); do NOT release.
                nint selTextureCubeDesc = MetalBindings.sel_registerName("textureCubeDescriptorWithPixelFormat:size:mipmapped:");
                descriptor = MetalBindings.objc_msgSend(
                    descriptorCls,
                    selTextureCubeDesc,
                    (nuint)pixelFormat,
                    (nuint)Math.Max(1, _info.Width),
                    (byte)(_info.Levels > 1 ? 1 : 0));
            }
            else
            {
                descriptor = MetalBindings.objc_msgSend(descriptorCls, MetalBindings.SelNew);
                descriptorOwned = true;
                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetPixelFormat, (nuint)pixelFormat);
                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetTextureType, (nuint)textureType);
                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetWidth, (nuint)Math.Max(1, _info.Width));
                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetHeight, (nuint)Math.Max(1, _info.Height));
            }

            MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetMipmapLevelCount, (nuint)Math.Max(1, _info.Levels));
            MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetSampleCount, (nuint)Math.Max(1, _info.Samples));

            // Use Shared storage mode (0) because Apple Silicon does not support Managed mode,
            // and Private mode prevents us from using replaceRegion: and getBytes: for CPU data transfer.
            MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetStorageMode, 0);

            if (textureType == MetalBindings.MTLTextureType3D)
            {
                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetDepth, (nuint)Math.Max(1, depth));
            }
            else if (textureType is MetalBindings.MTLTextureType2DArray or MetalBindings.MTLTextureTypeCube or MetalBindings.MTLTextureTypeCubeArray)
            {
                if (textureType == MetalBindings.MTLTextureTypeCube)
                {
                    // Metal expects arrayLength = 1 for a single Cube (which implicitly has 6 faces).
                    arrayLength = 1;
                }
                else if (textureType == MetalBindings.MTLTextureTypeCubeArray)
                {
                    // For Cube Arrays, Metal expects the number of Cubes, not faces. Ryujinx passes the number of faces.
                    arrayLength = Math.Max(1, arrayLength / 6);
                }
                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetArrayLength, arrayLength);
            }

            MetalBindings.objc_msgSend_void(
                descriptor,
                MetalBindings.SelSetUsage,
                (nuint)(MetalBindings.MTLTextureUsageShaderRead | MetalBindings.MTLTextureUsageRenderTarget));

            _mtlTexture = MetalBindings.objc_msgSend(_device, MetalBindings.SelNewTextureWithDescriptor, descriptor);
            _mtlTexture = MetalBindings.Retain(_mtlTexture);

            if (descriptorOwned)
            {
                MetalBindings.Release(descriptor);
            }
        }

        private bool UsesSlice => _info.Target is Target.Texture2DArray or Target.Cubemap or Target.CubemapArray;

        public void CopyTo(ITexture destination, int firstLayer, int firstLevel)
        {
            if (destination is not MetalTexture dstMetal)
            {
                return;
            }

            int levels = Math.Min(_info.Levels, Math.Max(0, dstMetal._info.Levels - firstLevel));
            int layers = Math.Min(_info.GetLayers(), Math.Max(0, dstMetal._info.GetLayers() - firstLayer));

            for (int level = 0; level < levels; level++)
            {
                int width = Math.Min(MipSize(Width, level), MipSize(dstMetal.Width, firstLevel + level));
                int height = Math.Min(MipSize(Height, level), MipSize(dstMetal.Height, firstLevel + level));

                for (int layer = 0; layer < layers; layer++)
                {
                    CopyRegion(dstMetal, layer, firstLayer + layer, level, firstLevel + level, 0, 0, 0, 0, width, height);
                }
            }
        }

        public void CopyTo(ITexture destination, int srcLayer, int dstLayer, int srcLevel, int dstLevel)
        {
            if (destination is not MetalTexture dstMetal)
            {
                return;
            }

            int width = Math.Min(MipSize(Width, srcLevel), MipSize(dstMetal.Width, dstLevel));
            int height = Math.Min(MipSize(Height, srcLevel), MipSize(dstMetal.Height, dstLevel));
            CopyRegion(dstMetal, srcLayer, dstLayer, srcLevel, dstLevel, 0, 0, 0, 0, width, height);
        }

        public void CopyTo(ITexture destination, Extents2D srcRegion, Extents2D dstRegion, bool linearFilter)
        {
            if (destination is not MetalTexture dstMetal)
            {
                return;
            }

            int srcWidth = srcRegion.X2 - srcRegion.X1;
            int srcHeight = srcRegion.Y2 - srcRegion.Y1;
            int dstWidth = dstRegion.X2 - dstRegion.X1;
            int dstHeight = dstRegion.Y2 - dstRegion.Y1;

            // Metal blits cannot scale. The current presentation path uses matching
            // extents; scaled copies need the helper-shader path.
            if (srcWidth <= 0 || srcHeight <= 0 || srcWidth != dstWidth || srcHeight != dstHeight)
            {
                return;
            }

            CopyRegion(dstMetal, 0, 0, 0, 0, srcRegion.X1, srcRegion.Y1, dstRegion.X1, dstRegion.Y1, srcWidth, srcHeight);
        }

        private static int MipSize(int size, int level) => Math.Max(1, size >> level);

        private unsafe void CopyRegion(MetalTexture destination, int srcLayer, int dstLayer, int srcLevel, int dstLevel, int srcX, int srcY, int dstX, int dstY, int width, int height)
        {
            if (_mtlTexture == nint.Zero || destination.TextureHandle == nint.Zero || _commandQueue == nint.Zero ||
                srcLayer < 0 || dstLayer < 0 || srcLevel < 0 || dstLevel < 0 || width <= 0 || height <= 0)
            {
                return;
            }

            _flushPendingWork?.Invoke();

            ulong srcPixelFormat = MetalFormats.ToMtlPixelFormat(_info.Format);
            ulong dstPixelFormat = MetalFormats.ToMtlPixelFormat(destination._info.Format);

            // Metal's MTLBlitCommandEncoder copyFromTexture:... requires source and
            // destination to share the SAME pixel format, otherwise it silently no-ops
            // (the framebuffer "magenta screen" symptom when the present surface is
            // left all-zero). Route mismatched copies through the format-converting
            // render pass (or a CPU preserve-copy for slice/mip cases).
            if (dstPixelFormat == 0)
            {
                return;
            }

            if (srcPixelFormat != dstPixelFormat)
            {
                if (srcLayer == 0 && dstLayer == 0 && srcLevel == 0 && dstLevel == 0)
                {
                    GetFormatBlit(_device, _commandQueue).Copy(this, destination, srcX, srcY, dstX, dstY, width, height);
                }
                else
                {
                    // GPU blit handles slice 0 / mip 0 only; use a CPU byte-preserving
                    // copy for other slice/level combos (rare; only for identical
                    // bytes-per-pixel so channel data is at least preserved).
                    CopyRegionCpu(destination, srcLayer, dstLayer, srcLevel, dstLevel, srcX, srcY, dstX, dstY, width, height);
                }

                return;
            }

            nint commandBuffer = MetalBindings.Retain(MetalBindings.objc_msgSend(_commandQueue, MetalBindings.SelCommandBuffer));
            if (commandBuffer == nint.Zero)
            {
                return;
            }

            nint blitEncoder = MetalBindings.objc_msgSend(commandBuffer, MetalBindings.SelBlitCommandEncoder);
            if (blitEncoder == nint.Zero)
            {
                MetalBindings.Release(commandBuffer);
                return;
            }

            MTLOrigin srcOrigin = new((nuint)srcX, (nuint)srcY, 0);
            MTLSize srcSize = new((nuint)width, (nuint)height, 1);
            MTLOrigin dstOrigin = new((nuint)dstX, (nuint)dstY, 0);

            MetalBindings.objc_msgSend_void_blitCopy(
                blitEncoder,
                MetalBindings.SelCopyFromTextureSourceSliceSourceLevelSourceOriginSourceSizeToTextureDestinationSliceDestinationLevelDestinationOrigin,
                _mtlTexture, (nuint)srcLayer, (nuint)srcLevel, srcOrigin, srcSize,
                destination.TextureHandle, (nuint)dstLayer, (nuint)dstLevel, dstOrigin);

            MetalBindings.objc_msgSend_void(blitEncoder, MetalBindings.SelEndEncoding);
            MetalBindings.objc_msgSend_void(commandBuffer, MetalBindings.SelCommit);
            MetalBindings.Release(commandBuffer);
        }

        private void CopyRegionCpu(MetalTexture destination, int srcLayer, int dstLayer, int srcLevel, int dstLevel, int srcX, int srcY, int dstX, int dstY, int width, int height)
        {
            if (_info.IsCompressed || destination._info.IsCompressed || _info.BytesPerPixel != destination._info.BytesPerPixel)
            {
                return;
            }

            using PinnedSpan<byte> srcData = GetData(srcLayer, srcLevel);
            ReadOnlySpan<byte> srcSpan = srcData.Get();
            int bytesPerPixel = _info.BytesPerPixel;
            int srcLevelWidth = Math.Max(1, Width >> srcLevel);

            for (int y = 0; y < height; y++)
            {
                int srcOffset = ((srcY + y) * srcLevelWidth + srcX) * bytesPerPixel;
                MemoryOwner<byte> row = MemoryOwner<byte>.Rent(width * bytesPerPixel);
                srcSpan.Slice(srcOffset, width * bytesPerPixel).CopyTo(row.Span);
                destination.SetData(row, dstLayer, dstLevel, new Rectangle<int>(dstX, dstY + y, width, 1));
            }
        }

        public void CopyTo(BufferRange range, int layer, int level, int stride) { }

        public ITexture CreateView(TextureCreateInfo info, int firstLayer, int firstLevel)
        {
            ulong pixelFormat = MetalFormats.ToMtlPixelFormat(info.Format);
            if (pixelFormat == 0 || _mtlTexture == nint.Zero)
            {
                return new MetalTexture(_device, _commandQueue, info, _flushPendingWork);
            }

            nint viewHandle = MetalBindings.objc_msgSend(_mtlTexture, MetalBindings.SelNewTextureViewWithPixelFormat, pixelFormat);

            if (viewHandle == nint.Zero)
            {
                return new MetalTexture(_device, _commandQueue, info, _flushPendingWork);
            }

            MetalTexture view = new MetalTexture(_device, _commandQueue, info, viewHandle, _flushPendingWork);
            MetalBindings.Release(viewHandle);
            return view;
        }

        public PinnedSpan<byte> GetData() => GetData(0, 0);

        public unsafe PinnedSpan<byte> GetData(int layer, int level)
        {
            if (_mtlTexture == nint.Zero || _disposed)
            {
                return PinnedSpan<byte>.UnsafeFromSpan(ReadOnlySpan<byte>.Empty);
            }

            int width = Math.Max(1, _info.Width >> level);
            int height = Math.Max(1, _info.Height >> level);
            int bytesPerRow = width * _info.BytesPerPixel;
            int size = bytesPerRow * height;

            byte[] buffer = new byte[size];
            GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);

            try
            {
                MTLRegion region = new(0, 0, 0, (nuint)width, (nuint)height, 1);

                if (UsesSlice)
                {
                    // Array/cube readback: getBytes:bytesPerRow:bytesPerImage:fromRegion:mipmapLevel:slice:
                    MetalBindings.objc_msgSend_void(
                        _mtlTexture,
                        MetalBindings.SelGetBytesBytesPerRowBytesPerImageFromRegionMipmapLevelSlice,
                        (void*)handle.AddrOfPinnedObject(),
                        (nuint)bytesPerRow,
                        (nuint)(bytesPerRow * height),
                        &region,
                        (nuint)level,
                        (nuint)layer);
                }
                else
                {
                    // 2D readback: getBytes:bytesPerRow:fromRegion:mipmapLevel: (the bytesPerImage:
                    // variant is not implemented on the 2D texture class on Apple Silicon).
                    MetalBindings.objc_msgSend_void(
                        _mtlTexture,
                        MetalBindings.SelGetBytesBytesPerRowFromRegionMipmapLevel,
                        (void*)handle.AddrOfPinnedObject(),
                        (nuint)bytesPerRow,
                        &region,
                        (nuint)level);
                }

                return new PinnedSpan<byte>((void*)handle.AddrOfPinnedObject(), size, () => handle.Free());
            }
            catch
            {
                handle.Free();
                throw;
            }
        }

        public void SetData(MemoryOwner<byte> data) => SetData(data, 0, 0);

        public void SetData(MemoryOwner<byte> data, int layer, int level)
        {
            SetData(data, layer, level, new Rectangle<int>(0, 0, _info.Width, _info.Height));
        }

        public unsafe void SetData(MemoryOwner<byte> data, int layer, int level, Rectangle<int> region)
        {
            try
            {
                if (_mtlTexture != nint.Zero && !_disposed && region.Width > 0 && region.Height > 0)
                {
                    MTLRegion mtlRegion = new((nuint)region.X, (nuint)region.Y, 0, (nuint)region.Width, (nuint)region.Height, 1);
                    int bytesPerRow = region.Width * _info.BytesPerPixel;

                    fixed (byte* p = data.Span)
                    {
                        if (UsesSlice)
                        {
                            // Array/cube upload: replaceRegion:mipmapLevel:slice:withBytes:bytesPerRow:bytesPerImage:
                            MetalBindings.objc_msgSend_void(
                                _mtlTexture,
                                MetalBindings.SelReplaceRegionMipmapLevelSliceWithBytesBytesPerRowBytesPerImage,
                                &mtlRegion,
                                (nuint)level,
                                (nuint)layer,
                                p,
                                (nuint)bytesPerRow,
                                (nuint)(bytesPerRow * region.Height));
                        }
                        else
                        {
                            MetalBindings.objc_msgSend_void(
                                _mtlTexture,
                                MetalBindings.SelReplaceRegionMipmapLevelWithBytesBytesPerRow,
                                &mtlRegion,
                                (nuint)level,
                                p,
                                (nuint)bytesPerRow);
                        }
                    }
                }
            }
            finally
            {
                data.Dispose();
            }
        }

        public void SetStorage(BufferRange buffer) { }

        public void Release()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;

                if (_mtlTexture != nint.Zero)
                {
                    MetalBindings.Release(_mtlTexture);
                    _mtlTexture = nint.Zero;
                }

                GC.SuppressFinalize(this);
            }
        }
    }

    [SupportedOSPlatform("macos")]
    public class MetalSampler : ISampler
    {
        private readonly nint _device;
        private readonly SamplerCreateInfo _info;
        private nint _samplerState;
        private bool _disposed;

        public nint SamplerState => _samplerState;

        public MetalSampler(nint device, SamplerCreateInfo info)
        {
            _device = device;
            _info = info;

            CreateSamplerState();
        }

        private void CreateSamplerState()
        {
            nint descriptor = MetalBindings.objc_msgSend(
                MetalBindings.objc_getClass("MTLSamplerDescriptor"),
                MetalBindings.SelNew);

            if (descriptor == nint.Zero)
            {
                return;
            }

            try
            {
                // Min/mag filter from MinFilter/MagFilter; address modes from WrapS/WrapT.
                ulong minFilter = _info.MinFilter == MinFilter.Linear
                    ? MetalBindings.MTLSamplerMinMagFilterLinear
                    : MetalBindings.MTLSamplerMinMagFilterNearest;

                ulong magFilter = _info.MagFilter == MagFilter.Linear
                    ? MetalBindings.MTLSamplerMinMagFilterLinear
                    : MetalBindings.MTLSamplerMinMagFilterNearest;

                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetMinFilter, (nuint)minFilter);
                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetMagFilter, (nuint)magFilter);

                ulong sAddress = ToAddressMode(_info.AddressU);
                ulong tAddress = ToAddressMode(_info.AddressV);

                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetSAddressMode, (nuint)sAddress);
                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetTAddressMode, (nuint)tAddress);

                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetMipFilter, (nuint)MetalBindings.MTLSamplerMipFilterLinear);

                if (_info.MaxAnisotropy > 1)
                {
                    MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetMaxAnisotropy, (nuint)_info.MaxAnisotropy);
                }

                _samplerState = MetalBindings.objc_msgSend(_device, MetalBindings.SelNewSamplerStateWithDescriptor, descriptor);
                _samplerState = MetalBindings.Retain(_samplerState);
            }
            finally
            {
                MetalBindings.Release(descriptor);
            }
        }

        private static ulong ToAddressMode(AddressMode mode)
        {
            return mode switch
            {
                AddressMode.ClampToEdge => MetalBindings.MTLTextureAddressModeClampToEdge,
                AddressMode.MirroredRepeat => MetalBindings.MTLTextureAddressModeMirrorRepeat,
                _ => MetalBindings.MTLTextureAddressModeRepeat,
            };
        }

        public void Release()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;

                if (_samplerState != nint.Zero)
                {
                    MetalBindings.Release(_samplerState);
                    _samplerState = nint.Zero;
                }

                GC.SuppressFinalize(this);
            }
        }
    }

    [SupportedOSPlatform("macos")]
    public class MetalImageArray : IImageArray
    {
        public MetalImageArray(int size) { }

        public void SetImages(int index, ITexture[] images) { }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }

    [SupportedOSPlatform("macos")]
    public class MetalTextureArray : ITextureArray
    {
        public MetalTextureArray(int size) { }

        public void SetTextures(int index, ITexture[] textures) { }

        public void SetSamplers(int index, ISampler[] samplers) { }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
