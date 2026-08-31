using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    public class MetalTexture : ITexture
    {
        private readonly nint _device;
        private readonly TextureCreateInfo _info;

        public int Width => _info.Width;
        public int Height => _info.Height;
        public float ScaleFactor => 1.0f;

        public MetalTexture(nint device, TextureCreateInfo info)
        {
            _device = device;
            _info = info;
        }

        public void CopyTo(ITexture destination, int firstLayer, int firstLevel) { }

        public void CopyTo(ITexture destination, int srcLayer, int dstLayer, int srcLevel, int dstLevel) { }

        public void CopyTo(ITexture destination, Extents2D srcRegion, Extents2D dstRegion, bool linearFilter) { }

        public void CopyTo(BufferRange range, int layer, int level, int stride) { }

        public ITexture CreateView(TextureCreateInfo info, int firstLayer, int firstLevel)
        {
            return new MetalTexture(_device, info);
        }

        public PinnedSpan<byte> GetData() => PinnedSpan<byte>.UnsafeFromSpan(ReadOnlySpan<byte>.Empty);

        public PinnedSpan<byte> GetData(int layer, int level) => PinnedSpan<byte>.UnsafeFromSpan(ReadOnlySpan<byte>.Empty);

        public void SetData(MemoryOwner<byte> data)
        {
            data.Dispose();
        }

        public void SetData(MemoryOwner<byte> data, int layer, int level)
        {
            data.Dispose();
        }

        public void SetData(MemoryOwner<byte> data, int layer, int level, Rectangle<int> region)
        {
            data.Dispose();
        }

        public void SetStorage(BufferRange buffer) { }

        public void Release() { }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }

    [SupportedOSPlatform("macos")]
    public class MetalSampler : ISampler
    {
        private readonly nint _device;
        private readonly SamplerCreateInfo _info;

        public MetalSampler(nint device, SamplerCreateInfo info)
        {
            _device = device;
            _info = info;
        }

        public void Release() { }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }

    [SupportedOSPlatform("macos")]
    public class MetalProgram : IProgram
    {
        private readonly nint _device;

        public MetalProgram(nint device, ShaderSource[] shaders, ShaderInfo info)
        {
            _device = device;
        }

        public MetalProgram(nint device, byte[] programBinary, ShaderInfo info)
        {
            _device = device;
        }

        public byte[] GetBinary() => Array.Empty<byte>();

        public ProgramLinkStatus CheckProgramLink(bool blocking) => ProgramLinkStatus.Success;

        public void Dispose()
        {
            GC.SuppressFinalize(this);
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
