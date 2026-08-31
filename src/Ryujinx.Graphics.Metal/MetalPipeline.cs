using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    public class MetalPipeline : IPipeline
    {
        private readonly MetalRenderer _renderer;
        private readonly nint _device;
        private readonly nint _commandQueue;

        private ulong _currentSync;

        public MetalPipeline(MetalRenderer renderer, nint device, nint commandQueue)
        {
            _renderer = renderer;
            _device = device;
            _commandQueue = commandQueue;
        }

        public void Barrier() { }

        public void BeginTransformFeedback(PrimitiveTopology topology) { }

        public void ClearBuffer(BufferHandle destination, int offset, int size, uint value)
        {
            PinnedSpan<byte> span = _renderer.GetBufferData(destination, offset, size);
            unsafe
            {
                ReadOnlySpan<byte> rspan = span.Get();
                if (rspan.Length > 0)
                {
                    void* ptr = Unsafe.AsPointer(ref MemoryMarshal.GetReference(rspan));
                    NativeMemory.Fill(ptr, (nuint)size, (byte)value);
                }
            }
            span.Dispose();
        }

        public void ClearRenderTargetColor(int index, int layer, int layerCount, uint componentMask, ColorF color) { }

        public void ClearRenderTargetDepthStencil(
            int layer,
            int layerCount,
            float depthValue,
            bool depthMask,
            int stencilValue,
            int stencilMask) { }

        public void CommandBufferBarrier() { }

        public void CopyBuffer(BufferHandle source, BufferHandle destination, int srcOffset, int dstOffset, int size)
        {
            PinnedSpan<byte> srcSpan = _renderer.GetBufferData(source, srcOffset, size);
            ReadOnlySpan<byte> rspan = srcSpan.Get();
            if (rspan.Length > 0)
            {
                _renderer.SetBufferData(destination, dstOffset, rspan);
            }
            srcSpan.Dispose();
        }

        public void DispatchCompute(int groupsX, int groupsY, int groupsZ) { }

        public void Draw(int vertexCount, int instanceCount, int firstVertex, int firstInstance) { }

        public void DrawIndexed(
            int indexCount,
            int instanceCount,
            int firstIndex,
            int firstVertex,
            int firstInstance) { }

        public void DrawIndexedIndirect(BufferRange indirectBuffer) { }

        public void DrawIndexedIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride) { }

        public void DrawIndirect(BufferRange indirectBuffer) { }

        public void DrawIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride) { }

        public void DrawTexture(ITexture texture, ISampler sampler, Extents2DF srcRegion, Extents2DF dstRegion) { }

        public void EndHostConditionalRendering() { }

        public void EndTransformFeedback() { }

        public void SetAlphaTest(bool enable, float reference, CompareOp op) { }

        public void SetBlendState(AdvancedBlendDescriptor blend) { }

        public void SetBlendState(int index, BlendDescriptor blend) { }

        public void SetDepthBias(PolygonModeMask enables, float factor, float units, float clamp) { }

        public void SetDepthClamp(bool clamp) { }

        public void SetDepthMode(DepthMode mode) { }

        public void SetDepthTest(DepthTestDescriptor depthTest) { }

        public void SetFaceCulling(bool enable, Face face) { }

        public void SetFrontFace(FrontFace frontFace) { }

        public void SetIndexBuffer(BufferRange buffer, IndexType type) { }

        public void SetImage(ShaderStage stage, int binding, ITexture texture) { }

        public void SetImageArray(ShaderStage stage, int binding, IImageArray array) { }

        public void SetImageArraySeparate(ShaderStage stage, int setIndex, IImageArray array) { }

        public void SetLineParameters(float width, bool smooth) { }

        public void SetLogicOpState(bool enable, LogicalOp op) { }

        public void SetMultisampleState(MultisampleDescriptor multisample) { }

        public void SetPatchParameters(int vertices, ReadOnlySpan<float> defaultOuterLevel, ReadOnlySpan<float> defaultInnerLevel) { }

        public void SetPointParameters(float size, bool isProgramPointSize, bool enablePointSprite, Origin origin) { }

        public void SetPolygonMode(PolygonMode frontMode, PolygonMode backMode) { }

        public void SetPrimitiveRestart(bool enable, int index) { }

        public void SetPrimitiveTopology(PrimitiveTopology topology) { }

        public void SetProgram(IProgram program) { }

        public void SetRasterizerDiscard(bool discard) { }

        public void SetRenderTargetColorMasks(ReadOnlySpan<uint> componentMask) { }

        public void SetRenderTargets(Span<ITexture> colors, ITexture depthStencil) { }

        public void SetScissors(ReadOnlySpan<Rectangle<int>> regions) { }

        public void SetStencilTest(StencilTestDescriptor stencilTest) { }

        public void SetStorageBuffers(ReadOnlySpan<BufferAssignment> buffers) { }

        public void SetTextureAndSampler(ShaderStage stage, int binding, ITexture texture, ISampler sampler) { }

        public void SetTextureArray(ShaderStage stage, int binding, ITextureArray array) { }

        public void SetTextureArraySeparate(ShaderStage stage, int setIndex, ITextureArray array) { }

        public void SetTransformFeedbackBuffers(ReadOnlySpan<BufferRange> buffers) { }

        public void SetUniformBuffers(ReadOnlySpan<BufferAssignment> buffers) { }

        public void SetUserClipDistance(int index, bool enableClip) { }

        public void SetVertexAttribs(ReadOnlySpan<VertexAttribDescriptor> vertexAttribs) { }

        public void SetVertexBuffers(ReadOnlySpan<VertexBufferDescriptor> vertexBuffers) { }

        public void SetViewports(ReadOnlySpan<Viewport> viewports) { }

        public void TextureBarrier() { }

        public void TextureBarrierTiled() { }

        public bool TryHostConditionalRendering(ICounterEvent value, ulong compare, bool isEqual) => false;

        public bool TryHostConditionalRendering(ICounterEvent value, ICounterEvent compare, bool isEqual) => false;

        public void CreateSync(ulong id)
        {
            _currentSync = id;
        }

        public ulong GetCurrentSync()
        {
            return _currentSync;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
