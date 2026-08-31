using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Metal.Interop;
using Ryujinx.Graphics.Shader.Translation;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    public class MetalRenderer : IRenderer
    {
        public event EventHandler<ScreenCaptureImageInfo>? ScreenCaptured;

        public bool PreferThreading => true;

        public IPipeline Pipeline => _pipeline;
        public IWindow Window => _window;

        public uint ProgramCount => 0;

        private readonly nint _device;
        private readonly nint _commandQueue;
        private readonly MetalPipeline _pipeline;
        private readonly MetalWindow _window;
        private readonly MetalBufferManager _bufferManager;

        public nint DeviceHandle => _device;
        public nint CommandQueueHandle => _commandQueue;

        public MetalRenderer()
        {
            _device = MetalBindings.MTLCreateSystemDefaultDevice();
            if (_device == nint.Zero)
            {
                throw new PlatformNotSupportedException("Apple Metal is not supported on this device.");
            }

            _commandQueue = MetalBindings.objc_msgSend(_device, MetalBindings.SelNewCommandQueue);
            _bufferManager = new MetalBufferManager(_device);
            _pipeline = new MetalPipeline(this, _device, _commandQueue);
            _window = new MetalWindow(this, _device, _commandQueue);

            Logger.Info?.Print(LogClass.Gpu, $"Initialized Pure Native Apple Metal 3 Backend: {GetHardwareInfo().GpuDriver}");
        }

        public void BackgroundContextAction(Action action, bool alwaysBackground = false)
        {
            action();
        }

        public BufferHandle CreateBuffer(int size, BufferAccess access = BufferAccess.Default)
        {
            return _bufferManager.CreateBuffer(size);
        }

        public BufferHandle CreateBuffer(nint pointer, int size)
        {
            return _bufferManager.CreateBuffer(pointer, size);
        }

        public BufferHandle CreateBufferSparse(ReadOnlySpan<BufferRange> storageBuffers)
        {
            int totalSize = 0;
            foreach (BufferRange range in storageBuffers)
            {
                totalSize += (int)range.Size;
            }
            return CreateBuffer(totalSize);
        }

        public IImageArray CreateImageArray(int size, bool isBuffer)
        {
            return new MetalImageArray(size);
        }

        public IProgram CreateProgram(ShaderSource[] shaders, ShaderInfo info)
        {
            return new MetalProgram(_device, shaders, info);
        }

        public ISampler CreateSampler(SamplerCreateInfo info)
        {
            return new MetalSampler(_device, info);
        }

        public ITexture CreateTexture(TextureCreateInfo info)
        {
            return new MetalTexture(_device, info);
        }

        public ITextureArray CreateTextureArray(int size, bool isBuffer)
        {
            return new MetalTextureArray(size);
        }

        public bool PrepareHostMapping(nint address, ulong size)
        {
            return true;
        }

        public void CreateSync(ulong id, bool strict)
        {
            _pipeline.CreateSync(id);
        }

        public void WaitSync(ulong id) { }

        public void DeleteBuffer(BufferHandle buffer)
        {
            _bufferManager.DeleteBuffer(buffer);
        }

        public PinnedSpan<byte> GetBufferData(BufferHandle buffer, int offset, int size)
        {
            return _bufferManager.GetData(buffer, offset, size);
        }

        public Capabilities GetCapabilities()
        {
            return new Capabilities(
                api: TargetApi.Vulkan,
                vendorName: "Apple",
                memoryType: SystemMemoryType.UnifiedMemory,
                hasFrontFacingBug: false,
                hasVectorIndexingBug: false,
                needsFragmentOutputSpecialization: false,
                reduceShaderPrecision: false,
                supportsAstcCompression: true,
                supportsBc123Compression: true,
                supportsBc45Compression: true,
                supportsBc67Compression: true,
                supportsEtc2Compression: true,
                supports3DTextureCompression: true,
                supportsBgraFormat: true,
                supportsR4G4Format: false,
                supportsR4G4B4A4Format: true,
                supportsScaledVertexFormats: true,
                supportsSnormBufferTextureFormat: true,
                supports5BitComponentFormat: true,
                supportsSparseBuffer: false,
                supportsBlendEquationAdvanced: false,
                supportsFragmentShaderInterlock: true,
                supportsFragmentShaderOrderingIntel: false,
                supportsGeometryShader: false,
                supportsGeometryShaderPassthrough: false,
                supportsTransformFeedback: false,
                supportsImageLoadFormatted: true,
                supportsLayerVertexTessellation: true,
                supportsMismatchingViewFormat: true,
                supportsCubemapView: true,
                supportsNonConstantTextureOffset: false,
                supportsQuads: false,
                supportsSeparateSampler: true,
                supportsShaderBallot: false,
                supportsShaderBarrierDivergence: true,
                supportsShaderFloat64: false,
                supportsShaderNonUniformIndexing: true,
                supportsTextureGatherOffsets: true,
                supportsTextureShadowLod: false,
                supportsVertexStoreAndAtomics: true,
                supportsViewportIndexVertexTessellation: true,
                supportsViewportMask: false,
                supportsViewportSwizzle: false,
                supportsIndirectParameters: true,
                supportsDepthClipControl: true,
                uniformBufferSetIndex: 0,
                storageBufferSetIndex: 1,
                textureSetIndex: 2,
                imageSetIndex: 3,
                extraSetBaseIndex: 4,
                maximumExtraSets: 4,
                maximumUniformBuffersPerStage: 16,
                maximumStorageBuffersPerStage: 31,
                maximumTexturesPerStage: 128,
                maximumImagesPerStage: 128,
                maximumComputeSharedMemorySize: 32768,
                maximumSupportedAnisotropy: 16,
                shaderSubgroupSize: 32,
                storageBufferOffsetAlignment: 16,
                textureBufferOffsetAlignment: 16,
                gatherBiasPrecision: 8,
                maximumGpuMemory: 16UL * 1024 * 1024 * 1024);
        }

        public ulong GetCurrentSync()
        {
            return _pipeline.GetCurrentSync();
        }

        public HardwareInfo GetHardwareInfo()
        {
            nint namePtr = MetalBindings.objc_msgSend(_device, MetalBindings.SelName);
            string gpuName = Marshal.PtrToStringUTF8(namePtr) ?? "Apple Silicon GPU";
            return new HardwareInfo("Apple", gpuName, "Metal 3.0", "macOS 26.5");
        }

        public void Initialize(GraphicsDebugLevel glLogLevel)
        {
            Logger.Notice.Print(LogClass.Gpu, "Pure Native Apple Metal 3 graphics device pipeline operational.");
        }

        public void PreFlush() { }

        public void PreFrame() { }

        public IProgram LoadProgramBinary(byte[] programBinary, bool isFragment, ShaderInfo info)
        {
            return new MetalProgram(_device, programBinary, info);
        }

        public void ResetCounter(CounterType type) { }

        public ICounterEvent? ReportCounter(CounterType type, EventHandler<ulong> callback, float factor, bool hostReserved)
        {
            callback(this, 0);
            return null;
        }

        public void SetBufferData(BufferHandle buffer, int offset, ReadOnlySpan<byte> data)
        {
            _bufferManager.SetData(buffer, offset, data);
        }

        public void SetInterruptAction(Action<Action> interruptAction) { }

        public void Screenshot()
        {
            ScreenCaptured?.Invoke(this, new ScreenCaptureImageInfo(1280, 720, true, Array.Empty<byte>(), false, false));
        }

        public void UpdateCounters() { }

        public void SetImage(int binding, ITexture texture, Format format) { }

        public void Dispose()
        {
            _window.Dispose();
            _pipeline.Dispose();
            _bufferManager.Dispose();
            if (_commandQueue != nint.Zero)
            {
                MetalBindings.objc_msgSend_void(_commandQueue, MetalBindings.SelRelease);
            }
            if (_device != nint.Zero)
            {
                MetalBindings.objc_msgSend_void(_device, MetalBindings.SelRelease);
            }
            GC.SuppressFinalize(this);
        }
    }
}
