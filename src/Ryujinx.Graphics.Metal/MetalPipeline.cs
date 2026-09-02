using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Metal.Interop;
using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// M4: full render pipeline state machine for the native Metal backend.
    ///
    /// Tracks the current program, render targets, fixed-function state, and bound
    /// resources; lazily builds and caches MTLRenderPipelineState objects; and encodes
    /// actual draws (draw / drawIndexed) against an active MTLRenderCommandEncoder.
    ///
    /// Render-pass lifecycle: SetRenderTargets starts a pass; the encoder is created on
    /// the first draw; <see cref="FlushFrame"/> ends the encoder and commits the command
    /// buffer (called before presentation so the blit sees the rendered frame).
    /// </summary>
    [SupportedOSPlatform("macos")]
    public class MetalPipeline : IPipeline
    {
        // GAL descriptor-set indices (must match GetCapabilities()).
        private const uint UniformBufferSet = 0;
        private const uint StorageBufferSet = 1;
        private const uint TextureSet = 2;
        private const uint ImageSet = 3;

        private readonly MetalRenderer _renderer;
        private readonly nint _device;
        private readonly nint _commandQueue;

        // Program + fixed-function state that feeds pipeline creation.
        private MetalProgram _program;
        private ITexture[] _colorTargets = Array.Empty<ITexture>();
        private int _colorTargetCount;
        private ITexture _depthTarget;
        private BlendDescriptor _blend;
        private DepthTestDescriptor _depthTest;
        private bool _depthDirty = true;
        private PrimitiveTopology _topology = PrimitiveTopology.Triangles;
        private VertexAttribDescriptor[] _vertexAttribs = Array.Empty<VertexAttribDescriptor>();
        private VertexBufferDescriptor[] _vertexBuffers = Array.Empty<VertexBufferDescriptor>();
        private bool _cullEnabled;
        private Face _cullFace;
        private FrontFace _frontFace = FrontFace.CounterClockwise;
        private bool _depthClamp;
        private bool _rasterizerDiscard;
        private bool _depthStencilDirty = true;
        private nint _depthStencilState;

        // Draw-time bound resources.
        private readonly Dictionary<int, BufferAssignment> _uniformBuffers = new();
        private readonly Dictionary<int, BufferAssignment> _storageBuffers = new();
        private readonly Dictionary<int, (ITexture Texture, ISampler Sampler)> _texturesVertex = new();
        private readonly Dictionary<int, (ITexture Texture, ISampler Sampler)> _texturesFragment = new();
        private readonly Dictionary<int, ITexture> _imagesVertex = new();
        private readonly Dictionary<int, ITexture> _imagesFragment = new();
        private BufferRange _indexBuffer;
        private IndexType _indexType;
        private Viewport[] _viewports = Array.Empty<Viewport>();
        private Rectangle<int>[] _scissors = Array.Empty<Rectangle<int>>();

        // Active render pass.
        private nint _commandBuffer;
        private nint _renderEncoder;
        private nint _renderPassDescriptor;

        // Pipeline state cache.
        private readonly Dictionary<string, nint> _pipelineCache = new();
        private ulong _currentSync;

        // M6: host sync via MTLSharedEvent + clear color state.
        private nint _syncEvent;
        private bool _hasClearColor;
        private ColorF _clearColor;
        private bool _hasDepthClear;
        private float _depthClearValue;
        private int _stencilClearValue;
        private uint[] _colorWriteMasks = Array.Empty<uint>();

        public MetalPipeline(MetalRenderer renderer, nint device, nint commandQueue)
        {
            _renderer = renderer;
            _device = device;
            _commandQueue = commandQueue;

            _syncEvent = MetalBindings.Retain(MetalBindings.objc_msgSend(_device, MetalBindings.SelNewSharedEvent));
        }

        public void FlushFrame()
        {
            EndRenderPass();
        }

        // ---- Render pass lifecycle ----

        private bool EnsureRenderPass()
        {
            if (_renderEncoder != nint.Zero)
            {
                return true;
            }

            if (_colorTargetCount == 0 && _depthTarget == null)
            {
                return false;
            }

            _commandBuffer = MetalBindings.Retain(_renderer.CommandPool.Acquire());

            if (_commandBuffer == nint.Zero)
            {
                return false;
            }

            _renderPassDescriptor = MetalBindings.objc_msgSend(
                MetalBindings.objc_getClass("MTLRenderPassDescriptor"),
                MetalBindings.SelRenderPassDescriptor);

            nint colorAttachments = MetalBindings.objc_msgSend(_renderPassDescriptor, MetalBindings.SelColorAttachments);

            for (int i = 0; i < _colorTargetCount; i++)
            {
                if (_colorTargets[i] is not MetalTexture target || target.TextureHandle == nint.Zero)
                {
                    continue;
                }

                nint attachment = MetalBindings.objc_msgSend(colorAttachments, MetalBindings.SelObjectAtIndexedSubscript, (nuint)i);

                if (attachment == nint.Zero)
                {
                    continue;
                }

                MetalBindings.objc_msgSend_void(attachment, MetalBindings.SelSetTexture, target.TextureHandle);
                MetalBindings.objc_msgSend_void(attachment, MetalBindings.SelSetLoadAction, (nuint)MetalBindings.MTLLoadActionClear);
                MetalBindings.objc_msgSend_void(attachment, MetalBindings.SelSetStoreAction, (nuint)MetalBindings.MTLStoreActionStore);

                if (i == 0 && _hasClearColor)
                {
                    MTLColor clearColor = new(_clearColor.Red, _clearColor.Green, _clearColor.Blue, _clearColor.Alpha);

                    unsafe
                    {
                        MetalBindings.objc_msgSend_void(attachment, MetalBindings.SelSetClearColor, &clearColor);
                    }
                }
            }

            if (_depthTarget is MetalTexture depthTarget && depthTarget.TextureHandle != nint.Zero)
            {
                nint depthAttachment = MetalBindings.objc_msgSend(_renderPassDescriptor, MetalBindings.SelDepthAttachment);

                if (depthAttachment != nint.Zero)
                {
                    MetalBindings.objc_msgSend_void(depthAttachment, MetalBindings.SelSetTexture, depthTarget.TextureHandle);
                    MetalBindings.objc_msgSend_void(depthAttachment, MetalBindings.SelSetLoadAction, (nuint)MetalBindings.MTLLoadActionClear);
                    MetalBindings.objc_msgSend_void(depthAttachment, MetalBindings.SelSetStoreAction, (nuint)MetalBindings.MTLStoreActionStore);

                    if (_hasDepthClear)
                    {
                        MetalBindings.objc_msgSend_void(depthAttachment, MetalBindings.SelSetClearDepth, (double)_depthClearValue);
                    }
                }
            }

            _renderEncoder = MetalBindings.objc_msgSend(_commandBuffer, MetalBindings.SelRenderCommandEncoderWithDescriptor, _renderPassDescriptor);

            return _renderEncoder != nint.Zero;
        }

        private void EndRenderPass()
        {
            if (_renderEncoder != nint.Zero)
            {
                MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelEndEncoding);
                MetalBindings.Release(_renderEncoder);
                _renderEncoder = nint.Zero;
            }

            if (_commandBuffer != nint.Zero)
            {
                // Signal the shared event with the current sync id before committing, so
                // consumers (WaitSync / the GPU driver) can wait on the fence value.
                if (_syncEvent != nint.Zero)
                {
                    MetalBindings.objc_msgSend_void(
                        _commandBuffer,
                        MetalBindings.SelEncodeSignalEventValue,
                        _syncEvent,
                        _currentSync);
                }

                MetalBindings.objc_msgSend_void(_commandBuffer, MetalBindings.SelCommit);

                // Wait for the GPU to COMPLETE the render (bounded). A scheduled-but-not-
                // completed buffer can race a subsequent getBytes readback on UMA.
                MetalBindings.objc_msgSend_void(_commandBuffer, MetalBindings.SelWaitUntilCompleted);

                _renderer.CommandPool.ReturnBuffer(_commandBuffer);
                _commandBuffer = nint.Zero;
            }

            _renderPassDescriptor = nint.Zero;
        }

        // ---- Pipeline state creation ----

        private nint GetOrCreatePipelineState()
        {
            if (_program == null)
            {
                return nint.Zero;
            }

            string key = BuildPipelineKey();

            if (_pipelineCache.TryGetValue(key, out nint cached))
            {
                return cached;
            }

            nint descriptor = MetalBindings.objc_msgSend(
                MetalBindings.objc_getClass("MTLRenderPipelineDescriptor"),
                MetalBindings.SelNew);

            try
            {
                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetVertexFunction, _program.VertexFunction);
                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetFragmentFunction, _program.FragmentFunction);

                // Keep inputPrimitiveTopology unspecified. SPIRV-Cross may retain a
                // [[point_size]] vertex output even for shaders that are subsequently
                // used with triangles; locking this descriptor to Triangle makes Metal
                // reject that otherwise valid pipeline. The real primitive type is
                // supplied by drawPrimitives/drawIndexedPrimitives below.

                ConfigureVertexDescriptor(descriptor);
                ConfigureColorAttachments(descriptor);

                ulong depthFormat = _depthTarget is MetalTexture dTex ? MetalFormats.ToMtlPixelFormat(dTex.Format) : 0;

                if (depthFormat != 0)
                {
                    MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetDepthAttachmentPixelFormat, (nuint)depthFormat);
                }

                nint nsError = nint.Zero;
                nint state;

                unsafe
                {
                    state = MetalBindings.objc_msgSend(
                        _device,
                        MetalBindings.SelNewRenderPipelineStateWithDescriptorError,
                        descriptor,
                        (nint)(&nsError));
                }

                if (state != nint.Zero)
                {
                    _pipelineCache[key] = state;
                }
                else
                {
                    Logger.Error?.Print(LogClass.Gpu, $"MetalPipeline: pipeline-state creation failed: {MetalBindings.GetErrorDescription(nsError)}");
                }

                return state;
            }
            finally
            {
                MetalBindings.Release(descriptor);
            }
        }

        private string BuildPipelineKey()
        {
            var sb = new System.Text.StringBuilder(256);

            sb.Append(_program.VertexFunction.ToString("x"));
            sb.Append('/');
            sb.Append(_program.FragmentFunction.ToString("x"));
            sb.Append('/');
            sb.Append(((int)_topology).ToString());
            sb.Append('/');

            for (int i = 0; i < _colorTargetCount; i++)
            {
                if (_colorTargets[i] is MetalTexture target)
                {
                    sb.Append(target.Format.ToString());
                }

                sb.Append('/');
            }

            sb.Append(_blend.Enable ? "B1" : "B0");
            sb.Append('/');
            sb.Append(((int)_blend.ColorOp).ToString());
            sb.Append('/');
            sb.Append(((int)_blend.AlphaOp).ToString());
            sb.Append('/');
            sb.Append(((int)_blend.ColorSrcFactor).ToString());
            sb.Append('/');
            sb.Append(((int)_blend.ColorDstFactor).ToString());
            sb.Append('/');
            sb.Append(((int)_blend.AlphaSrcFactor).ToString());
            sb.Append('/');
            sb.Append(((int)_blend.AlphaDstFactor).ToString());
            sb.Append('/');

            foreach (VertexAttribDescriptor attrib in _vertexAttribs)
            {
                sb.Append(attrib.BufferIndex.ToString());
                sb.Append('.');
                sb.Append(attrib.Offset.ToString());
                sb.Append('.');
                sb.Append(attrib.Format.ToString());
                sb.Append(',');
            }

            sb.Append('/');

            foreach (VertexBufferDescriptor vb in _vertexBuffers)
            {
                sb.Append(vb.Stride.ToString());
                sb.Append('.');
                sb.Append(vb.Divisor.ToString());
                sb.Append(',');
            }

            return sb.ToString();
        }

        private void ConfigureVertexDescriptor(nint pipelineDescriptor)
        {
            nint vertexDescriptor = MetalBindings.objc_msgSend(
                MetalBindings.objc_getClass("MTLVertexDescriptor"),
                MetalBindings.SelVertexDescriptor);

            if (vertexDescriptor == nint.Zero)
            {
                return;
            }

            try
            {
                nint attributes = MetalBindings.objc_msgSend(vertexDescriptor, MetalBindings.SelAttributes);
                nint layouts = MetalBindings.objc_msgSend(vertexDescriptor, MetalBindings.SelLayouts);

                for (int i = 0; i < _vertexAttribs.Length; i++)
                {
                    VertexAttribDescriptor attrib = _vertexAttribs[i];
                    ulong format = MetalFormats.ToMtlVertexFormat(attrib.Format);

                    if (format == MetalBindings.MTLVertexFormatInvalid)
                    {
                        continue;
                    }

                    nint attribute = MetalBindings.objc_msgSend(attributes, MetalBindings.SelObjectAtIndexedSubscript, (nuint)i);

                    if (attribute == nint.Zero)
                    {
                        continue;
                    }

                    MetalBindings.objc_msgSend_void(attribute, MetalBindings.SelSetVertexFormat, (nuint)format);
                    MetalBindings.objc_msgSend_void(attribute, MetalBindings.SelSetOffset, (nuint)attrib.Offset);
                    MetalBindings.objc_msgSend_void(attribute, MetalBindings.SelSetBufferIndex, (nuint)attrib.BufferIndex);
                }

                // Vertex buffer layouts: stride + step function from the bound vertex buffers.
                for (int i = 0; i < _vertexBuffers.Length; i++)
                {
                    VertexBufferDescriptor vb = _vertexBuffers[i];
                    nint layout = MetalBindings.objc_msgSend(layouts, MetalBindings.SelObjectAtIndexedSubscript, (nuint)i);

                    if (layout == nint.Zero)
                    {
                        continue;
                    }

                    MetalBindings.objc_msgSend_void(layout, MetalBindings.SelSetStride, (nuint)vb.Stride);

                    ulong stepFunction = vb.Divisor == 0
                        ? MetalBindings.MTLVertexStepFunctionPerVertex
                        : MetalBindings.MTLVertexStepFunctionPerInstance;

                    ulong stepRate = vb.Divisor == 0 ? 1 : (ulong)vb.Divisor;

                    MetalBindings.objc_msgSend_void(layout, MetalBindings.SelSetStepFunction, (nuint)stepFunction);
                    MetalBindings.objc_msgSend_void(layout, MetalBindings.SelSetStepRate, (nuint)stepRate);
                }

                MetalBindings.objc_msgSend_void(pipelineDescriptor, MetalBindings.SelSetVertexDescriptor, vertexDescriptor);
            }
            finally
            {
                MetalBindings.Release(vertexDescriptor);
            }
        }

        private void ConfigureColorAttachments(nint pipelineDescriptor)
        {
            nint colorAttachments = MetalBindings.objc_msgSend(pipelineDescriptor, MetalBindings.SelColorAttachments);

            for (int i = 0; i < _colorTargetCount; i++)
            {
                if (_colorTargets[i] is not MetalTexture target)
                {
                    continue;
                }

                ulong pixelFormat = MetalFormats.ToMtlPixelFormat(target.Format);

                if (pixelFormat == 0)
                {
                    continue;
                }

                nint attachment = MetalBindings.objc_msgSend(colorAttachments, MetalBindings.SelObjectAtIndexedSubscript, (nuint)i);

                if (attachment == nint.Zero)
                {
                    continue;
                }

                MetalBindings.objc_msgSend_void(attachment, MetalBindings.SelSetPixelFormat, (nuint)pixelFormat);
                MetalBindings.objc_msgSend_void(attachment, MetalBindings.SelSetBlendingEnabled, _blend.Enable);

                if (_blend.Enable)
                {
                    MetalBindings.objc_msgSend_void(attachment, MetalBindings.SelSetRgbBlendOperation, (nuint)ToBlendOp(_blend.ColorOp));
                    MetalBindings.objc_msgSend_void(attachment, MetalBindings.SelSetAlphaBlendOperation, (nuint)ToBlendOp(_blend.AlphaOp));
                    MetalBindings.objc_msgSend_void(attachment, MetalBindings.SelSetSourceRGBBlendFactor, (nuint)ToBlendFactor(_blend.ColorSrcFactor));
                    MetalBindings.objc_msgSend_void(attachment, MetalBindings.SelSetDestinationRGBBlendFactor, (nuint)ToBlendFactor(_blend.ColorDstFactor));
                    MetalBindings.objc_msgSend_void(attachment, MetalBindings.SelSetSourceAlphaBlendFactor, (nuint)ToBlendFactor(_blend.AlphaSrcFactor));
                    MetalBindings.objc_msgSend_void(attachment, MetalBindings.SelSetDestinationAlphaBlendFactor, (nuint)ToBlendFactor(_blend.AlphaDstFactor));
                }

                ulong writeMask = i < _colorWriteMasks.Length ? _colorWriteMasks[i] : 0xF;

                MetalBindings.objc_msgSend_void(attachment, MetalBindings.SelSetWriteMask, (nuint)writeMask);
            }
        }

        private void EnsureDepthStencilState()
        {
            if (!_depthDirty && _depthStencilState != nint.Zero)
            {
                return;
            }

            if (_depthStencilState != nint.Zero)
            {
                MetalBindings.Release(_depthStencilState);
                _depthStencilState = nint.Zero;
            }

            nint descriptor = MetalBindings.objc_msgSend(
                MetalBindings.objc_getClass("MTLDepthStencilDescriptor"),
                MetalBindings.SelNew);

            if (descriptor == nint.Zero)
            {
                return;
            }

            try
            {
                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetDepthCompareFunction, (nuint)ToCompareFunction(_depthTest.Func));
                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetDepthWriteEnabled, _depthTest.WriteEnable);

                _depthStencilState = MetalBindings.objc_msgSend(_device, MetalBindings.SelNewDepthStencilStateWithDescriptor, descriptor);
                _depthStencilState = MetalBindings.Retain(_depthStencilState);
                _depthDirty = false;
            }
            finally
            {
                MetalBindings.Release(descriptor);
            }
        }

        // ---- Draw ----

        private void DrawInternal(int count, int instanceCount, int first, bool indexed, int firstIndex = 0)
        {
            if (_program == null)
            {
                return;
            }

            nint pipelineState = GetOrCreatePipelineState();

            if (pipelineState == nint.Zero)
            {
                return;
            }

            if (!EnsureRenderPass())
            {
                return;
            }

            MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetRenderPipelineState, pipelineState);

            // Viewport.
            if (_viewports.Length > 0)
            {
                Viewport vp = _viewports[0];
                
                // Handle negative heights: flip Y and make height positive
                float x = vp.Region.X;
                float y = vp.Region.Y;
                float width = vp.Region.Width;
                float height = vp.Region.Height;
                
                if (height < 0)
                {
                    y += height;  // Adjust Y down by the (negative) height amount
                    height = -height;  // Make height positive
                }
                
                MTLViewport viewport = new(x, y, width, height, vp.DepthNear, vp.DepthFar);

                unsafe
                {
                    MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetViewport, &viewport);
                }
            }

            // Scissor.
            if (_scissors.Length > 0)
            {
                Rectangle<int> sc = _scissors[0];
                MTLScissorRect scissor = new((nuint)Math.Max(0, sc.X), (nuint)Math.Max(0, sc.Y), (nuint)Math.Max(0, sc.Width), (nuint)Math.Max(0, sc.Height));

                unsafe
                {
                    MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetScissorRect, &scissor);
                }
            }

            // Rasterizer state.
            ulong cullMode = MetalBindings.MTLCullModeNone;

            if (_cullEnabled)
            {
                cullMode = _cullFace switch
                {
                    Face.Front => MetalBindings.MTLCullModeFront,
                    Face.Back => MetalBindings.MTLCullModeBack,
                    _ => MetalBindings.MTLCullModeNone,
                };
            }

            ulong winding = _frontFace == FrontFace.Clockwise
                ? MetalBindings.MTLWindingClockwise
                : MetalBindings.MTLWindingCounterClockwise;

            MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetCullMode, (nuint)cullMode);
            MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetFrontFacingWinding, (nuint)winding);

            if (_rasterizerDiscard)
            {
                return;
            }

            // Depth/stencil state (only meaningful when the render pass has a depth attachment).
            if (_depthTarget != null)
            {
                EnsureDepthStencilState();

                if (_depthStencilState != nint.Zero)
                {
                    MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetDepthStencilState, _depthStencilState);
                }
            }

            // Vertex buffers.
            for (int i = 0; i < _vertexBuffers.Length; i++)
            {
                VertexBufferDescriptor vb = _vertexBuffers[i];
                nint buffer = _renderer.GetBuffer(vb.Buffer.Handle);

                if (buffer != nint.Zero)
                {
                    MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetVertexBufferOffsetAtIndex, buffer, (nuint)vb.Buffer.Offset, (nuint)i);
                }
            }

            // Uniform buffers (bind to every stage that uses the (set, binding)).
            foreach ((_, BufferAssignment assignment) in _uniformBuffers)
            {
                BindBuffer(ShaderStage.Vertex, assignment.Binding, assignment.Range, UniformBufferSet, Interop.SpirvCross.MslResourceKind.UniformBuffer);
                BindBuffer(ShaderStage.Fragment, assignment.Binding, assignment.Range, UniformBufferSet, Interop.SpirvCross.MslResourceKind.UniformBuffer);
            }

            // Storage buffers.
            foreach ((_, BufferAssignment assignment) in _storageBuffers)
            {
                BindBuffer(ShaderStage.Vertex, assignment.Binding, assignment.Range, StorageBufferSet, Interop.SpirvCross.MslResourceKind.StorageBuffer);
                BindBuffer(ShaderStage.Fragment, assignment.Binding, assignment.Range, StorageBufferSet, Interop.SpirvCross.MslResourceKind.StorageBuffer);
            }

            // Textures + samplers.
            BindTexturesAndSamplers(_texturesVertex, ShaderStage.Vertex);
            BindTexturesAndSamplers(_texturesFragment, ShaderStage.Fragment);

            // Images.
            BindImages(_imagesVertex, ShaderStage.Vertex);
            BindImages(_imagesFragment, ShaderStage.Fragment);

            // Draw.
            if (indexed)
            {
                nint indexBuffer = _renderer.GetBuffer(_indexBuffer.Handle);

                if (indexBuffer == nint.Zero)
                {
                    return;
                }

                ulong indexType = _indexType switch
                {
                    IndexType.UInt => MetalBindings.MTLIndexTypeUInt32,
                    _ => MetalBindings.MTLIndexTypeUInt16,
                };

                ulong primitiveType = ToPrimitiveType(_topology);

                MetalBindings.objc_msgSend_void(
                    _renderEncoder,
                    MetalBindings.SelDrawIndexedPrimitivesIndexCountIndexTypeIndexBufferIndexBufferOffsetInstanceCount,
                    (nuint)primitiveType,
                    (nuint)count,
                    (nuint)indexType,
                    indexBuffer,
                    (nuint)(_indexBuffer.Offset + firstIndex * (_indexType == IndexType.UInt ? 4 : 2)),
                    (nuint)instanceCount);
            }
            else
            {
                ulong primitiveType = ToPrimitiveType(_topology);

                MetalBindings.objc_msgSend_void(
                    _renderEncoder,
                    MetalBindings.SelDrawPrimitivesVertexStartVertexCountInstanceCount,
                    (nuint)primitiveType,
                    (nuint)first,
                    (nuint)count,
                    (nuint)instanceCount);
            }
        }

        private void BindBuffer(ShaderStage stage, int binding, BufferRange range, uint setIndex, Interop.SpirvCross.MslResourceKind kind)
        {
            uint mslIndex = _program.GetMslBinding(stage, setIndex, (uint)binding, kind, out _);

            if (mslIndex == uint.MaxValue)
            {
                return;
            }

            nint buffer = _renderer.GetBuffer(range.Handle);

            if (buffer == nint.Zero)
            {
                return;
            }

            if (stage == ShaderStage.Vertex)
            {
                MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetVertexBufferOffsetAtIndex, buffer, (nuint)range.Offset, (nuint)mslIndex);
            }
            else
            {
                MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetFragmentBufferOffsetAtIndex, buffer, (nuint)range.Offset, (nuint)mslIndex);
            }
        }

        private void BindTexturesAndSamplers(Dictionary<int, (ITexture Texture, ISampler Sampler)> bindings, ShaderStage stage)
        {
            foreach ((int binding, (ITexture texture, ISampler sampler)) in bindings)
            {
                uint mslIndex = _program.GetMslBinding(stage, TextureSet, (uint)binding, Interop.SpirvCross.MslResourceKind.Texture, out uint samplerIndex);

                if (mslIndex == uint.MaxValue)
                {
                    continue;
                }

                if (texture is MetalTexture metalTexture && metalTexture.TextureHandle != nint.Zero)
                {
                    if (stage == ShaderStage.Vertex)
                    {
                        MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetVertexTextureAtIndex, metalTexture.TextureHandle, (nuint)mslIndex);
                    }
                    else
                    {
                        MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetFragmentTextureAtIndex, metalTexture.TextureHandle, (nuint)mslIndex);
                    }
                }

                uint actualSamplerIndex = samplerIndex != uint.MaxValue ? samplerIndex : mslIndex;

                if (sampler is MetalSampler metalSampler && metalSampler.SamplerState != nint.Zero)
                {
                    if (stage == ShaderStage.Vertex)
                    {
                        MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetVertexSamplerStateAtIndex, metalSampler.SamplerState, (nuint)actualSamplerIndex);
                    }
                    else
                    {
                        MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetFragmentSamplerStateAtIndex, metalSampler.SamplerState, (nuint)actualSamplerIndex);
                    }
                }
            }
        }

        private void BindImages(Dictionary<int, ITexture> bindings, ShaderStage stage)
        {
            foreach ((int binding, ITexture texture) in bindings)
            {
                uint mslIndex = _program.GetMslBinding(stage, ImageSet, (uint)binding, Interop.SpirvCross.MslResourceKind.StorageImage, out _);

                if (mslIndex == uint.MaxValue || texture is not MetalTexture metalTexture || metalTexture.TextureHandle == nint.Zero)
                {
                    continue;
                }

                if (stage == ShaderStage.Vertex)
                {
                    MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetVertexTextureAtIndex, metalTexture.TextureHandle, (nuint)mslIndex);
                }
                else
                {
                    MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetFragmentTextureAtIndex, metalTexture.TextureHandle, (nuint)mslIndex);
                }
            }
        }

        // ---- IPipeline state setters ----

        public void SetProgram(IProgram program)
        {
            _program = program as MetalProgram;
        }

        public void SetRenderTargets(Span<ITexture> colors, ITexture depthStencil)
        {
            EndRenderPass();

            _colorTargetCount = colors.Length;
            _colorTargets = new ITexture[colors.Length];
            colors.CopyTo(_colorTargets);
            _depthTarget = depthStencil;
        }

        public void SetVertexAttribs(ReadOnlySpan<VertexAttribDescriptor> vertexAttribs)
        {
            _vertexAttribs = vertexAttribs.ToArray();
        }

        public void SetVertexBuffers(ReadOnlySpan<VertexBufferDescriptor> vertexBuffers)
        {
            _vertexBuffers = vertexBuffers.ToArray();
        }

        public void SetIndexBuffer(BufferRange buffer, IndexType type)
        {
            _indexBuffer = buffer;
            _indexType = type;
        }

        public void SetUniformBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            _uniformBuffers.Clear();

            foreach (BufferAssignment assignment in buffers)
            {
                _uniformBuffers[assignment.Binding] = assignment;
            }
        }

        public void SetStorageBuffers(ReadOnlySpan<BufferAssignment> buffers)
        {
            _storageBuffers.Clear();

            foreach (BufferAssignment assignment in buffers)
            {
                _storageBuffers[assignment.Binding] = assignment;
            }
        }

        public void SetTextureAndSampler(ShaderStage stage, int binding, ITexture texture, ISampler sampler)
        {
            Dictionary<int, (ITexture Texture, ISampler Sampler)> target = stage == ShaderStage.Vertex ? _texturesVertex : _texturesFragment;
            target[binding] = (texture, sampler);
        }

        public void SetImage(ShaderStage stage, int binding, ITexture texture)
        {
            Dictionary<int, ITexture> target = stage == ShaderStage.Vertex ? _imagesVertex : _imagesFragment;
            target[binding] = texture;
        }

        public void SetTextureArray(ShaderStage stage, int binding, ITextureArray array) { }

        public void SetTextureArraySeparate(ShaderStage stage, int setIndex, ITextureArray array) { }

        public void SetImageArray(ShaderStage stage, int binding, IImageArray array) { }

        public void SetImageArraySeparate(ShaderStage stage, int setIndex, IImageArray array) { }

        public void SetBlendState(int index, BlendDescriptor blend)
        {
            _blend = blend;
        }

        public void SetBlendState(AdvancedBlendDescriptor blend) { }

        public void SetDepthTest(DepthTestDescriptor depthTest)
        {
            _depthTest = depthTest;
            _depthDirty = true;
        }

        public void SetStencilTest(StencilTestDescriptor stencilTest) { }

        public void SetFaceCulling(bool enable, Face face)
        {
            _cullEnabled = enable;
            _cullFace = face;
        }

        public void SetFrontFace(FrontFace frontFace)
        {
            _frontFace = frontFace;
        }

        public void SetPrimitiveTopology(PrimitiveTopology topology)
        {
            _topology = topology;
        }

        public void SetViewports(ReadOnlySpan<Viewport> viewports)
        {
            _viewports = viewports.ToArray();
        }

        public void SetScissors(ReadOnlySpan<Rectangle<int>> regions)
        {
            _scissors = regions.ToArray();
        }

        public void SetDepthClamp(bool clamp)
        {
            _depthClamp = clamp;
        }

        public void SetRasterizerDiscard(bool discard)
        {
            _rasterizerDiscard = discard;
        }

        public void SetPolygonMode(PolygonMode frontMode, PolygonMode backMode) { }

        public void SetDepthBias(PolygonModeMask enables, float factor, float units, float clamp) { }

        public void SetPointParameters(float size, bool isProgramPointSize, bool enablePointSprite, Origin origin) { }

        public void SetAlphaTest(bool enable, float reference, CompareOp op) { }

        public void SetDepthMode(DepthMode mode) { }

        public void SetLineParameters(float width, bool smooth) { }

        public void SetLogicOpState(bool enable, LogicalOp op) { }

        public void SetMultisampleState(MultisampleDescriptor multisample) { }

        public void SetPatchParameters(int vertices, ReadOnlySpan<float> defaultOuterLevel, ReadOnlySpan<float> defaultInnerLevel) { }

        public void SetPrimitiveRestart(bool enable, int index) { }

        public void SetRenderTargetColorMasks(ReadOnlySpan<uint> componentMask)
        {
            _colorWriteMasks = componentMask.ToArray();
        }

        public void SetUserClipDistance(int index, bool enableClip) { }

        public void SetTransformFeedbackBuffers(ReadOnlySpan<BufferRange> buffers) { }

        public void BeginTransformFeedback(PrimitiveTopology topology) { }

        public void EndTransformFeedback() { }

        public void Draw(int vertexCount, int instanceCount, int firstVertex, int firstInstance)
        {
            DrawInternal(vertexCount, instanceCount, firstVertex, false);
        }

        public void DrawIndexed(int indexCount, int instanceCount, int firstIndex, int firstVertex, int firstInstance)
        {
            DrawInternal(indexCount, instanceCount, firstIndex, true);
        }

        public void DrawIndirect(BufferRange indirectBuffer) { }

        public void DrawIndexedIndirect(BufferRange indirectBuffer) { }

        public void DrawIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride) { }

        public void DrawIndexedIndirectCount(BufferRange indirectBuffer, BufferRange parameterBuffer, int maxDrawCount, int stride) { }

        public void DrawTexture(ITexture texture, ISampler sampler, Extents2DF srcRegion, Extents2DF dstRegion) { }

        public void DispatchCompute(int groupsX, int groupsY, int groupsZ)
        {
            if (_program?.ComputeFunction == nint.Zero)
            {
                return;
            }

            nint commandBuffer = MetalBindings.Retain(MetalBindings.objc_msgSend(_commandQueue, MetalBindings.SelCommandBufferWithUnretainedReferences));
            nint encoder = nint.Zero;

            try
            {
                nint pipelineState = MetalBindings.objc_msgSend(
                    _device,
                    MetalBindings.SelNewComputePipelineStateWithFunctionError,
                    _program.ComputeFunction,
                    nint.Zero);

                if (pipelineState == nint.Zero)
                {
                    return;
                }

                try
                {
                    encoder = MetalBindings.objc_msgSend(commandBuffer, MetalBindings.SelComputeCommandEncoder);

                    if (encoder == nint.Zero)
                    {
                        return;
                    }

                    MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelSetComputePipelineState, pipelineState);

                    foreach ((_, BufferAssignment assignment) in _storageBuffers)
                    {
                        BindComputeBuffer(encoder, assignment.Binding, assignment.Range);
                    }

                    MTLSize threadgroups = new((nuint)groupsX, (nuint)groupsY, (nuint)groupsZ);
                    MTLSize threadsPerGroup = new(64, 1, 1);

                    unsafe
                    {
                        MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelDispatchThreadgroupsThreadsPerThreadgroup, &threadgroups, &threadsPerGroup);
                    }

                    MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelEndEncoding);
                }
                finally
                {
                    MetalBindings.Release(pipelineState);
                }

                MetalBindings.objc_msgSend_void(commandBuffer, MetalBindings.SelCommit);
            }
            finally
            {
                MetalBindings.Release(encoder);
                MetalBindings.Release(commandBuffer);
            }
        }

        private void BindComputeBuffer(nint encoder, int binding, BufferRange range)
        {
            uint mslIndex = _program.GetMslBinding(ShaderStage.Compute, StorageBufferSet, (uint)binding, Interop.SpirvCross.MslResourceKind.StorageBuffer, out _);

            if (mslIndex == uint.MaxValue)
            {
                return;
            }

            nint buffer = _renderer.GetBuffer(range.Handle);

            if (buffer != nint.Zero)
            {
                MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelSetComputeBufferOffsetAtIndex, buffer, (nuint)range.Offset, (nuint)mslIndex);
            }
        }

        public void Barrier() { }

        public void CommandBufferBarrier() { }

        public void TextureBarrier() { }

        public void TextureBarrierTiled() { }

        public void ClearRenderTargetColor(int index, int layer, int layerCount, uint componentMask, ColorF color)
        {
            // Record the clear color; EnsureRenderPass applies it (load action Clear)
            // when the render pass is next built.
            _clearColor = color;
            _hasClearColor = true;
        }

        public void ClearRenderTargetDepthStencil(
            int layer,
            int layerCount,
            float depthValue,
            bool depthMask,
            int stencilValue,
            int stencilMask)
        {
            _depthClearValue = depthValue;
            _stencilClearValue = stencilValue;
            _hasDepthClear = true;
        }

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

        public bool TryHostConditionalRendering(ICounterEvent value, ulong compare, bool isEqual) => false;

        public bool TryHostConditionalRendering(ICounterEvent value, ICounterEvent compare, bool isEqual) => false;

        public void EndHostConditionalRendering() { }

        public void CreateSync(ulong id)
        {
            _currentSync = id;
        }

        public ulong GetCurrentSync()
        {
            return _currentSync;
        }

        // ---- Enum conversions ----

        private static ulong ToPrimitiveType(PrimitiveTopology topology)
        {
            return topology switch
            {
                PrimitiveTopology.Points => MetalBindings.MTLPrimitiveTypePoint,
                PrimitiveTopology.Lines => MetalBindings.MTLPrimitiveTypeLine,
                PrimitiveTopology.LineStrip => MetalBindings.MTLPrimitiveTypeLineStrip,
                PrimitiveTopology.TriangleStrip => MetalBindings.MTLPrimitiveTypeTriangleStrip,
                _ => MetalBindings.MTLPrimitiveTypeTriangle,
            };
        }

        private static ulong ToTopologyClass(PrimitiveTopology topology)
        {
            return topology switch
            {
                PrimitiveTopology.Points => MetalBindings.MTLPrimitiveTopologyClassPoint,
                PrimitiveTopology.Lines or PrimitiveTopology.LineLoop or PrimitiveTopology.LineStrip => MetalBindings.MTLPrimitiveTopologyClassLine,
                PrimitiveTopology.Triangles or PrimitiveTopology.TriangleStrip or PrimitiveTopology.TriangleFan => MetalBindings.MTLPrimitiveTopologyClassTriangle,
                _ => MetalBindings.MTLPrimitiveTopologyClassUnspecified,
            };
        }

        private static ulong ToCompareFunction(CompareOp op)
        {
            return op switch
            {
                CompareOp.Never => MetalBindings.MTLCompareFunctionNever,
                CompareOp.Less => MetalBindings.MTLCompareFunctionLess,
                CompareOp.Equal => MetalBindings.MTLCompareFunctionEqual,
                CompareOp.LessOrEqual => MetalBindings.MTLCompareFunctionLessEqual,
                CompareOp.Greater => MetalBindings.MTLCompareFunctionGreater,
                CompareOp.NotEqual => MetalBindings.MTLCompareFunctionNotEqual,
                CompareOp.GreaterOrEqual => MetalBindings.MTLCompareFunctionGreaterEqual,
                _ => MetalBindings.MTLCompareFunctionAlways,
            };
        }

        private static ulong ToBlendOp(BlendOp op)
        {
            return op switch
            {
                BlendOp.Subtract => MetalBindings.MTLBlendOperationSubtract,
                BlendOp.ReverseSubtract => MetalBindings.MTLBlendOperationReverseSubtract,
                BlendOp.Minimum => MetalBindings.MTLBlendOperationMin,
                BlendOp.Maximum => MetalBindings.MTLBlendOperationMax,
                _ => MetalBindings.MTLBlendOperationAdd,
            };
        }

        private static ulong ToBlendFactor(BlendFactor factor)
        {
            return factor switch
            {
                BlendFactor.One => MetalBindings.MTLBlendFactorOne,
                BlendFactor.SrcColor => MetalBindings.MTLBlendFactorSourceColor,
                BlendFactor.OneMinusSrcColor => MetalBindings.MTLBlendFactorOneMinusSourceColor,
                BlendFactor.SrcAlpha => MetalBindings.MTLBlendFactorSourceAlpha,
                BlendFactor.OneMinusSrcAlpha => MetalBindings.MTLBlendFactorOneMinusSourceAlpha,
                BlendFactor.DstColor => MetalBindings.MTLBlendFactorDestinationColor,
                BlendFactor.OneMinusDstColor => MetalBindings.MTLBlendFactorOneMinusDestinationColor,
                BlendFactor.DstAlpha => MetalBindings.MTLBlendFactorDestinationAlpha,
                BlendFactor.OneMinusDstAlpha => MetalBindings.MTLBlendFactorOneMinusDestinationAlpha,
                BlendFactor.SrcAlphaSaturate => MetalBindings.MTLBlendFactorSourceAlphaSaturated,
                BlendFactor.ConstantColor => MetalBindings.MTLBlendFactorBlendColor,
                BlendFactor.OneMinusConstantColor => MetalBindings.MTLBlendFactorOneMinusBlendColor,
                BlendFactor.ConstantAlpha => MetalBindings.MTLBlendFactorBlendAlpha,
                BlendFactor.OneMinusConstantAlpha => MetalBindings.MTLBlendFactorOneMinusBlendAlpha,
                BlendFactor.Src1Color => MetalBindings.MTLBlendFactorSource1Color,
                BlendFactor.OneMinusSrc1Color => MetalBindings.MTLBlendFactorOneMinusSource1Color,
                BlendFactor.Src1Alpha => MetalBindings.MTLBlendFactorSource1Alpha,
                BlendFactor.OneMinusSrc1Alpha => MetalBindings.MTLBlendFactorOneMinusSource1Alpha,
                _ => MetalBindings.MTLBlendFactorZero,
            };
        }

        public void Dispose()
        {
            EndRenderPass();

            foreach ((_, nint state) in _pipelineCache)
            {
                MetalBindings.Release(state);
            }

            _pipelineCache.Clear();

            if (_depthStencilState != nint.Zero)
            {
                MetalBindings.Release(_depthStencilState);
                _depthStencilState = nint.Zero;
            }

            if (_syncEvent != nint.Zero)
            {
                MetalBindings.Release(_syncEvent);
                _syncEvent = nint.Zero;
            }

            GC.SuppressFinalize(this);
        }
    }
}
