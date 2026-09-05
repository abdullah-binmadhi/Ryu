using Ryujinx.Common;
using Ryujinx.Common.Logging;
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

        internal static MetalFormatBlit GetFormatBlit(nint device, nint commandQueue)
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

        private readonly MetalRenderer _renderer;

        public MetalRenderer Renderer => _renderer;

        public MetalTexture(nint device, nint commandQueue, TextureCreateInfo info)
            : this(null, device, commandQueue, info, null)
        {
        }

        public MetalTexture(nint device, nint commandQueue, TextureCreateInfo info, Action flushPendingWork)
            : this(null, device, commandQueue, info, flushPendingWork)
        {
        }

        public MetalTexture(MetalRenderer renderer, TextureCreateInfo info)
            : this(renderer, renderer.DeviceHandle, renderer.CommandQueueHandle, info, renderer.FlushBeforePresent)
        {
        }

        private MetalTexture(MetalRenderer renderer, nint device, nint commandQueue, TextureCreateInfo info, Action flushPendingWork)
        {
            _renderer = renderer;
            _device = device;
            _commandQueue = commandQueue;
            _info = info;
            _flushPendingWork = flushPendingWork;

            AllocateTexture();
        }

        public MetalTexture(nint device, nint commandQueue, TextureCreateInfo info, nint mtlTexture)
            : this(null, device, commandQueue, info, mtlTexture, null)
        {
        }

        public MetalTexture(nint device, nint commandQueue, TextureCreateInfo info, nint mtlTexture, Action flushPendingWork)
            : this(null, device, commandQueue, info, mtlTexture, flushPendingWork)
        {
        }

        private MetalTexture(MetalRenderer renderer, nint device, nint commandQueue, TextureCreateInfo info, nint mtlTexture, Action flushPendingWork)
        {
            _renderer = renderer;
            _device = device;
            _commandQueue = commandQueue;
            _info = info;
            _flushPendingWork = flushPendingWork;
            _mtlTexture = mtlTexture;

            if (_mtlTexture != nint.Zero)
            {
                MetalBindings.Retain(_mtlTexture);
                _renderer?.M4Queue.AddResidencyResource(_mtlTexture);
            }
        }

        private void AllocateTexture()
        {
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

            // Compressed formats (ASTC, BCn, ETC2) in Metal cannot be used as render targets or shader write.
            nuint usage = (nuint)MetalBindings.MTLTextureUsageShaderRead;
            if (!_info.IsCompressed)
            {
                usage |= (nuint)(MetalBindings.MTLTextureUsageShaderWrite | MetalBindings.MTLTextureUsageRenderTarget);
            }

            MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetUsage, usage);

            _mtlTexture = MetalBindings.objc_msgSend(_device, MetalBindings.SelNewTextureWithDescriptor, descriptor);
            _renderer?.M4Queue.AddResidencyResource(_mtlTexture);

            if (_info.Width >= 960 || _info.Height >= 540)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"[TEX_ALLOC] handle=0x{_mtlTexture:X} {_info.Width}x{_info.Height} {_info.Format} target={_info.Target} levels={_info.Levels} usage=0x{usage:X}");
            }

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

            if (srcWidth <= 0 || srcHeight <= 0 || dstWidth <= 0 || dstHeight <= 0)
            {
                return;
            }

            _flushPendingWork?.Invoke();
            dstMetal._flushPendingWork?.Invoke();

            // Same-size same-format fast blit path:
            if (srcWidth == dstWidth && srcHeight == dstHeight && srcRegion.X1 >= 0 && srcRegion.Y1 >= 0 && dstRegion.X1 >= 0 && dstRegion.Y1 >= 0 && _info.Format == dstMetal._info.Format)
            {
                CopyRegion(dstMetal, 0, 0, 0, 0, srcRegion.X1, srcRegion.Y1, dstRegion.X1, dstRegion.Y1, srcWidth, srcHeight);
            }
            else
            {
                // Scaled or format-converting GPU copy pass:
                GetFormatBlit(_device, _commandQueue).Copy(this, dstMetal, srcRegion, dstRegion, linearFilter);
            }
        }

        private static int MipSize(int size, int level) => Math.Max(1, size >> level);

        private unsafe void CopyRegion(MetalTexture destination, int srcLayer, int dstLayer, int srcLevel, int dstLevel, int srcX, int srcY, int dstX, int dstY, int width, int height)
        {
            if (_mtlTexture == nint.Zero || destination.TextureHandle == nint.Zero || _commandQueue == nint.Zero ||
                srcLayer < 0 || dstLayer < 0 || srcLevel < 0 || dstLevel < 0 || width <= 0 || height <= 0)
            {
                return;
            }

            if (width >= 960 || height >= 540)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"[TEX_COPY] src=0x{_mtlTexture:X} ({_info.Format}) -> dst=0x{destination.TextureHandle:X} ({destination._info.Format}) {width}x{height} srcPos=({srcX},{srcY}) dstPos=({dstX},{dstY})");
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

            if (_renderer != null && _renderer.M4Queue.CompletionEvent != nint.Zero && _renderer.M4Queue.LastSignaledValue > 0)
            {
                MetalBindings.objc_msgSend_void(commandBuffer, MetalBindings.SelEncodeWaitForEventValue, _renderer.M4Queue.CompletionEvent, _renderer.M4Queue.LastSignaledValue);
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

        public unsafe void CopyTo(BufferRange range, int layer, int level, int stride)
        {
            if (_mtlTexture == nint.Zero || _renderer == null || _commandQueue == nint.Zero)
            {
                return;
            }

            nint dstBuffer = _renderer.GetBuffer(range.Handle);
            if (dstBuffer == nint.Zero)
            {
                return;
            }

            int mipW = Math.Max(1, _info.Width  >> level);
            int mipH = Math.Max(1, _info.Height >> level);

            int blockWidth  = Math.Max(1, _info.BlockWidth);
            int blockHeight = Math.Max(1, _info.BlockHeight);
            int blocksX = (mipW + blockWidth  - 1) / blockWidth;
            int blocksY = (mipH + blockHeight - 1) / blockHeight;

            // stride == 0 means packed; use natural row stride aligned to 4 bytes.
            int bytesPerRow = stride > 0
                ? stride
                : (int)BitUtils.AlignUp(blocksX * _info.BytesPerPixel, 4);
            int bytesPerImage = bytesPerRow * blocksY;

            nint commandBuffer = MetalBindings.Retain(MetalBindings.objc_msgSend(_commandQueue, MetalBindings.SelCommandBuffer));
            if (commandBuffer == nint.Zero)
            {
                return;
            }

            // Wait for any in-flight GPU work on this texture to complete before reading.
            if (_renderer.M4Queue.CompletionEvent != nint.Zero && _renderer.M4Queue.LastSignaledValue > 0)
            {
                MetalBindings.objc_msgSend_void(commandBuffer, MetalBindings.SelEncodeWaitForEventValue,
                    _renderer.M4Queue.CompletionEvent, _renderer.M4Queue.LastSignaledValue);
            }

            nint blitEncoder = MetalBindings.objc_msgSend(commandBuffer, MetalBindings.SelBlitCommandEncoder);
            if (blitEncoder == nint.Zero)
            {
                MetalBindings.Release(commandBuffer);
                return;
            }

            MTLOrigin srcOrigin = new(0, 0, 0);
            MTLSize   srcSize   = new((nuint)mipW, (nuint)mipH, 1);

            MetalBindings.objc_msgSend_void_blitCopyToBuffer(
                blitEncoder,
                MetalBindings.SelCopyFromTextureToBuffer,
                _mtlTexture,
                (nuint)layer,
                (nuint)level,
                srcOrigin,
                srcSize,
                dstBuffer,
                (nuint)range.Offset,
                (nuint)bytesPerRow,
                (nuint)bytesPerImage);

            MetalBindings.objc_msgSend_void(blitEncoder, MetalBindings.SelEndEncoding);
            MetalBindings.objc_msgSend_void(commandBuffer, MetalBindings.SelCommit);
            MetalBindings.objc_msgSend_void(commandBuffer, MetalBindings.SelWaitUntilCompleted);
            MetalBindings.Release(commandBuffer);
        }


        internal static ulong ToMtlTextureType(Target target)
        {
            return target switch
            {
                Target.Texture2D => MetalBindings.MTLTextureType2D,
                Target.Texture2DMultisample => MetalBindings.MTLTextureType2DMultisample,
                Target.Texture2DArray => MetalBindings.MTLTextureType2DArray,
                Target.Cubemap => MetalBindings.MTLTextureTypeCube,
                Target.CubemapArray => MetalBindings.MTLTextureTypeCubeArray,
                Target.Texture3D => MetalBindings.MTLTextureType3D,
                _ => MetalBindings.MTLTextureType2D,
            };
        }

        public ITexture CreateView(TextureCreateInfo info, int firstLayer, int firstLevel)
        {
            ulong pixelFormat = MetalFormats.ToMtlPixelFormat(info.Format);
            if (pixelFormat == 0 || _mtlTexture == nint.Zero)
            {
                return new MetalTexture(_renderer, _device, _commandQueue, info, _flushPendingWork);
            }

            ulong textureType = ToMtlTextureType(info.Target);

            nint viewHandle = nint.Zero;

            MTLTextureSwizzleChannels swizzle = new(
                MetalFormats.ToMtlSwizzle(info.SwizzleR),
                MetalFormats.ToMtlSwizzle(info.SwizzleG),
                MetalFormats.ToMtlSwizzle(info.SwizzleB),
                MetalFormats.ToMtlSwizzle(info.SwizzleA));

            if (!swizzle.IsIdentity)
            {
                viewHandle = MetalBindings.objc_msgSend(
                    _mtlTexture,
                    MetalBindings.SelNewTextureViewWithPixelFormatTextureTypeLevelsSlicesSwizzle,
                    (nuint)pixelFormat,
                    (nuint)textureType,
                    (nuint)firstLevel,
                    (nuint)Math.Max(1, info.Levels),
                    (nuint)firstLayer,
                    (nuint)Math.Max(1, info.GetLayers()),
                    swizzle);
            }

            if (viewHandle == nint.Zero)
            {
                viewHandle = MetalBindings.objc_msgSend(
                    _mtlTexture,
                    MetalBindings.SelNewTextureViewWithPixelFormatTextureTypeLevelsSlices,
                    (nuint)pixelFormat,
                    (nuint)textureType,
                    (nuint)firstLevel,
                    (nuint)Math.Max(1, info.Levels),
                    (nuint)firstLayer,
                    (nuint)Math.Max(1, info.GetLayers()));
            }

            if (viewHandle == nint.Zero)
            {
                viewHandle = MetalBindings.objc_msgSend(_mtlTexture, MetalBindings.SelNewTextureViewWithPixelFormat, pixelFormat);
            }

            if (viewHandle == nint.Zero)
            {
                return new MetalTexture(_renderer, _device, _commandQueue, info, _flushPendingWork);
            }

            MetalTexture view = new MetalTexture(_renderer, _device, _commandQueue, info, viewHandle, _flushPendingWork);
            MetalBindings.Release(viewHandle);

            if (info.Width >= 960 || info.Height >= 540)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"[TEX_VIEW] viewHandle=0x{view.TextureHandle:X} parentHandle=0x{_mtlTexture:X} {info.Width}x{info.Height} {info.Format} target={info.Target} firstLayer={firstLayer} firstLevel={firstLevel}");
            }

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

            int blockWidth = Math.Max(1, _info.BlockWidth);
            int blockHeight = Math.Max(1, _info.BlockHeight);
            int blocksX = (width + blockWidth - 1) / blockWidth;
            int blocksY = (height + blockHeight - 1) / blockHeight;

            int bytesPerRow = blocksX * _info.BytesPerPixel;
            int size = bytesPerRow * blocksY;

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
                        (nuint)size,
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

        public void SetData(MemoryOwner<byte> data)
        {
            if (_info.Levels <= 1 && _info.GetLayers() <= 1)
            {
                // Fast path: single-layer, single-level texture. Just upload level 0.
                SetData(data, 0, 0);
                return;
            }

            // Multi-mip or multi-layer: iterate all layers and levels, slicing
            // the concatenated data buffer by the correct per-level mip size.
            int offset = 0;
            int layers = _info.GetLayers();
            ReadOnlySpan<byte> src = data.Span;

            for (int level = 0; level < _info.Levels; level++)
            {
                int mipLevelWidth  = Math.Max(1, _info.Width  >> level);
                int mipLevelHeight = Math.Max(1, _info.Height >> level);
                int blockWidth  = Math.Max(1, _info.BlockWidth);
                int blockHeight = Math.Max(1, _info.BlockHeight);
                int blocksX = (mipLevelWidth  + blockWidth  - 1) / blockWidth;
                int blocksY = (mipLevelHeight + blockHeight - 1) / blockHeight;
                int bytesPerRow   = blocksX * _info.BytesPerPixel;
                int bytesPerSlice = bytesPerRow * blocksY;

                for (int layer = 0; layer < layers; layer++)
                {
                    int sliceEnd = offset + bytesPerSlice;
                    if (sliceEnd > src.Length)
                    {
                        break;
                    }

                    using MemoryOwner<byte> sliceData = MemoryOwner<byte>.Rent(bytesPerSlice);
                    src.Slice(offset, bytesPerSlice).CopyTo(sliceData.Span);
                    SetData(sliceData, layer, level, new Rectangle<int>(0, 0, mipLevelWidth, mipLevelHeight));

                    offset += bytesPerSlice;
                }
            }

            data.Dispose();
        }


        public unsafe void SetData(MemoryOwner<byte> data, int layer, int level, Rectangle<int> region)
        {
            try
            {
                if (_mtlTexture != nint.Zero && !_disposed && region.Width > 0 && region.Height > 0)
                {
                    MTLRegion mtlRegion = new((nuint)region.X, (nuint)region.Y, 0, (nuint)region.Width, (nuint)region.Height, 1);

                    int blockWidth = Math.Max(1, _info.BlockWidth);
                    int blockHeight = Math.Max(1, _info.BlockHeight);
                    int blocksX = (region.Width + blockWidth - 1) / blockWidth;
                    int blocksY = (region.Height + blockHeight - 1) / blockHeight;

                    int bytesPerRow = blocksX * _info.BytesPerPixel;
                    int bytesPerImage = bytesPerRow * blocksY;

                    if (Width >= 960 || Height >= 540)
                    {
                        int sampleSize = Math.Min(data.Span.Length, 64);
                        int nonzero = 0;
                        for (int i = 0; i < sampleSize; i++) if (data.Span[i] != 0) nonzero++;
                        Logger.Warning?.Print(LogClass.Gpu, $"[TEX_SETDATA] handle=0x{_mtlTexture:X} {Width}x{Height} region=({region.X},{region.Y},{region.Width},{region.Height}) layer={layer} level={level} len={data.Span.Length} sampleNonzero={nonzero}/{sampleSize}");
                    }

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
                                (nuint)bytesPerImage);
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

        public void SetData(MemoryOwner<byte> data, int layer, int level)
        {
            // Use correct mip-level dimensions rather than mip-0 Width/Height.
            // Passing mip-0 size for level > 0 causes out-of-bounds copies and
            // the "rectangular slab" Maxwell texture deswizzle corruption.
            int mipW = Math.Max(1, _info.Width  >> level);
            int mipH = Math.Max(1, _info.Height >> level);
            SetData(data, layer, level, new Rectangle<int>(0, 0, mipW, mipH));
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
        public SamplerCreateInfo Info => _info;

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
                // Min/mag filter from MinFilter/MagFilter; address modes from WrapS/WrapT/WrapR.
                ulong minFilter = _info.MinFilter is MinFilter.Linear or MinFilter.LinearMipmapLinear or MinFilter.LinearMipmapNearest
                    ? MetalBindings.MTLSamplerMinMagFilterLinear
                    : MetalBindings.MTLSamplerMinMagFilterNearest;

                ulong magFilter = _info.MagFilter == MagFilter.Linear
                    ? MetalBindings.MTLSamplerMinMagFilterLinear
                    : MetalBindings.MTLSamplerMinMagFilterNearest;

                ulong mipFilter = _info.MinFilter switch
                {
                    MinFilter.NearestMipmapNearest or MinFilter.LinearMipmapNearest => MetalBindings.MTLSamplerMipFilterNearest,
                    MinFilter.NearestMipmapLinear or MinFilter.LinearMipmapLinear => MetalBindings.MTLSamplerMipFilterLinear,
                    _ => MetalBindings.MTLSamplerMipFilterNotMipmapped,
                };

                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetMinFilter, (nuint)minFilter);
                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetMagFilter, (nuint)magFilter);
                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetMipFilter, (nuint)mipFilter);

                ulong sAddress = ToAddressMode(_info.AddressU);
                ulong tAddress = ToAddressMode(_info.AddressV);
                ulong rAddress = ToAddressMode(_info.AddressP);

                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetSAddressMode, (nuint)sAddress);
                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetTAddressMode, (nuint)tAddress);
                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetRAddressMode, (nuint)rAddress);

                if (_info.CompareMode != CompareMode.None)
                {
                    MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetCompareFunction, (nuint)MetalFormats.ToMtlCompareFunction(_info.CompareOp));
                }

                if (mipFilter == MetalBindings.MTLSamplerMipFilterNotMipmapped)
                {
                    MetalBindings.objc_msgSend_float(descriptor, MetalBindings.SelSetLodMinClamp, 0f);
                    MetalBindings.objc_msgSend_float(descriptor, MetalBindings.SelSetLodMaxClamp, 0f);
                }
                else
                {
                    MetalBindings.objc_msgSend_float(descriptor, MetalBindings.SelSetLodMinClamp, Math.Max(0f, _info.MinLod));
                    MetalBindings.objc_msgSend_float(descriptor, MetalBindings.SelSetLodMaxClamp, Math.Max(0f, _info.MaxLod));
                }

                if (_info.MaxAnisotropy > 1)
                {
                    MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetMaxAnisotropy, (nuint)_info.MaxAnisotropy);
                }

                _samplerState = MetalBindings.objc_msgSend(_device, MetalBindings.SelNewSamplerStateWithDescriptor, descriptor);
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
