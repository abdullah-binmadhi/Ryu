using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Metal.Interop;
using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Generic;
using System.Linq;
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

        // Offset vertex buffer bindings in the M4 argument table so they never clash
        // with SPIRV-Cross auto-assigned uniform/storage buffers (which start at 0).
        // 8 allows 8 UBOs/SSBOs (slots 0..7) and 16 vertex buffers (slots 8..23 < 31).
        private const uint VertexBufferSlotOffset = 8;
        private int _drawLogCount;
        private int _setTargetsLogCount;

        private readonly MetalRenderer _renderer;
        private readonly nint _device;
        private readonly nint _commandQueue;

        // Program + fixed-function state that feeds pipeline creation.
        private MetalProgram _program;
        private ITexture[] _colorTargets = Array.Empty<ITexture>();
        private int _colorTargetCount;
        private static readonly bool s_forceNoCull = Environment.GetEnvironmentVariable("RYU_METAL_FORCE_NO_CULL") == "1";
        private const int MaxRenderTargets = 8;
        private ITexture _depthTarget;
        private readonly BlendDescriptor[] _blends = new BlendDescriptor[MaxRenderTargets];
        private MultisampleDescriptor _multisample;
        private StencilTestDescriptor _stencilTest;
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
        private bool _depthBiasEnabled;
        private float _depthBiasFactor;
        private float _depthBiasUnits;
        private float _depthBiasClamp;
        private bool _depthStencilDirty = true;
        private nint _depthStencilState;

        // Draw-time bound resources.
        private readonly Dictionary<int, BufferAssignment> _uniformBuffers = new();
        private readonly Dictionary<int, BufferAssignment> _storageBuffers = new();
        private readonly Dictionary<int, (ITexture Texture, ISampler Sampler)> _texturesVertex = new();
        private readonly Dictionary<int, (ITexture Texture, ISampler Sampler)> _texturesFragment = new();
        private readonly Dictionary<int, (ITexture Texture, ISampler Sampler)> _texturesCompute = new();
        private readonly Dictionary<int, ITexture> _imagesVertex = new();
        private readonly Dictionary<int, ITexture> _imagesFragment = new();
        private readonly Dictionary<int, ITexture> _imagesCompute = new();
        private BufferRange _indexBuffer;
        private IndexType _indexType;
        private Viewport[] _viewports = Array.Empty<Viewport>();
        private Rectangle<int>[] _scissors = Array.Empty<Rectangle<int>>();

        // Active render pass.
        private nint _commandBuffer;
        private int _commandBufferAllocatorIndex;
        private nint _renderEncoder;
        private nint _renderPassDescriptor;

        // M4: ended render-pass command buffers accumulate here and are submitted
        // together as one commit:count: batch on FlushFrame (block-free shared-event
        // wait), replacing the per-pass commit+WaitUntilCompleted M3 path.
        private readonly List<nint> _frameBuffers = new();
        private readonly List<nint> _framePassDescriptors = new();
        private readonly List<int> _frameAllocatorIndices = new();
        private readonly List<MetalTelemetryDump.PassRecord> _framePassRecords = new();
        private int _frameNumber;
        private bool _isFlushing;

        // M4: per-stage argument tables. The M4 render context binds ALL resources
        // (vertex buffers, uniforms, storage, textures, samplers, images) through a
        // device-side MTL4ArgumentTable via setArgumentTable:atStages:, replacing the
        // M3 per-encoder setVertexBuffer/setVertexTexture/setFragmentTexture calls.
        private nint _argumentTableVertex;
        private nint _argumentTableFragment;
        private nint _argumentTableCompute;

        // Pipeline state cache.
        private readonly Dictionary<string, nint> _pipelineCache = new();
        private ulong _currentSync;

        // Probe 3d (RYU_METAL_CONST_OVERLAY): draws a fullscreen constant-color
        // triangle over color target 0 on the first draw of the run, after the real
        // draw is encoded. A green/magenta readback proves the M4 encoder +
        // attachments + commit + readback path emits output; persisting black means
        // the failure is upstream of shader resources entirely.
        private static readonly (float R, float G, float B, float A) _overlayColor = ParseOverlayColor();
        private readonly Dictionary<string, nint> _overlayPipelineCache = new();
        private nint _overlayLibrary;
        private nint _overlayCompiler;
        private nint _overlayTaskOptions;
        private nint _overlayVertexFunction;
        private nint _overlayFragmentFunction;
        private nint _overlayColorBuffer;
        private nint _overlayArgumentTableVertex;
        private nint _overlayArgumentTableFragment;

        private bool _miniM4Tested;
        private int _flushLogCount;
        private int _frameDrawCount;
        private int _totalDrawCount;
        private int _passLogCount;
        private bool _probeIndexed;
        private int _probeCount;
        private int _probeFirstIndex;
        private int _probeInstanceCount;
        private readonly HashSet<int> _gameDrawProbeFiredDraws = new();

        // M6: host sync via MTLSharedEvent + clear color state.
        private nint _syncEvent;
        private bool _hasClearColor;
        private uint _clearColorMask;
        private readonly ColorF[] _clearColors = new ColorF[8];
        private bool _hasDepthClear;
        private float _depthClearValue;
        private int _stencilClearValue;
        private uint[] _colorWriteMasks = Array.Empty<uint>();

        public MetalPipeline(MetalRenderer renderer, nint device, nint commandQueue)
        {
            _renderer = renderer;
            _device = device;
            _commandQueue = commandQueue;

            _syncEvent = MetalBindings.objc_msgSend(_device, MetalBindings.SelNewSharedEvent);
        }

        private struct InFlightBatch
        {
            public ulong SignalValue;
            public List<nint> Buffers;
            public List<nint> PassDescriptors;
            public List<int> AllocatorIndices;
        }

        private readonly List<InFlightBatch> _inFlightBatches = new();

        private void RetireCompletedBatches()
        {
            if (_inFlightBatches.Count == 0) return;

            ulong completed = _renderer.M4Queue.SignaledValue;

            for (int i = _inFlightBatches.Count - 1; i >= 0; i--)
            {
                var batch = _inFlightBatches[i];
                if (batch.SignalValue <= completed)
                {
                    for (int b = 0; b < batch.Buffers.Count; b++)
                    {
                        MetalBindings.Release(batch.Buffers[b]);
                    }
                    for (int p = 0; p < batch.PassDescriptors.Count; p++)
                    {
                        if (batch.PassDescriptors[p] != nint.Zero)
                        {
                            MetalBindings.Release(batch.PassDescriptors[p]);
                        }
                    }
                    for (int a = 0; a < batch.AllocatorIndices.Count; a++)
                    {
                        _renderer.M4AllocatorPool.Release(batch.AllocatorIndices[a]);
                    }

                    _inFlightBatches.RemoveAt(i);
                }
            }
        }

        public void FlushFrame()
        {
            if (_isFlushing)
            {
                return;
            }

            _isFlushing = true;
            try
            {
                if (_clearColorMask != 0 || _hasDepthClear)
                {
                    EnsureRenderPass();
                }

                EndRenderPass();

                RetireCompletedBatches();

                if (_frameBuffers.Count > 0)
                {
                    bool hasLargeTarget = _colorTargets.OfType<MetalTexture>().Any(t => t.Width >= 960 || t.Height >= 540);
                    if (_flushLogCount < 100 || (_flushLogCount % 300 == 0))
                    {
                        string target = (_colorTargets.Length > 0 && _colorTargets[0] is MetalTexture fmt)
                            ? $"0x{fmt.TextureHandle:X} {fmt.Width}x{fmt.Height} {fmt.Format}"
                            : "none";
                        string allTargets = string.Join(" | ", _colorTargets
                            .OfType<MetalTexture>()
                            .Select(t => $"0x{t.TextureHandle:X} {t.Width}x{t.Height} {t.Format}"));
                        Logger.Warning?.Print(LogClass.Gpu, $"[FLUSH] #{++_flushLogCount}: committedBuffers={_frameBuffers.Count} passDescs={_framePassDescriptors.Count} target0={target} allTargets=[{allTargets}] drawsThisBatch={_frameDrawCount}");
                    }
                    else
                    {
                        _flushLogCount++;
                    }

                    ulong signal = _renderer.M4Queue.CommitBatch(CollectionsMarshal.AsSpan(_frameBuffers));

                    // Sample writer probes periodically without stalling every frame
                    if (hasLargeTarget && _frameDrawCount > 0 && (_flushLogCount <= 20 || (_flushLogCount % 120 == 0)))
                    {
                        Metal4Bindings.m4_wait_event_bool(
                            _renderer.M4Queue.CompletionEvent,
                            Metal4Bindings.SelWaitUntilSignaledValueTimeoutMS,
                            signal,
                            5000);

                        foreach (var target in _colorTargets.OfType<MetalTexture>())
                        {
                            if (target.Width >= 960 && target.Height >= 540 && target.TextureHandle != nint.Zero)
                            {
                                byte[] sample = new byte[64];
                                MTLRegion r = new(0, 0, 0, 4, 4, 1);
                                unsafe
                                {
                                    fixed (byte* sp = sample)
                                    {
                                        MetalBindings.objc_msgSend_void(target.TextureHandle, MetalBindings.SelGetBytesBytesPerRowFromRegionMipmapLevel, sp, (nuint)(4 * 4), &r, 0);
                                    }
                                }
                                int nonzero = 0;
                                for (int i = 0; i < sample.Length; i++) if (sample[i] != 0) nonzero++;
                                Logger.Warning?.Print(LogClass.Gpu, $"[WRITER_PROBE] passTarget=0x{target.TextureHandle:X} {target.Width}x{target.Height} {target.Format} nonzeroBytes={nonzero}/{sample.Length} sample0=[{sample[0]},{sample[1]},{sample[2]},{sample[3]}] draws={_frameDrawCount}");
                            }
                        }
                    }

                    _inFlightBatches.Add(new InFlightBatch
                    {
                        SignalValue = signal,
                        Buffers = new List<nint>(_frameBuffers),
                        PassDescriptors = new List<nint>(_framePassDescriptors),
                        AllocatorIndices = new List<int>(_frameAllocatorIndices)
                    });

                    _frameNumber++;
                    if (MetalTelemetryDump.IsEnabled && _framePassRecords.Count > 0)
                    {
                        MetalTelemetryDump.QueueFrameDump(
                            _frameNumber,
                            _renderer?.M4Queue?.CompletionEvent ?? nint.Zero,
                            signal,
                            _framePassRecords);
                        _framePassRecords.Clear();
                    }

                    _frameBuffers.Clear();
                    _framePassDescriptors.Clear();
                    _frameAllocatorIndices.Clear();
                    _frameDrawCount = 0;
                }
            }
            finally
            {
                _isFlushing = false;
            }
        }

        // ---- Argument tables (M4 resource binding model) ----

        private bool EnsureArgumentTables()
        {
            if (_argumentTableVertex != nint.Zero && _argumentTableFragment != nint.Zero)
            {
                return true;
            }

            // Capacity mirrors the Metal 4 caps: 31 buffers, 64 textures, 16 samplers
            // per stage (matches GetCapabilities() maxima).
            nint descriptor = Metal4Bindings.Metal4New("MTL4ArgumentTableDescriptor");

            if (descriptor == nint.Zero)
            {
                return false;
            }

            try
            {
                Metal4Bindings.m4_msgSend_void(descriptor, Metal4Bindings.SelSetMaxBufferBindCount, (nuint)31);
                Metal4Bindings.m4_msgSend_void(descriptor, Metal4Bindings.SelSetMaxTextureBindCount, (nuint)64);
                Metal4Bindings.m4_msgSend_void(descriptor, Metal4Bindings.SelSetMaxSamplerStateBindCount, (nuint)16);

                _argumentTableVertex = MetalBindings.objc_msgSend(_device, Metal4Bindings.SelNewArgumentTableWithDescriptorError, descriptor, nint.Zero);
                _argumentTableFragment = MetalBindings.objc_msgSend(_device, Metal4Bindings.SelNewArgumentTableWithDescriptorError, descriptor, nint.Zero);
            }
            finally
            {
                MetalBindings.Release(descriptor);
            }

            return _argumentTableVertex != nint.Zero && _argumentTableFragment != nint.Zero;
        }

        private bool EnsureComputeArgumentTable()
        {
            if (_argumentTableCompute != nint.Zero)
            {
                return true;
            }

            nint descriptor = Metal4Bindings.Metal4New("MTL4ArgumentTableDescriptor");

            if (descriptor == nint.Zero)
            {
                return false;
            }

            try
            {
                Metal4Bindings.m4_msgSend_void(descriptor, Metal4Bindings.SelSetMaxBufferBindCount, (nuint)31);
                Metal4Bindings.m4_msgSend_void(descriptor, Metal4Bindings.SelSetMaxTextureBindCount, (nuint)64);
                Metal4Bindings.m4_msgSend_void(descriptor, Metal4Bindings.SelSetMaxSamplerStateBindCount, (nuint)16);

                _argumentTableCompute = MetalBindings.objc_msgSend(_device, Metal4Bindings.SelNewArgumentTableWithDescriptorError, descriptor, nint.Zero);
            }
            finally
            {
                MetalBindings.Release(descriptor);
            }

            return _argumentTableCompute != nint.Zero;
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

            RetireCompletedBatches();
            int allocatorIndex = _renderer.M4AllocatorPool.Acquire();

            if (allocatorIndex < 0)
            {
                if (_frameBuffers.Count > 0)
                {
                    FlushFrame();
                }

                if (_inFlightBatches.Count > 0)
                {
                    Metal4Bindings.m4_wait_event_bool(_renderer.M4Queue.CompletionEvent, Metal4Bindings.SelWaitUntilSignaledValueTimeoutMS, _inFlightBatches[0].SignalValue, 5);
                    RetireCompletedBatches();
                }

                allocatorIndex = _renderer.M4AllocatorPool.Acquire();
                if (allocatorIndex < 0)
                {
                    return false;
                }
            }

            nint allocator = _renderer.M4AllocatorPool.GetAllocatorHandle(allocatorIndex);

            _commandBuffer = _renderer.M4Queue.BeginCommandBuffer(_renderer.DeviceHandle, allocator);

            if (_commandBuffer == nint.Zero)
            {
                _renderer.M4AllocatorPool.Release(allocatorIndex);
                return false;
            }

            _commandBufferAllocatorIndex = allocatorIndex;
            _renderPassDescriptor = Metal4Bindings.Metal4New("MTL4RenderPassDescriptor");

            if (_renderPassDescriptor == nint.Zero)
            {
                return false;
            }

            nint colorAttachments = MetalBindings.objc_msgSend(_renderPassDescriptor, MetalBindings.SelColorAttachments);
            List<string> attachmentActions = new();

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

                bool shouldClear = (_clearColorMask & (1u << i)) != 0;

                nuint loadAction = shouldClear ? (nuint)MetalBindings.MTLLoadActionClear : (nuint)MetalBindings.MTLLoadActionLoad;

                MetalBindings.objc_msgSend_void(attachment, MetalBindings.SelSetTexture, target.TextureHandle);
                MetalBindings.objc_msgSend_void(attachment, MetalBindings.SelSetLoadAction, loadAction);
                MetalBindings.objc_msgSend_void(attachment, MetalBindings.SelSetStoreAction, (nuint)MetalBindings.MTLStoreActionStore);

                attachmentActions.Add($"{i}:{(shouldClear ? "Clear" : "Load")}/Store");

                if (shouldClear)
                {
                    MTLColor clearColor;

                    clearColor = new(_clearColors[i].Red, _clearColors[i].Green, _clearColors[i].Blue, _clearColors[i].Alpha);

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
                    nuint loadAction = _hasDepthClear ? (nuint)MetalBindings.MTLLoadActionClear : (nuint)MetalBindings.MTLLoadActionLoad;

                    MetalBindings.objc_msgSend_void(depthAttachment, MetalBindings.SelSetTexture, depthTarget.TextureHandle);
                    MetalBindings.objc_msgSend_void(depthAttachment, MetalBindings.SelSetLoadAction, loadAction);
                    MetalBindings.objc_msgSend_void(depthAttachment, MetalBindings.SelSetStoreAction, (nuint)MetalBindings.MTLStoreActionStore);

                    attachmentActions.Add($"depth:{(_hasDepthClear ? "Clear" : "Load")}/Store");

                    if (_hasDepthClear)
                    {
                        MetalBindings.objc_msgSend_void(depthAttachment, MetalBindings.SelSetClearDepth, (double)_depthClearValue);
                    }
                }

                if (MetalFormats.HasStencil(depthTarget.Format))
                {
                    nint stencilAttachment = MetalBindings.objc_msgSend(_renderPassDescriptor, MetalBindings.SelStencilAttachment);
                    if (stencilAttachment != nint.Zero)
                    {
                        nuint loadAction = _hasDepthClear ? (nuint)MetalBindings.MTLLoadActionClear : (nuint)MetalBindings.MTLLoadActionLoad;

                        MetalBindings.objc_msgSend_void(stencilAttachment, MetalBindings.SelSetTexture, depthTarget.TextureHandle);
                        MetalBindings.objc_msgSend_void(stencilAttachment, MetalBindings.SelSetLoadAction, loadAction);
                        MetalBindings.objc_msgSend_void(stencilAttachment, MetalBindings.SelSetStoreAction, (nuint)MetalBindings.MTLStoreActionStore);

                        attachmentActions.Add($"stencil:{(_hasDepthClear ? "Clear" : "Load")}/Store");

                        if (_hasDepthClear)
                        {
                            MetalBindings.objc_msgSend_void(stencilAttachment, MetalBindings.SelSetClearStencil, (nuint)(uint)_stencilClearValue);
                        }
                    }
                }
            }

            _renderEncoder = MetalBindings.objc_msgSend(_commandBuffer, MetalBindings.SelRenderCommandEncoderWithDescriptor, _renderPassDescriptor);
            
            if (_passLogCount < 100 || (_passLogCount % 600 == 0))
            {
                _passLogCount++;
                string passTargets = string.Join(" | ", _colorTargets
                    .OfType<MetalTexture>()
                    .Select(t => $"0x{t.TextureHandle:X} {t.Width}x{t.Height} {t.Format}"));
                string depthT = (_depthTarget is MetalTexture dt && dt.TextureHandle != nint.Zero)
                    ? $" depth=0x{dt.TextureHandle:X} {dt.Width}x{dt.Height} {dt.Format}"
                    : "";
                Logger.Warning?.Print(LogClass.Gpu, $"[PASS] #{_passLogCount} targets=[{passTargets}]{depthT} actions=[{string.Join(" | ", attachmentActions)}] draws={_frameDrawCount}");
            }

            if (MetalTelemetryDump.IsEnabled)
            {
                var passRecord = new MetalTelemetryDump.PassRecord
                {
                    PassIndex = _framePassRecords.Count
                };

                for (int i = 0; i < _colorTargetCount; i++)
                {
                    if (_colorTargets[i] is MetalTexture target && target.TextureHandle != nint.Zero)
                    {
                        bool shouldClear = (_clearColorMask & (1u << i)) != 0;
                        passRecord.ColorAttachments.Add(new MetalTelemetryDump.AttachmentRecord
                        {
                            TextureHandle = target.TextureHandle,
                            Width = target.Width,
                            Height = target.Height,
                            Format = target.Format,
                            LoadAction = shouldClear ? "Clear" : "Load",
                            StoreAction = "Store",
                            ClearColor = _clearColors[i]
                        });
                    }
                }

                if (_depthTarget is MetalTexture dTarget && dTarget.TextureHandle != nint.Zero)
                {
                    passRecord.DepthAttachment = new MetalTelemetryDump.DepthAttachmentRecord
                    {
                        TextureHandle = dTarget.TextureHandle,
                        Width = dTarget.Width,
                        Height = dTarget.Height,
                        Format = dTarget.Format,
                        LoadAction = _hasDepthClear ? "Clear" : "Load",
                        StoreAction = "Store",
                        DepthClear = _depthClearValue
                    };
                }

                _framePassRecords.Add(passRecord);
            }

            // Consumed clears for this pass
            _clearColorMask = 0;
            _hasDepthClear = false;

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
                // MTL4 command buffers do not implement the legacy MTLCommandBuffer
                // encodeSignalEvent:value: selector. The MTL4 queue signals its shared
                // event after commit:count:, so synchronization must remain queue-level.
                // Sending the legacy selector here crashes with NSInvalidArgumentException
                // on MTL4DebugCommandBuffer.

                // End and accumulate: the whole frame is submitted as one commit:count:
                // batch on FlushFrame, with a block-free shared-event wait.
                _renderer.M4Queue.EndCommandBuffer(_commandBuffer);
                _frameBuffers.Add(_commandBuffer);
                _framePassDescriptors.Add(_renderPassDescriptor);
                _frameAllocatorIndices.Add(_commandBufferAllocatorIndex);
                _commandBuffer = nint.Zero;
                _renderPassDescriptor = nint.Zero;
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

            nint descriptor = nint.Zero;
            nint vertexFunctionDescriptor = nint.Zero;
            nint fragmentFunctionDescriptor = nint.Zero;
            nint m4VertexLibrary = _program.GetM4Library(ShaderStage.Vertex);
            nint m4FragmentLibrary = _program.GetM4Library(ShaderStage.Fragment);

            if (m4VertexLibrary == nint.Zero || m4FragmentLibrary == nint.Zero)
            {
                _pipelineCache[key] = nint.Zero;
                Logger.Warning?.Print(LogClass.Gpu, "MetalPipeline: MTL4 shader library missing; caching failed pipeline state");
                return nint.Zero;
            }

            try
            {
                // MTL4 render encoders require a pipeline state produced by MTL4Compiler.
                // A classic MTLRenderPipelineState may compile successfully but does not
                // rasterize on the MTL4 encoder, which was the source of the black frame.
                vertexFunctionDescriptor = Metal4Bindings.Metal4New("MTL4LibraryFunctionDescriptor");
                fragmentFunctionDescriptor = Metal4Bindings.Metal4New("MTL4LibraryFunctionDescriptor");

                MetalBindings.objc_msgSend_void(vertexFunctionDescriptor, Metal4Bindings.SelSetName, MetalBindings.CreateNSString(_program.GetM4EntryPoint(ShaderStage.Vertex)));
                MetalBindings.objc_msgSend_void(vertexFunctionDescriptor, Metal4Bindings.SelSetLibrary, m4VertexLibrary);
                MetalBindings.objc_msgSend_void(fragmentFunctionDescriptor, Metal4Bindings.SelSetName, MetalBindings.CreateNSString(_program.GetM4EntryPoint(ShaderStage.Fragment)));
                MetalBindings.objc_msgSend_void(fragmentFunctionDescriptor, Metal4Bindings.SelSetLibrary, m4FragmentLibrary);

                descriptor = Metal4Bindings.Metal4New("MTL4RenderPipelineDescriptor");
                MetalBindings.objc_msgSend_void(descriptor, Metal4Bindings.SelSetVertexFunctionDescriptor, vertexFunctionDescriptor);
                MetalBindings.objc_msgSend_void(descriptor, Metal4Bindings.SelSetFragmentFunctionDescriptor, fragmentFunctionDescriptor);

                ConfigureVertexDescriptor(descriptor);
                ConfigureColorAttachments(descriptor);

                if (_multisample.AlphaToCoverageEnable)
                {
                    MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetAlphaToCoverageEnabled, (nuint)1);
                }

                if (_multisample.AlphaToOneEnable)
                {
                    MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetAlphaToOneEnabled, (nuint)1);
                }

                // MTL4RenderPipelineDescriptor does not expose the legacy depth
                // attachment pixel-format property. MTL4 derives depth format from
                // the depth texture supplied by the render pass.

                nint nsError = nint.Zero;
                nint state;
                unsafe
                {
                    state = MetalBindings.objc_msgSend(
                        _program.M4Compiler,
                        Metal4Bindings.SelNewRenderPipelineStateWithDescriptorCompilerTaskOptionsError,
                        descriptor,
                        _program.M4TaskOptions,
                        (nint)(&nsError));
                }

                if (state != nint.Zero)
                {
                    _pipelineCache[key] = state;
                }
                else
                {
                    _pipelineCache[key] = nint.Zero;
                    string err = MetalBindings.GetErrorDescription(nsError);
                    Logger.Warning?.Print(LogClass.Gpu, $"MetalPipeline: MTL4 pipeline-state creation failed (cached): {err}");

                    if (_framePassRecords.Count > 0)
                    {
                        _framePassRecords[^1].PsoStatus = "Failed_PipelineCompilation";
                    }

                    MetalTelemetryDump.DumpFailedPipeline(key, descriptor, err);
                }

                return state;
            }
            finally
            {
                MetalBindings.Release(descriptor);
                MetalBindings.Release(vertexFunctionDescriptor);
                MetalBindings.Release(fragmentFunctionDescriptor);
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

            for (int i = 0; i < _colorTargetCount; i++)
            {
                BlendDescriptor b = i < _blends.Length ? _blends[i] : default;
                sb.Append(b.Enable ? "B1" : "B0");
                sb.Append('/');
                sb.Append(((int)b.ColorOp).ToString());
                sb.Append('/');
                sb.Append(((int)b.AlphaOp).ToString());
                sb.Append('/');
                sb.Append(((int)b.ColorSrcFactor).ToString());
                sb.Append('/');
                sb.Append(((int)b.ColorDstFactor).ToString());
                sb.Append('/');
                sb.Append(((int)b.AlphaSrcFactor).ToString());
                sb.Append('/');
                sb.Append(((int)b.AlphaDstFactor).ToString());
                sb.Append('/');
            }

            sb.Append(_multisample.AlphaToCoverageEnable ? "A2C1" : "A2C0");
            sb.Append('/');
            sb.Append(_multisample.AlphaToOneEnable ? "A1_1" : "A1_0");
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

            sb.Append('/');

            for (int i = 0; i < _colorTargetCount; i++)
            {
                ulong mask = i < _colorWriteMasks.Length ? ToMtlColorWriteMask(_colorWriteMasks[i]) : 0xF;
                sb.Append(mask.ToString("x"));
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
                    uint slot = VertexBufferSlotOffset + (uint)attrib.BufferIndex;
                    if (slot < 31)
                    {
                        MetalBindings.objc_msgSend_void(attribute, MetalBindings.SelSetBufferIndex, (nuint)slot);
                    }
                }

                // Vertex buffer layouts: stride + step function from the bound vertex buffers.
                for (int i = 0; i < _vertexBuffers.Length; i++)
                {
                    uint slot = VertexBufferSlotOffset + (uint)i;
                    if (slot >= 31)
                    {
                        break;
                    }

                    VertexBufferDescriptor vb = _vertexBuffers[i];
                    nint layout = MetalBindings.objc_msgSend(layouts, MetalBindings.SelObjectAtIndexedSubscript, (nuint)slot);

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

        private static ulong ToMtlColorWriteMask(uint galMask)
        {
            ulong mtlMask = 0;
            if ((galMask & 1u) != 0) mtlMask |= MetalBindings.MTLColorWriteMaskRed;   // 8
            if ((galMask & 2u) != 0) mtlMask |= MetalBindings.MTLColorWriteMaskGreen; // 4
            if ((galMask & 4u) != 0) mtlMask |= MetalBindings.MTLColorWriteMaskBlue;  // 2
            if ((galMask & 8u) != 0) mtlMask |= MetalBindings.MTLColorWriteMaskAlpha; // 1
            return mtlMask;
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

                MetalBindings.objc_msgSend_void(attachment, Metal4Bindings.SelSetPixelFormat, (nuint)pixelFormat);
                BlendDescriptor blend = i < _blends.Length ? _blends[i] : default;
                MetalBindings.objc_msgSend_void(attachment, Metal4Bindings.SelSetBlendingState,
                    blend.Enable ? Metal4Bindings.MTL4BlendStateEnabled : Metal4Bindings.MTL4BlendStateDisabled);

                if (blend.Enable)
                {
                    MetalBindings.objc_msgSend_void(attachment, MetalBindings.SelSetRgbBlendOperation, (nuint)ToBlendOp(blend.ColorOp));
                    MetalBindings.objc_msgSend_void(attachment, MetalBindings.SelSetAlphaBlendOperation, (nuint)ToBlendOp(blend.AlphaOp));
                    MetalBindings.objc_msgSend_void(attachment, MetalBindings.SelSetSourceRGBBlendFactor, (nuint)ToBlendFactor(blend.ColorSrcFactor));
                    MetalBindings.objc_msgSend_void(attachment, MetalBindings.SelSetDestinationRGBBlendFactor, (nuint)ToBlendFactor(blend.ColorDstFactor));
                    MetalBindings.objc_msgSend_void(attachment, MetalBindings.SelSetSourceAlphaBlendFactor, (nuint)ToBlendFactor(blend.AlphaSrcFactor));
                    MetalBindings.objc_msgSend_void(attachment, MetalBindings.SelSetDestinationAlphaBlendFactor, (nuint)ToBlendFactor(blend.AlphaDstFactor));
                }

                ulong writeMask = i < _colorWriteMasks.Length ? ToMtlColorWriteMask(_colorWriteMasks[i]) : 0xF;

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
                ulong depthCompare = _depthTest.TestEnable
                    ? ToCompareFunction(_depthTest.Func)
                    : MetalBindings.MTLCompareFunctionAlways;

                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetDepthCompareFunction, (nuint)depthCompare);
                MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetDepthWriteEnabled, _depthTest.WriteEnable && _depthTest.TestEnable);

                if (_stencilTest.TestEnable)
                {
                    nint frontDesc = MetalBindings.objc_msgSend(MetalBindings.objc_getClass("MTLStencilDescriptor"), MetalBindings.SelNew);
                    if (frontDesc != nint.Zero)
                    {
                        MetalBindings.objc_msgSend_void(frontDesc, MetalBindings.SelSetStencilCompareFunction, (nuint)MetalFormats.ToMtlCompareFunction(_stencilTest.FrontFunc));
                        MetalBindings.objc_msgSend_void(frontDesc, MetalBindings.SelSetStencilFailureOperation, (nuint)MetalFormats.ToMtlStencilOp(_stencilTest.FrontSFail));
                        MetalBindings.objc_msgSend_void(frontDesc, MetalBindings.SelSetDepthFailureOperation, (nuint)MetalFormats.ToMtlStencilOp(_stencilTest.FrontDpFail));
                        MetalBindings.objc_msgSend_void(frontDesc, MetalBindings.SelSetDepthStencilPassOperation, (nuint)MetalFormats.ToMtlStencilOp(_stencilTest.FrontDpPass));
                        MetalBindings.objc_msgSend_void(frontDesc, MetalBindings.SelSetReadMask, (nuint)(uint)_stencilTest.FrontFuncMask);
                        MetalBindings.objc_msgSend_void(frontDesc, MetalBindings.SelSetWriteMask, (nuint)(uint)_stencilTest.FrontMask);

                        MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetFrontFaceStencil, frontDesc);
                        MetalBindings.Release(frontDesc);
                    }

                    nint backDesc = MetalBindings.objc_msgSend(MetalBindings.objc_getClass("MTLStencilDescriptor"), MetalBindings.SelNew);
                    if (backDesc != nint.Zero)
                    {
                        MetalBindings.objc_msgSend_void(backDesc, MetalBindings.SelSetStencilCompareFunction, (nuint)MetalFormats.ToMtlCompareFunction(_stencilTest.BackFunc));
                        MetalBindings.objc_msgSend_void(backDesc, MetalBindings.SelSetStencilFailureOperation, (nuint)MetalFormats.ToMtlStencilOp(_stencilTest.BackSFail));
                        MetalBindings.objc_msgSend_void(backDesc, MetalBindings.SelSetDepthFailureOperation, (nuint)MetalFormats.ToMtlStencilOp(_stencilTest.BackDpFail));
                        MetalBindings.objc_msgSend_void(backDesc, MetalBindings.SelSetDepthStencilPassOperation, (nuint)MetalFormats.ToMtlStencilOp(_stencilTest.BackDpPass));
                        MetalBindings.objc_msgSend_void(backDesc, MetalBindings.SelSetReadMask, (nuint)(uint)_stencilTest.BackFuncMask);
                        MetalBindings.objc_msgSend_void(backDesc, MetalBindings.SelSetWriteMask, (nuint)(uint)_stencilTest.BackMask);

                        MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetBackFaceStencil, backDesc);
                        MetalBindings.Release(backDesc);
                    }
                }

                _depthStencilState = MetalBindings.objc_msgSend(_device, MetalBindings.SelNewDepthStencilStateWithDescriptor, descriptor);
                _depthDirty = false;
            }
            finally
            {
                MetalBindings.Release(descriptor);
            }
        }

        private MetalTexture _lastDrawnTarget;
        private int _lastDrawnTargetDrawCount;

        public MetalTexture LastDrawnTarget => _lastDrawnTarget;
        public int LastDrawnTargetDrawCount => _lastDrawnTargetDrawCount;

        // ---- Draw ----

        private void DrawInternal(int count, int instanceCount, int first, bool indexed, int firstIndex = 0)
        {
            if (_colorTargets.Length > 0 && _colorTargets[0] is MetalTexture activeTarget && activeTarget.TextureHandle != nint.Zero)
            {
                if (_lastDrawnTarget != activeTarget)
                {
                    _lastDrawnTarget = activeTarget;
                    _lastDrawnTargetDrawCount = 0;
                }
                _lastDrawnTargetDrawCount++;
            }

            if (_drawLogCount++ < 20)
            {
                string target = (_colorTargets.Length > 0 && _colorTargets[0] is MetalTexture mt) ? $"0x{mt.TextureHandle:X} {mt.Width}x{mt.Height} {mt.Format}" : "none";
                string vp = _viewports.Length > 0 ? $"{_viewports[0].Region.X},{_viewports[0].Region.Y},{_viewports[0].Region.Width},{_viewports[0].Region.Height}" : "none";
                string sc = _scissors.Length > 0 ? $"{_scissors[0].X},{_scissors[0].Y},{_scissors[0].Width},{_scissors[0].Height}" : "none";
                Logger.Warning?.Print(LogClass.Gpu, $"[DRAW] #{_drawLogCount}: count={count} inst={instanceCount} idx={indexed} target={target} vp=({vp}) sc=({sc}) blend={(_blends.Length > 0 && _blends[0].Enable)} depthTest={_depthTest.TestEnable} cull={_cullEnabled}");
            }

            if (_program == null)
            {
                return;
            }

            nint pipelineState = GetOrCreatePipelineState();

            if (pipelineState == nint.Zero)
            {
                if (_drawLogCount <= 5)
                {
                    Logger.Error?.Print(LogClass.Gpu, $"[DRAW_DIAG] #{_drawLogCount}: pipelineState is ZERO — pipeline creation failed!");
                }
                return;
            }

            if (!EnsureRenderPass())
            {
                if (_drawLogCount <= 5)
                {
                    Logger.Error?.Print(LogClass.Gpu, $"[DRAW_DIAG] #{_drawLogCount}: EnsureRenderPass FAILED");
                }
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

            // Scissor: must be strictly clamped to render target bounds per Metal spec.
            if (_scissors.Length > 0)
            {
                Rectangle<int> sc = _scissors[0];
                int targetWidth;
                int targetHeight;

                if (_colorTargets.Length > 0 && _colorTargets[0] != null)
                {
                    targetWidth  = _colorTargets[0].Width;
                    targetHeight = _colorTargets[0].Height;
                }
                else if (_depthTarget is MetalTexture depthForScissor && depthForScissor.TextureHandle != nint.Zero)
                {
                    // Depth-only pass (e.g. shadow cascade maps). Use the depth target's
                    // actual dimensions so a 2048x2048 shadow map is not clipped to 1920x1080.
                    targetWidth  = depthForScissor.Width;
                    targetHeight = depthForScissor.Height;
                }
                else
                {
                    targetWidth  = 1920;
                    targetHeight = 1080;
                }


                int scX = Math.Clamp(sc.X, 0, Math.Max(0, targetWidth - 1));
                int scY = Math.Clamp(sc.Y, 0, Math.Max(0, targetHeight - 1));
                int scW = Math.Clamp(sc.Width, 1, Math.Max(1, targetWidth - scX));
                int scH = Math.Clamp(sc.Height, 1, Math.Max(1, targetHeight - scY));

                MTLScissorRect scissor = new((nuint)scX, (nuint)scY, (nuint)scW, (nuint)scH);

                unsafe
                {
                    MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetScissorRect, &scissor);
                }
            }

            // Rasterizer state.
            ulong cullMode = MetalBindings.MTLCullModeNone;

            // Keep the guest state by default. This switch is intentionally opt-in so
            // a real boot can distinguish winding/culling rejection from shader or
            // attachment failures without changing normal rendering semantics.
            if (_cullEnabled && !s_forceNoCull)
            {
                cullMode = _cullFace switch
                {
                    Face.Front => MetalBindings.MTLCullModeFront,
                    Face.Back => MetalBindings.MTLCullModeBack,
                    _ => MetalBindings.MTLCullModeNone,
                };
            }

            bool isYFlipped = _viewports.Length > 0 && _viewports[0].Region.Height < 0;
            bool isClockwise = _frontFace == FrontFace.Clockwise;
            if (isYFlipped)
            {
                isClockwise = !isClockwise;
            }

            ulong winding = isClockwise
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

                if (_stencilTest.TestEnable)
                {
                    MetalBindings.objc_msgSend_void(
                        _renderEncoder,
                        MetalBindings.SelSetStencilFrontReferenceValueBackReferenceValue,
                        (nuint)(uint)_stencilTest.FrontFuncRef,
                        (nuint)(uint)_stencilTest.BackFuncRef);
                }

                if (_depthBiasEnabled)
                {
                    float biasUnits = _depthBiasUnits;
                    if (_depthTarget is MetalTexture metalDepth && metalDepth.Format == Format.D32Float)
                    {
                        // D32Float depth bias precision scaling for Maxwell -> Metal
                        biasUnits /= (1 << 24);
                    }
                    MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetDepthBiasSlopeScaleClamp, _depthBiasFactor, biasUnits, _depthBiasClamp);
                }

                MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetDepthClipMode, (nuint)(_depthClamp ? 1 : 0)); // MTLDepthClipModeClamp = 1, MTLDepthClipModeClip = 0
            }

            // M4: all resources bind through per-stage device-side argument tables.
            if (!EnsureArgumentTables())
            {
                return;
            }

            // Vertex buffers.
            int vbBoundCount = 0;
            for (int i = 0; i < _vertexBuffers.Length; i++)
            {
                uint slot = VertexBufferSlotOffset + (uint)i;
                if (slot >= 31)
                {
                    break;
                }

                VertexBufferDescriptor vb = _vertexBuffers[i];
                nint buffer = _renderer.GetBuffer(vb.Buffer.Handle);

                if (buffer != nint.Zero)
                {
                    BindTableBuffer(_argumentTableVertex, buffer, (uint)vb.Buffer.Offset, slot);
                    vbBoundCount++;
                }
            }

            // Uniform buffers (bind to every stage that uses the (set, binding)).
            int ubBoundCount = 0;
            foreach ((_, BufferAssignment assignment) in _uniformBuffers)
            {
                BindTableBufferForSet(_argumentTableVertex, ShaderStage.Vertex, assignment, UniformBufferSet, Interop.SpirvCross.MslResourceKind.UniformBuffer);
                BindTableBufferForSet(_argumentTableFragment, ShaderStage.Fragment, assignment, UniformBufferSet, Interop.SpirvCross.MslResourceKind.UniformBuffer);
                ubBoundCount++;
            }

            // Storage buffers.
            foreach ((_, BufferAssignment assignment) in _storageBuffers)
            {
                BindTableBufferForSet(_argumentTableVertex, ShaderStage.Vertex, assignment, StorageBufferSet, Interop.SpirvCross.MslResourceKind.StorageBuffer);
                BindTableBufferForSet(_argumentTableFragment, ShaderStage.Fragment, assignment, StorageBufferSet, Interop.SpirvCross.MslResourceKind.StorageBuffer);
            }

            // Textures + samplers.
            BindTexturesAndSamplers(_argumentTableVertex, ShaderStage.Vertex, _texturesVertex);
            BindTexturesAndSamplers(_argumentTableFragment, ShaderStage.Fragment, _texturesFragment);

            // Images.
            BindImages(_argumentTableVertex, ShaderStage.Vertex, _imagesVertex);
            BindImages(_argumentTableFragment, ShaderStage.Fragment, _imagesFragment);

            // Deep diagnostics for first 5 draws
            if (_drawLogCount <= 5)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"[DRAW_DIAG] #{_drawLogCount}: PSO=0x{pipelineState:X} enc=0x{_renderEncoder:X} cb=0x{_commandBuffer:X} vbBound={vbBoundCount} ubBound={ubBoundCount} attribs={_vertexAttribs.Length} vertFn=0x{_program.VertexFunction:X} fragFn=0x{_program.FragmentFunction:X} winding={(isClockwise ? "CW" : "CCW")} cull={cullMode} forceNoCull={s_forceNoCull} yFlip={isYFlipped}");
                
                if (_vertexBuffers.Length > 0)
                {
                    var vb0 = _vertexBuffers[0];
                    nint buf0 = _renderer.GetBuffer(vb0.Buffer.Handle);
                    ulong len0 = buf0 != nint.Zero ? MetalBindings.objc_msgSend_ulong_ret(buf0, MetalBindings.SelLength) : 0;
                    ulong addr0 = buf0 != nint.Zero ? MetalBindings.objc_msgSend_ulong_ret(buf0, Metal4Bindings.SelGpuAddress) : 0;
                    Logger.Warning?.Print(LogClass.Gpu, $"[DRAW_DIAG] #{_drawLogCount}: VB[0] handle=0x{buf0:X} gpuAddr=0x{addr0:X} len={len0} offset={vb0.Buffer.Offset} stride={vb0.Stride}");
                }

                if (indexed)
                {
                    nint idxBuf = _renderer.GetBuffer(_indexBuffer.Handle);
                    ulong idxLen = idxBuf != nint.Zero ? MetalBindings.objc_msgSend_ulong_ret(idxBuf, MetalBindings.SelLength) : 0;
                    ulong idxAddr = idxBuf != nint.Zero ? MetalBindings.objc_msgSend_ulong_ret(idxBuf, Metal4Bindings.SelGpuAddress) : 0;
                    Logger.Warning?.Print(LogClass.Gpu, $"[DRAW_DIAG] #{_drawLogCount}: IB handle=0x{idxBuf:X} gpuAddr=0x{idxAddr:X} len={idxLen} offset={_indexBuffer.Offset} type={_indexType}");
                }
            }

            // Bind the tables to their stages. M4 takes a snapshot of the resources in
            // the argument table when the draw below is encoded.
            Metal4Bindings.m4_msgSend_void(_renderEncoder, Metal4Bindings.SelSetArgumentTableAtStages, _argumentTableVertex, Metal4Bindings.MTLRenderStageVertex);
            Metal4Bindings.m4_msgSend_void(_renderEncoder, Metal4Bindings.SelSetArgumentTableAtStages, _argumentTableFragment, Metal4Bindings.MTLRenderStageFragment);

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

                uint indexSize = _indexType == IndexType.UInt ? 4u : 2u;
                ulong indexByteOffset = (ulong)_indexBuffer.Offset + (ulong)firstIndex * indexSize;
                ulong indexAddress = MetalBindings.objc_msgSend_ulong_ret(indexBuffer, Metal4Bindings.SelGpuAddress) + indexByteOffset;
                ulong bufferLength = MetalBindings.objc_msgSend_ulong_ret(indexBuffer, MetalBindings.SelLength);

                Metal4Bindings.m4_msgSend_void(
                    _renderEncoder,
                    Metal4Bindings.SelDrawIndexedPrimitivesIndexCountIndexTypeIndexBufferLengthInstanceCount,
                    (nuint)primitiveType,
                    (nuint)count,
                    (nuint)indexType,
                    indexAddress,
                    (nuint)(bufferLength - indexByteOffset),
                    (nuint)instanceCount);
            }
else
            {
                ulong primitiveType = ToPrimitiveType(_topology);

                Metal4Bindings.m4_msgSend_void(
                    _renderEncoder,
                    MetalBindings.SelDrawPrimitivesVertexStartVertexCountInstanceCount,
                    (nuint)primitiveType,
                    (nuint)first,
                    (nuint)count,
                    (nuint)instanceCount);
            }

            EncodeConstColorOverlay();

            _frameDrawCount++;
            _totalDrawCount++;

            if (MetalTelemetryDump.IsEnabled && _framePassRecords.Count > 0)
            {
                _framePassRecords[^1].DrawCount++;
            }

            if (MetalTelemetryDump.IsVerboseLoggingEnabled || MetalTelemetryDump.IsEnabled)
            {
                int currentPass = _framePassRecords.Count > 0 ? _framePassRecords[^1].PassIndex : 0;
                int currentDraw = _framePassRecords.Count > 0 ? _framePassRecords[^1].DrawCount : _frameDrawCount;
                string state = MetalTelemetryDump.FormatDrawState(
                    currentPass,
                    currentDraw,
                    _viewports.Length > 0 ? _viewports[0] : default,
                    _scissors.Length > 0 ? _scissors[0] : default,
                    _cullEnabled ? _cullFace.ToString() : "None",
                    _frontFace.ToString(),
                    _depthTest.TestEnable,
                    _depthTest.Func.ToString());

                if (MetalTelemetryDump.IsEnabled && _framePassRecords.Count > 0)
                {
                    _framePassRecords[^1].DrawStates.Add(state);
                }

                if (_totalDrawCount <= 20 || MetalTelemetryDump.IsVerboseLoggingEnabled)
                {
                    Logger.Warning?.Print(LogClass.Gpu, state);
                }
            }

            _probeIndexed = indexed;
            _probeCount = count;
            _probeFirstIndex = firstIndex;
            _probeInstanceCount = instanceCount;

            RunGameDrawCaptureProbe();
        }

        private void BindTableBufferForSet(nint table, ShaderStage stage, BufferAssignment assignment, uint setIndex, Interop.SpirvCross.MslResourceKind kind)
        {
            uint mslIndex = _program.GetMslBinding(stage, setIndex, (uint)assignment.Binding, kind, out _);

            if (_drawLogCount <= 5)
            {
                Logger.Warning?.Print(LogClass.Gpu, $"[BIND] {stage} set={setIndex} bind={assignment.Binding} kind={kind} -> msl={mslIndex} ({(mslIndex == uint.MaxValue ? "MISS" : "OK")}) range=0x{assignment.Range.Handle:x} off={assignment.Range.Offset} size={assignment.Range.Size}");
            }

            if (mslIndex == uint.MaxValue)
            {
                return;
            }

            nint buffer = _renderer.GetBuffer(assignment.Range.Handle);

            if (buffer == nint.Zero)
            {
                return;
            }

            BindTableBuffer(table, buffer, (uint)assignment.Range.Offset, mslIndex);
        }

        private static void BindTableBuffer(nint table, nint buffer, uint offset, uint index)
        {
            ulong address = MetalBindings.objc_msgSend_ulong_ret(buffer, Metal4Bindings.SelGpuAddress) + offset;
            Metal4Bindings.m4_msgSend_void(table, Metal4Bindings.SelSetAddressAtIndex, address, (nuint)index);
        }

        private static void BindTableTexture(nint table, nint texture, uint index)
        {
            ulong resourceId = MetalBindings.objc_msgSend_ulong_ret(texture, Metal4Bindings.SelGpuResourceID);
            Metal4Bindings.m4_msgSend_void(table, Metal4Bindings.SelSetTextureAtIndex, resourceId, (nuint)index);
        }

        private static void BindTableSampler(nint table, nint sampler, uint index)
        {
            // MTL4ArgumentTable permits sampler slots 0..15 only.
            // Overflow samplers are handled via constexpr samplers in MSL and must
            // not overwrite valid argument table slots.
            if (index > 15)
            {
                return;
            }
            ulong resourceId = MetalBindings.objc_msgSend_ulong_ret(sampler, Metal4Bindings.SelGpuResourceID);
            Metal4Bindings.m4_msgSend_void(table, Metal4Bindings.SelSetSamplerStateAtIndex, resourceId, (nuint)index);
        }

        private void BindTexturesAndSamplers(nint table, ShaderStage stage, Dictionary<int, (ITexture Texture, ISampler Sampler)> bindings)
        {
            foreach ((int binding, (ITexture texture, ISampler sampler)) in bindings)
            {
                uint mslIndex = _program.GetMslBinding(stage, TextureSet, (uint)binding, Interop.SpirvCross.MslResourceKind.Texture, out uint samplerIndex);

                if (_drawLogCount <= 5)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"[BIND] {stage} set={TextureSet} bind={binding} kind=Texture -> msl={mslIndex} samp={samplerIndex} ({(mslIndex == uint.MaxValue ? "MISS" : "OK")})");
                }

                if (mslIndex == uint.MaxValue)
                {
                    continue;
                }

                if (texture is MetalTexture metalTexture && metalTexture.TextureHandle != nint.Zero)
                {
                    BindTableTexture(table, metalTexture.TextureHandle, mslIndex);
                }

                if (samplerIndex != Interop.SpirvCross.ConstexprSampler && sampler is MetalSampler metalSampler && metalSampler.SamplerState != nint.Zero)
                {
                    uint actualSamplerIndex = samplerIndex != uint.MaxValue ? samplerIndex : mslIndex;

                    if (actualSamplerIndex <= 15)
                    {
                        BindTableSampler(table, metalSampler.SamplerState, actualSamplerIndex);
                    }
                }
            }
        }

        private void BindImages(nint table, ShaderStage stage, Dictionary<int, ITexture> bindings)
        {
            foreach ((int binding, ITexture texture) in bindings)
            {
                uint mslIndex = _program.GetMslBinding(stage, ImageSet, (uint)binding, Interop.SpirvCross.MslResourceKind.StorageImage, out _);

                if (_drawLogCount <= 5)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"[BIND] {stage} set={ImageSet} bind={binding} kind=StorageImage -> msl={mslIndex} ({(mslIndex == uint.MaxValue ? "MISS" : "OK")})");
                }

                if (mslIndex == uint.MaxValue || texture is not MetalTexture metalTexture || metalTexture.TextureHandle == nint.Zero)
                {
                    continue;
                }

                BindTableTexture(table, metalTexture.TextureHandle, mslIndex);
            }
        }

        // ---- Probe 3d: const-color overlay ----

        private const string OverlayShaderSource = @"
#include <metal_stdlib>
using namespace metal;

struct OverlayOut {
    float4 pos [[position]];
};

vertex OverlayOut overlay_v(uint vid [[vertex_id]]) {
    OverlayOut o;
    float2 p = float2((float((vid << 1) & 2)), float(vid & 2));
    o.pos = float4(p * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}

fragment float4 overlay_f(OverlayOut in [[stage_in]],
                          const device float4 & color [[buffer(0)]]) {
    return color;
}
";

        private static (float R, float G, float B, float A) ParseOverlayColor()
        {
            return Environment.GetEnvironmentVariable("RYU_METAL_CONST_OVERLAY") switch
            {
                "magenta" => (1f, 0f, 1f, 1f),
                "red" => (1f, 0f, 0f, 1f),
                "green" => (0f, 1f, 0f, 1f),
                "blue" => (0f, 0f, 1f, 1f),
                "yellow" => (1f, 1f, 0f, 1f),
                "cyan" => (0f, 1f, 1f, 1f),
                "white" => (1f, 1f, 1f, 1f),
                _ => (0f, 0f, 0f, 0f),
            };
        }

        private unsafe bool EnsureOverlayResources()
        {
            if (_overlayLibrary != nint.Zero)
            {
                return true;
            }

            nint serDesc = nint.Zero;
            nint serializer = nint.Zero;
            nint compDesc = nint.Zero;
            nint libDesc = nint.Zero;
            nint opts = nint.Zero;
            nint sourceString = nint.Zero;
            nint nsError = nint.Zero;

            try
            {
                // MTL4 pipeline path (mirrors the passing Metal 4 parallel-encode diagnostic):
                // MTL4Compiler + MTL4LibraryDescriptor (MSL) -> MTL4Library -> functions.
                // A classic MTL library/pipeline driven on an MTL4RenderPassEncoder rasterizes
                // nothing, which is exactly the failure the overlay probe is validating.
                serDesc = Metal4Bindings.Metal4New("MTL4PipelineDataSetSerializerDescriptor");
                MetalBindings.objc_msgSend_void(serDesc, Metal4Bindings.SelSetConfiguration, (nuint)(Metal4Bindings.M4CaptureDescriptors | Metal4Bindings.M4CaptureBinaries));
                serializer = MetalBindings.objc_msgSend(_device, Metal4Bindings.SelNewPipelineDataSetSerializerWithDescriptor, serDesc);

                compDesc = Metal4Bindings.Metal4New("MTL4CompilerDescriptor");
                MetalBindings.objc_msgSend_void(compDesc, Metal4Bindings.SelSetPipelineDataSetSerializer, serializer);
                MetalBindings.objc_msgSend_void(compDesc, MetalBindings.SelSetLabel, MetalBindings.CreateNSString("m4-overlay-compiler"));
                _overlayCompiler = MetalBindings.objc_msgSend(_device, Metal4Bindings.SelNewCompilerWithDescriptorError, compDesc, nint.Zero);

                if (_overlayCompiler == nint.Zero)
                {
                    Logger.Error?.Print(LogClass.Gpu, "MetalPipeline overlay: MTL4Compiler creation failed");
                    return false;
                }

                libDesc = Metal4Bindings.Metal4New("MTL4LibraryDescriptor");
                sourceString = MetalBindings.CreateNSString(OverlayShaderSource);
                MetalBindings.objc_msgSend_void(libDesc, Metal4Bindings.SelSetSource, sourceString);
                MetalBindings.objc_msgSend_void(libDesc, Metal4Bindings.SelSetName, MetalBindings.CreateNSString("m4OverlayLib"));
                opts = MetalBindings.objc_msgSend(MetalBindings.objc_getClass("MTLCompileOptions"), MetalBindings.SelNew);
                MetalBindings.objc_msgSend_void(opts, Metal4Bindings.SelSetLanguageVersion, (nuint)Metal4Bindings.MTLLanguageVersion4_0);
                MetalBindings.objc_msgSend_void(libDesc, Metal4Bindings.SelSetOptions, opts);

                _overlayLibrary = MetalBindings.objc_msgSend(_overlayCompiler, Metal4Bindings.SelNewLibraryWithDescriptorError, libDesc, (nint)(&nsError));

                if (_overlayLibrary == nint.Zero)
                {
                    Logger.Error?.Print(LogClass.Gpu, $"MetalPipeline overlay: MTL4 MSL compile failed: {MetalBindings.GetErrorDescription(nsError)}");
                    return false;
                }

                _overlayTaskOptions = MetalBindings.objc_msgSend(MetalBindings.objc_getClass("MTL4CompilerTaskOptions"), MetalBindings.SelNew);

                _overlayVertexFunction = MetalBindings.objc_msgSend(_overlayLibrary, MetalBindings.SelNewFunctionWithName, MetalBindings.CreateNSString("overlay_v"));
                _overlayFragmentFunction = MetalBindings.objc_msgSend(_overlayLibrary, MetalBindings.SelNewFunctionWithName, MetalBindings.CreateNSString("overlay_f"));

                if (_overlayVertexFunction == nint.Zero || _overlayFragmentFunction == nint.Zero)
                {
                    Logger.Error?.Print(LogClass.Gpu, "MetalPipeline overlay: newFunctionWithName returned nil for overlay_v/overlay_f");
                    return false;
                }

                _overlayColorBuffer = MetalBindings.objc_msgSend(_device, MetalBindings.SelNewBufferWithLengthOptions, (nuint)16, (nuint)MetalBindings.MTLResourceStorageModeShared);

                if (_overlayColorBuffer == nint.Zero)
                {
                    return false;
                }

                float[] color = { _overlayColor.R, _overlayColor.G, _overlayColor.B, _overlayColor.A };

                nint contents = MetalBindings.objc_msgSend(_overlayColorBuffer, MetalBindings.SelContents);
                fixed (float* src = color)
                {
                    for (int i = 0; i < 4; i++)
                    {
                        *((float*)contents + i) = src[i];
                    }
                }
            }
            finally
            {
                MetalBindings.Release(serDesc);
                MetalBindings.Release(serializer);
                MetalBindings.Release(compDesc);
                MetalBindings.Release(libDesc);
                MetalBindings.Release(opts);
                MetalBindings.Release(sourceString);
            }

            return true;
        }

        private bool EnsureOverlayArgumentTables()
        {
            if (_overlayArgumentTableVertex != nint.Zero && _overlayArgumentTableFragment != nint.Zero)
            {
                return true;
            }

            nint descriptor = Metal4Bindings.Metal4New("MTL4ArgumentTableDescriptor");

            if (descriptor == nint.Zero)
            {
                return false;
            }

            try
            {
                Metal4Bindings.m4_msgSend_void(descriptor, Metal4Bindings.SelSetMaxBufferBindCount, (nuint)31);
                Metal4Bindings.m4_msgSend_void(descriptor, Metal4Bindings.SelSetMaxTextureBindCount, (nuint)64);
                Metal4Bindings.m4_msgSend_void(descriptor, Metal4Bindings.SelSetMaxSamplerStateBindCount, (nuint)16);

                _overlayArgumentTableVertex = MetalBindings.objc_msgSend(_device, Metal4Bindings.SelNewArgumentTableWithDescriptorError, descriptor, nint.Zero);
                _overlayArgumentTableFragment = MetalBindings.objc_msgSend(_device, Metal4Bindings.SelNewArgumentTableWithDescriptorError, descriptor, nint.Zero);
            }
            finally
            {
                MetalBindings.Release(descriptor);
            }

            return _overlayArgumentTableVertex != nint.Zero && _overlayArgumentTableFragment != nint.Zero;
        }

        private bool EnsureOverlayPipeline(MetalTexture target, out nint overlayPipeline)
        {
            overlayPipeline = nint.Zero;

            ulong colorFormat = MetalFormats.ToMtlPixelFormat(target.Format);
            ulong depthFormat = _depthTarget is MetalTexture depthTex ? MetalFormats.ToMtlPixelFormat(depthTex.Format) : 0;

            if (colorFormat == 0)
            {
                return false;
            }

            string key = $"{colorFormat}/{depthFormat}";

            if (_overlayPipelineCache.TryGetValue(key, out overlayPipeline))
            {
                return overlayPipeline != nint.Zero;
            }

            nint descriptor = nint.Zero;
            nint vdesc = nint.Zero;
            nint fdesc = nint.Zero;
            nint nsError = nint.Zero;

            try
            {
                vdesc = Metal4Bindings.Metal4New("MTL4LibraryFunctionDescriptor");
                MetalBindings.objc_msgSend_void(vdesc, Metal4Bindings.SelSetName, MetalBindings.CreateNSString("overlay_v"));
                MetalBindings.objc_msgSend_void(vdesc, Metal4Bindings.SelSetLibrary, _overlayLibrary);

                fdesc = Metal4Bindings.Metal4New("MTL4LibraryFunctionDescriptor");
                MetalBindings.objc_msgSend_void(fdesc, Metal4Bindings.SelSetName, MetalBindings.CreateNSString("overlay_f"));
                MetalBindings.objc_msgSend_void(fdesc, Metal4Bindings.SelSetLibrary, _overlayLibrary);

                descriptor = Metal4Bindings.Metal4New("MTL4RenderPipelineDescriptor");
                MetalBindings.objc_msgSend_void(descriptor, Metal4Bindings.SelSetVertexFunctionDescriptor, vdesc);
                MetalBindings.objc_msgSend_void(descriptor, Metal4Bindings.SelSetFragmentFunctionDescriptor, fdesc);

                // Declare EVERY color attachment the pass has, not just target 0. A render
                // pipeline whose colorAttachments are a strict subset of the pass's
                // attachments is invalid and its draws are dropped by the driver.
                nint colorAttachments = MetalBindings.objc_msgSend(descriptor, Metal4Bindings.SelColorAttachments);
                int attachmentCount = Math.Max(1, _colorTargetCount);

                for (int ai = 0; ai < attachmentCount && ai < 8; ai++)
                {
                    nint attachment = MetalBindings.objc_msgSend(colorAttachments, Metal4Bindings.SelObjectAtIndexedSubscript, (nuint)ai);

                    if (attachment == nint.Zero)
                    {
                        continue;
                    }

                    ulong format = colorFormat;

                    if (ai < _colorTargets.Length && _colorTargets[ai] is MetalTexture mTarget)
                    {
                        ulong f = MetalFormats.ToMtlPixelFormat(mTarget.Format);

                        if (f != 0)
                        {
                            format = f;
                        }
                    }

                    MetalBindings.objc_msgSend_void(attachment, Metal4Bindings.SelSetPixelFormat, (nuint)format);
                }

                // MTL4 supplies the depth format through the render pass depth
                // attachment, not through MTL4RenderPipelineDescriptor.

                unsafe
                {
                    overlayPipeline = MetalBindings.objc_msgSend(
                        _overlayCompiler,
                        Metal4Bindings.SelNewRenderPipelineStateWithDescriptorCompilerTaskOptionsError,
                        descriptor,
                        _overlayTaskOptions,
                        (nint)(&nsError));
                }

                if (overlayPipeline != nint.Zero)
                {
                    _overlayPipelineCache[key] = overlayPipeline;
                }
                else
                {
                    Logger.Error?.Print(LogClass.Gpu, $"MetalPipeline overlay: MTL4 pipeline-state creation failed: {MetalBindings.GetErrorDescription(nsError)}");
                }
            }
            finally
            {
                MetalBindings.Release(descriptor);
                MetalBindings.Release(vdesc);
                MetalBindings.Release(fdesc);
            }

            return overlayPipeline != nint.Zero;
        }

        private void EncodeConstColorOverlay()
        {
            if (_overlayColor.A == 0 || _drawLogCount != 1 ||
                _colorTargets.Length == 0 || _colorTargets[0] is not MetalTexture target || target.TextureHandle == nint.Zero)
            {
                return;
            }

            if (!EnsureOverlayResources() || !EnsureOverlayArgumentTables() || !EnsureOverlayPipeline(target, out nint overlayPipeline))
            {
                return;
            }

            MTLViewport viewport = new(0, 0, target.Width, target.Height, 0, 1);
            MTLScissorRect fullScissor = new(0, 0, (nuint)target.Width, (nuint)target.Height);

            MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetRenderPipelineState, overlayPipeline);
            MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetCullMode, (nuint)MetalBindings.MTLCullModeNone);
            MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetFrontFacingWinding, (nuint)MetalBindings.MTLWindingCounterClockwise);

            unsafe
            {
                MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetViewport, &viewport);
                MetalBindings.objc_msgSend_void(_renderEncoder, MetalBindings.SelSetScissorRect, &fullScissor);
            }

ulong colorBufAddr = MetalBindings.objc_msgSend_ulong_ret(_overlayColorBuffer, Metal4Bindings.SelGpuAddress);
            Metal4Bindings.m4_msgSend_void(_overlayArgumentTableFragment, Metal4Bindings.SelSetAddressAtIndex, colorBufAddr, (nuint)0);

            // Mirror the passing parallel-encode diagnostic exactly: fragment table only,
            // classic drawPrimitives:vertexStart:vertexCount: (no instance count).
            Metal4Bindings.m4_msgSend_void(_renderEncoder, Metal4Bindings.SelSetArgumentTableAtStages, _overlayArgumentTableFragment, Metal4Bindings.MTLRenderStageFragment);

            Metal4Bindings.m4_msgSend_void(
                _renderEncoder,
                Metal4Bindings.SelDrawPrimitivesVertexStartVertexCount,
                (nuint)MetalBindings.MTLPrimitiveTypeTriangle,
                (nuint)0,
                (nuint)3);

            Logger.Warning?.Print(LogClass.Gpu, $"[OVERLAY] drew {_overlayColor} over 0x{target.TextureHandle:X} {target.Width}x{target.Height} (draw #{_drawLogCount})");

            RunMiniMetal4DrawTest(overlayPipeline);
        }

        private void RunMiniMetal4DrawTest(nint overlayPipeline)
        {
            if (_miniM4Tested || _renderer == null || overlayPipeline == nint.Zero)
            {
                return;
            }

            _miniM4Tested = true;

            try
            {
                // DECISIVE: does a CLASSIC (newRenderPipelineStateWithDescriptor:) pipeline rasterize
                // on an MTL4RenderPassEncoder? Build the SAME overlay magenta shader via the classic
                // one-time-compile path and draw it into a probe. If classic=black but MTL4=magenta,
                // classic PSOs are silently dropped on the M4 encoder == the game's GetOrCreatePipelineState path.
                RunClassicVsMtl4Probe();

                // Draw the magenta fullscreen overlay directly into the game's active
                // present-texture (target of the current pass), via a standalone commit.
                // This tells us whether M4 draws into the GAME texture rasterize at all,
                // independent of the game's pass/PSO.
                if (_colorTargets.Length > 0 && _colorTargets[0] is MetalTexture presentTarget && presentTarget.TextureHandle != nint.Zero)
                {
                    nuint w = (nuint)presentTarget.Width;
                    nuint h = (nuint)presentTarget.Height;

                    int alloc = _renderer.M4AllocatorPool.Acquire();

                    if (alloc >= 0)
                    {
                        nint allocator = _renderer.M4AllocatorPool.GetAllocatorHandle(alloc);
                        nint cb = _renderer.M4Queue.BeginCommandBuffer(_renderer.DeviceHandle, allocator);
                        nint passDesc = Metal4Bindings.Metal4New("MTL4RenderPassDescriptor");

                        MTLColor black = new(0, 0, 0, 1);
                        nint atts = MetalBindings.objc_msgSend(passDesc, MetalBindings.SelColorAttachments);
                        nint att0 = MetalBindings.objc_msgSend(atts, MetalBindings.SelObjectAtIndexedSubscript, (nuint)0);
                        MetalBindings.objc_msgSend_void(att0, MetalBindings.SelSetTexture, presentTarget.TextureHandle);
                        MetalBindings.objc_msgSend_void(att0, MetalBindings.SelSetLoadAction, (nuint)MetalBindings.MTLLoadActionClear);
                        unsafe
                        {
                            MetalBindings.objc_msgSend_void(att0, MetalBindings.SelSetClearColor, &black);
                        }
                        MetalBindings.objc_msgSend_void(att0, MetalBindings.SelSetStoreAction, (nuint)MetalBindings.MTLStoreActionStore);

                        nint enc = MetalBindings.objc_msgSend(cb, Metal4Bindings.SelRenderCommandEncoderWithDescriptor, passDesc);
                        MetalBindings.objc_msgSend_void(enc, MetalBindings.SelSetRenderPipelineState, overlayPipeline);
                        Metal4Bindings.m4_msgSend_void(enc, Metal4Bindings.SelSetArgumentTableAtStages, _overlayArgumentTableFragment, Metal4Bindings.MTLRenderStageFragment);
                        Metal4Bindings.m4_msgSend_void(enc, Metal4Bindings.SelDrawPrimitivesVertexStartVertexCount,
                            (nuint)MetalBindings.MTLPrimitiveTypeTriangle, (nuint)0, (nuint)3);
                        MetalBindings.objc_msgSend_void(enc, MetalBindings.SelEndEncoding);
                        _renderer.M4Queue.EndCommandBuffer(cb);

                        ulong signal = _renderer.M4Queue.CommitBatch(new[] { cb });

                        for (int i = 0; i < 20 && _renderer.M4Queue.SignaledValue < signal; i++)
                        {
                            Metal4Bindings.m4_wait_event_bool(_renderer.M4Queue.CompletionEvent, Metal4Bindings.SelWaitUntilSignaledValueTimeoutMS, signal, 250);
                        }

                        byte[] px = new byte[4];
                        MTLRegion region = new(w / 2, h / 2, 0, 1, 1, 1);

                        unsafe
                        {
                            fixed (byte* p = px)
                            {
                                MetalBindings.objc_msgSend_void(presentTarget.TextureHandle, MetalBindings.SelGetBytesBytesPerRowFromRegionMipmapLevel, p, (nuint)4, &region, 0);
                            }
                        }

                        Logger.Warning?.Print(LogClass.Gpu, $"[DIRECT_TEX] drew magenta directly into game texture 0x{presentTarget.TextureHandle:X} {w}x{h}: pixel=({px[0]},{px[1]},{px[2]},{px[3]})");

                        _renderer.M4AllocatorPool.Release(alloc);
                        MetalBindings.Release(passDesc);
                        MetalBindings.Release(cb);
                    }
                }

                TextureCreateInfo info = new(8, 8, 1, 1, 1, 1, 1, 4, Format.R8G8B8A8Unorm, DepthStencilMode.Depth, Target.Texture2D,
                    SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);

                using MetalTexture mini = new(_renderer.DeviceHandle, _renderer.CommandQueueHandle, info);

                int mAlloc = _renderer.M4AllocatorPool.Acquire();

                if (mAlloc < 0)
                {
                    Logger.Error?.Print(LogClass.Gpu, "[MINI_M4] allocator acquire failed");
                    return;
                }

                nint mAllocator = _renderer.M4AllocatorPool.GetAllocatorHandle(mAlloc);
                nint mCb = _renderer.M4Queue.BeginCommandBuffer(_renderer.DeviceHandle, mAllocator);
                nint mPassDesc = Metal4Bindings.Metal4New("MTL4RenderPassDescriptor");

                MTLColor mBlack = new(0, 0, 0, 1);
                nint mAtts = MetalBindings.objc_msgSend(mPassDesc, MetalBindings.SelColorAttachments);
                nint mAtt0 = MetalBindings.objc_msgSend(mAtts, MetalBindings.SelObjectAtIndexedSubscript, (nuint)0);
                MetalBindings.objc_msgSend_void(mAtt0, MetalBindings.SelSetTexture, mini.TextureHandle);
                MetalBindings.objc_msgSend_void(mAtt0, MetalBindings.SelSetLoadAction, (nuint)MetalBindings.MTLLoadActionClear);
                unsafe
                {
                    MetalBindings.objc_msgSend_void(mAtt0, MetalBindings.SelSetClearColor, &mBlack);
                }
                MetalBindings.objc_msgSend_void(mAtt0, MetalBindings.SelSetStoreAction, (nuint)MetalBindings.MTLStoreActionStore);

                nint mEnc = MetalBindings.objc_msgSend(mCb, Metal4Bindings.SelRenderCommandEncoderWithDescriptor, mPassDesc);
                MetalBindings.objc_msgSend_void(mEnc, MetalBindings.SelSetRenderPipelineState, overlayPipeline);
                Metal4Bindings.m4_msgSend_void(mEnc, Metal4Bindings.SelSetArgumentTableAtStages, _overlayArgumentTableFragment, Metal4Bindings.MTLRenderStageFragment);
                Metal4Bindings.m4_msgSend_void(mEnc, Metal4Bindings.SelDrawPrimitivesVertexStartVertexCount,
                    (nuint)MetalBindings.MTLPrimitiveTypeTriangle, (nuint)0, (nuint)3);
                MetalBindings.objc_msgSend_void(mEnc, MetalBindings.SelEndEncoding);
                _renderer.M4Queue.EndCommandBuffer(mCb);

                ulong pollBefore = _renderer.M4Queue.SignaledValue;
                ulong mSignal = _renderer.M4Queue.CommitBatch(new[] { mCb });
                ulong pollAfter = _renderer.M4Queue.SignaledValue;
                Thread.Sleep(30);
                ulong pollAfterSleep = _renderer.M4Queue.SignaledValue;

                for (int i = 0; i < 20 && _renderer.M4Queue.SignaledValue < mSignal; i++)
                {
                    Metal4Bindings.m4_wait_event_bool(_renderer.M4Queue.CompletionEvent, Metal4Bindings.SelWaitUntilSignaledValueTimeoutMS, mSignal, 250);
                }

                byte[] mPx = new byte[4];
                MTLRegion mRegion = new(4, 4, 0, 1, 1, 1);

                unsafe
                {
                    fixed (byte* p = mPx)
                    {
                        MetalBindings.objc_msgSend_void(mini.TextureHandle, MetalBindings.SelGetBytesBytesPerRowFromRegionMipmapLevel, p, (nuint)(4), &mRegion, 0);
                    }
                }

                bool beforeIsFree = _renderer.M4Queue.SignaledValue >= mSignal;

                Logger.Warning?.Print(LogClass.Gpu, $"[MINI_M4] signal={mSignal} pollBefore={pollBefore} pollAfter={pollAfter} pollAfterSleep={pollAfterSleep} pixel=({mPx[0]},{mPx[1]},{mPx[2]},{mPx[3]}) waitersDone={beforeIsFree}");

                _renderer.M4AllocatorPool.Release(mAlloc);
                MetalBindings.Release(mPassDesc);
                MetalBindings.Release(mCb);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"[MINI_M4] exception: {ex.Message}");
            }
        }

        // ---- Game draw render-pass probe ----

        private unsafe void RunClassicVsMtl4Probe()
        {
            try
            {
                nint src = MetalBindings.CreateNSString(OverlayShaderSource);
                nint nsErr = nint.Zero;
                nint lib = MetalBindings.objc_msgSend(_device, MetalBindings.SelNewLibraryWithSourceOptionsError, src, nint.Zero, (nint)(&nsErr));

                if (lib == nint.Zero)
                {
                    Logger.Warning?.Print(LogClass.Gpu, "[CLASSPROBE] classic library compile failed");
                    MetalBindings.Release(src);
                    return;
                }

                nint vf = MetalBindings.objc_msgSend(lib, MetalBindings.SelNewFunctionWithName, MetalBindings.CreateNSString("overlay_v"));
                nint ff = MetalBindings.objc_msgSend(lib, MetalBindings.SelNewFunctionWithName, MetalBindings.CreateNSString("overlay_f"));

                nint desc = MetalBindings.objc_msgSend(MetalBindings.objc_getClass("MTLRenderPipelineDescriptor"), MetalBindings.SelNew);
                MetalBindings.objc_msgSend_void(desc, MetalBindings.SelSetVertexFunction, vf);
                MetalBindings.objc_msgSend_void(desc, MetalBindings.SelSetFragmentFunction, ff);
                nint atts = MetalBindings.objc_msgSend(desc, MetalBindings.SelColorAttachments);
                nint att0 = MetalBindings.objc_msgSend(atts, MetalBindings.SelObjectAtIndexedSubscript, (nuint)0);
                MetalBindings.objc_msgSend_void(att0, MetalBindings.SelSetPixelFormat, (nuint)MetalBindings.MTLPixelFormatRGBA8Unorm);

                nint classicPipeline;
                unsafe
                {
                    classicPipeline = MetalBindings.objc_msgSend(_device, MetalBindings.SelNewRenderPipelineStateWithDescriptorError, desc, (nint)(&nsErr));
                }

                if (classicPipeline == nint.Zero)
                {
                    Logger.Warning?.Print(LogClass.Gpu, $"[CLASSPROBE] classic pipeline creation failed: {MetalBindings.GetErrorDescription(nsErr)}");
                    MetalBindings.Release(lib);
                    MetalBindings.Release(src);
                    MetalBindings.Release(desc);
                    return;
                }

                TextureCreateInfo info = new(8, 8, 1, 1, 1, 1, 1, 4, Format.R8G8B8A8Unorm, DepthStencilMode.Depth, Target.Texture2D,
                    SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);
                using MetalTexture probe = new(_renderer.DeviceHandle, _renderer.CommandQueueHandle, info);

                MTLColor black = new(0, 0, 0, 1);
                nint passDesc = Metal4Bindings.Metal4New("MTL4RenderPassDescriptor");
                nint patts = MetalBindings.objc_msgSend(passDesc, MetalBindings.SelColorAttachments);
                nint patt0 = MetalBindings.objc_msgSend(patts, MetalBindings.SelObjectAtIndexedSubscript, (nuint)0);
                MetalBindings.objc_msgSend_void(patt0, MetalBindings.SelSetTexture, probe.TextureHandle);
                MetalBindings.objc_msgSend_void(patt0, MetalBindings.SelSetLoadAction, (nuint)MetalBindings.MTLLoadActionClear);
                unsafe
                {
                    MetalBindings.objc_msgSend_void(patt0, MetalBindings.SelSetClearColor, &black);
                }
                MetalBindings.objc_msgSend_void(patt0, MetalBindings.SelSetStoreAction, (nuint)MetalBindings.MTLStoreActionStore);

                nint colorBuf = MetalBindings.objc_msgSend(_device, MetalBindings.SelNewBufferWithLengthOptions, (nuint)16, (nuint)MetalBindings.MTLResourceStorageModeShared);
                float[] clr = { 1.0f, 0.0f, 1.0f, 1.0f };
                unsafe
                {
                    nint contents = MetalBindings.objc_msgSend(colorBuf, MetalBindings.SelContents);
                    fixed (float* p = clr)
                    {
                        for (int k = 0; k < 4; k++) *((float*)contents + k) = p[k];
                    }
                }

                nint atDesc = Metal4Bindings.Metal4New("MTL4ArgumentTableDescriptor");
                Metal4Bindings.m4_msgSend_void(atDesc, Metal4Bindings.SelSetMaxBufferBindCount, (nuint)31);
                Metal4Bindings.m4_msgSend_void(atDesc, Metal4Bindings.SelSetMaxTextureBindCount, (nuint)64);
                Metal4Bindings.m4_msgSend_void(atDesc, Metal4Bindings.SelSetMaxSamplerStateBindCount, (nuint)16);
                nint table = MetalBindings.objc_msgSend(_device, Metal4Bindings.SelNewArgumentTableWithDescriptorError, atDesc, nint.Zero);
                ulong addr = MetalBindings.objc_msgSend_ulong_ret(colorBuf, Metal4Bindings.SelGpuAddress);
                Metal4Bindings.m4_msgSend_void(table, Metal4Bindings.SelSetAddressAtIndex, addr, (nuint)0);

                int mAlloc = _renderer.M4AllocatorPool.Acquire();
                nint mAllocator = _renderer.M4AllocatorPool.GetAllocatorHandle(mAlloc);
                nint cb = _renderer.M4Queue.BeginCommandBuffer(_renderer.DeviceHandle, mAllocator);
                nint enc = MetalBindings.objc_msgSend(cb, Metal4Bindings.SelRenderCommandEncoderWithDescriptor, passDesc);
                MetalBindings.objc_msgSend_void(enc, MetalBindings.SelSetRenderPipelineState, classicPipeline);
                MetalBindings.objc_msgSend_void(enc, MetalBindings.SelSetCullMode, (nuint)MetalBindings.MTLCullModeNone);
                Metal4Bindings.m4_msgSend_void(enc, Metal4Bindings.SelSetArgumentTableAtStages, table, Metal4Bindings.MTLRenderStageFragment);
                MetalBindings.objc_msgSend_void(enc, MetalBindings.SelDrawPrimitivesVertexStartVertexCount,
                    (nuint)MetalBindings.MTLPrimitiveTypeTriangle, (nuint)0, (nuint)3);
                MetalBindings.objc_msgSend_void(enc, MetalBindings.SelEndEncoding);
                _renderer.M4Queue.EndCommandBuffer(cb);

                nint[] cbArr = { cb };
                ulong sig = _renderer.M4Queue.CommitBatch(cbArr);
                for (int i = 0; i < 20 && _renderer.M4Queue.SignaledValue < sig; i++)
                {
                    Metal4Bindings.m4_wait_event_bool(_renderer.M4Queue.CompletionEvent, Metal4Bindings.SelWaitUntilSignaledValueTimeoutMS, sig, 250);
                }

                byte[] px = new byte[4];
                MTLRegion region = new(4, 4, 0, 1, 1, 1);
                unsafe
                {
                    fixed (byte* p = px)
                    {
                        MetalBindings.objc_msgSend_void(probe.TextureHandle, MetalBindings.SelGetBytesBytesPerRowFromRegionMipmapLevel, p, (nuint)(8), &region, 0);
                    }
                }

                Logger.Warning?.Print(LogClass.Gpu, $"[CLASSPROBE] classic pso=0x{classicPipeline:X} pixel=({px[0]},{px[1]},{px[2]},{px[3]})  (255,0,255 if classic PSO rasterizes on M4 encoder)");

                _renderer.M4AllocatorPool.Release(mAlloc);
                MetalBindings.Release(passDesc);
                MetalBindings.Release(cb);
                MetalBindings.Release(lib);
                MetalBindings.Release(src);
                MetalBindings.Release(desc);
                MetalBindings.Release(colorBuf);
                MetalBindings.Release(atDesc);
                MetalBindings.Release(table);
                MetalBindings.Release(classicPipeline);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"[CLASSPROBE] exception: {ex.Message}");
            }
        }

        private void RunGameDrawCaptureProbe()
        {
            // Draws the CURRENT game draw (same PSO + vertex/index buffers + viewport) into a
            // fresh OFFSCREEN probe texture instead of the game's present target. If this probe
            // rasterizes non-black, the game's draw machinery (PSO/buffers/state) is fine and the
            // failure is specific to the game's actual attachment/commit path. If it stays black,
            // the game's own draw commands themselves produce no output.
            if (_renderer == null || _program == null)
            {
                return;
            }

            if (_totalDrawCount != 5 && _totalDrawCount != 100 && _totalDrawCount != 500 && _totalDrawCount != 1500)
            {
                return;
            }
            if (!_gameDrawProbeFiredDraws.Add(_totalDrawCount))
            {
                return;
            }

            try
            {
                TextureCreateInfo info = new(64, 64, 1, 1, 1, 1, 1, 4, Format.R8G8B8A8Unorm, DepthStencilMode.Depth, Target.Texture2D,
                    SwizzleComponent.Red, SwizzleComponent.Green, SwizzleComponent.Blue, SwizzleComponent.Alpha);

                using MetalTexture probe = new(_renderer.DeviceHandle, _renderer.CommandQueueHandle, info);

                nint gamePipeline = GetOrCreatePipelineState();

                MTLColor black = new(0, 0, 0, 1);
                nint passDesc = Metal4Bindings.Metal4New("MTL4RenderPassDescriptor");
                nint atts = MetalBindings.objc_msgSend(passDesc, MetalBindings.SelColorAttachments);
                nint att0 = MetalBindings.objc_msgSend(atts, MetalBindings.SelObjectAtIndexedSubscript, (nuint)0);
                MetalBindings.objc_msgSend_void(att0, MetalBindings.SelSetTexture, probe.TextureHandle);
                MetalBindings.objc_msgSend_void(att0, MetalBindings.SelSetLoadAction, (nuint)MetalBindings.MTLLoadActionClear);
                unsafe
                {
                    MetalBindings.objc_msgSend_void(att0, MetalBindings.SelSetClearColor, &black);
                }
                MetalBindings.objc_msgSend_void(att0, MetalBindings.SelSetStoreAction, (nuint)MetalBindings.MTLStoreActionStore);

                int alloc = _renderer.M4AllocatorPool.Acquire();

                if (alloc < 0)
                {
                    MetalBindings.Release(passDesc);
                    return;
                }

                nint allocator = _renderer.M4AllocatorPool.GetAllocatorHandle(alloc);
                nint cb = _renderer.M4Queue.BeginCommandBuffer(_renderer.DeviceHandle, allocator);
                nint enc = MetalBindings.objc_msgSend(cb, Metal4Bindings.SelRenderCommandEncoderWithDescriptor, passDesc);

                MetalBindings.objc_msgSend_void(enc, MetalBindings.SelSetRenderPipelineState, gamePipeline);
                MetalBindings.objc_msgSend_void(enc, MetalBindings.SelSetCullMode, (nuint)MetalBindings.MTLCullModeNone);
                MetalBindings.objc_msgSend_void(enc, MetalBindings.SelSetFrontFacingWinding, (nuint)MetalBindings.MTLWindingCounterClockwise);

                if (_viewports.Length > 0)
                {
                    Viewport vp = _viewports[0];
                    MTLViewport vp2 = new(0, 0, probe.Width, probe.Height, vp.DepthNear, vp.DepthFar);
                    unsafe
                    {
                        MetalBindings.objc_msgSend_void(enc, MetalBindings.SelSetViewport, &vp2);
                    }
                }

                // Feed the probe with the SAME resources/geometry the real DrawInternal uses:
                // all vertex buffers at VB slots, all uniform/storage buffers, textures, images,
                // then replay the real indexed draw. This answers: does the game's actual draw
                // (PSO + resources + geometry) rasterize offscreen?
                if (EnsureArgumentTables())
                {
                    for (int i = 0; i < _vertexBuffers.Length; i++)
                    {
                        uint slot = VertexBufferSlotOffset + (uint)i;
                        if (slot >= 31) break;
                        VertexBufferDescriptor vbf = _vertexBuffers[i];
                        nint buf = _renderer.GetBuffer(vbf.Buffer.Handle);
                        if (buf != nint.Zero)
                        {
                            BindTableBuffer(_argumentTableVertex, buf, (uint)vbf.Buffer.Offset, slot);
                        }
                    }

                    foreach ((_, BufferAssignment assignment) in _uniformBuffers)
                    {
                        BindTableBufferForSet(_argumentTableVertex, ShaderStage.Vertex, assignment, UniformBufferSet, Interop.SpirvCross.MslResourceKind.UniformBuffer);
                        BindTableBufferForSet(_argumentTableFragment, ShaderStage.Fragment, assignment, UniformBufferSet, Interop.SpirvCross.MslResourceKind.UniformBuffer);
                    }
                    foreach ((_, BufferAssignment assignment) in _storageBuffers)
                    {
                        BindTableBufferForSet(_argumentTableVertex, ShaderStage.Vertex, assignment, StorageBufferSet, Interop.SpirvCross.MslResourceKind.StorageBuffer);
                        BindTableBufferForSet(_argumentTableFragment, ShaderStage.Fragment, assignment, StorageBufferSet, Interop.SpirvCross.MslResourceKind.StorageBuffer);
                    }
                    BindTexturesAndSamplers(_argumentTableVertex, ShaderStage.Vertex, _texturesVertex);
                    BindTexturesAndSamplers(_argumentTableFragment, ShaderStage.Fragment, _texturesFragment);
                    BindImages(_argumentTableVertex, ShaderStage.Vertex, _imagesVertex);
                    BindImages(_argumentTableFragment, ShaderStage.Fragment, _imagesFragment);

                    Metal4Bindings.m4_msgSend_void(enc, Metal4Bindings.SelSetArgumentTableAtStages, _argumentTableVertex, Metal4Bindings.MTLRenderStageVertex);
                    Metal4Bindings.m4_msgSend_void(enc, Metal4Bindings.SelSetArgumentTableAtStages, _argumentTableFragment, Metal4Bindings.MTLRenderStageFragment);
                }

                bool drew = false;

                if (_probeIndexed && _indexBuffer.Handle != 0 && _renderer.GetBuffer(_indexBuffer.Handle) != nint.Zero)
                {
                    nint indexBuffer = _renderer.GetBuffer(_indexBuffer.Handle);
                    ulong indexType = _indexType switch
                    {
                        IndexType.UInt => MetalBindings.MTLIndexTypeUInt32,
                        _ => MetalBindings.MTLIndexTypeUInt16,
                    };
                    ulong indexSize = _indexType == IndexType.UInt ? 4u : 2u;
                    ulong indexByteOffset = (ulong)_indexBuffer.Offset + (ulong)_probeFirstIndex * indexSize;
                    ulong indexAddress = MetalBindings.objc_msgSend_ulong_ret(indexBuffer, Metal4Bindings.SelGpuAddress) + indexByteOffset;
                    ulong bufferLength = MetalBindings.objc_msgSend_ulong_ret(indexBuffer, MetalBindings.SelLength);

                    Metal4Bindings.m4_msgSend_void(
                        enc,
                        Metal4Bindings.SelDrawIndexedPrimitivesIndexCountIndexTypeIndexBufferLengthInstanceCount,
                        (nuint)ToPrimitiveType(_topology),
                        (nuint)_probeCount,
                        (nuint)indexType,
                        indexAddress,
                        (nuint)(bufferLength - indexByteOffset),
                        (nuint)_probeInstanceCount);
                    drew = true;
                }
                else
                {
                    MetalBindings.objc_msgSend_void(
                        enc,
                        MetalBindings.SelDrawPrimitivesVertexStartVertexCountInstanceCount,
                        (nuint)MetalBindings.MTLPrimitiveTypeTriangle,
                        (nuint)0,
                        (nuint)3,
                        (nuint)1);
                    drew = true;
                }

                MetalBindings.objc_msgSend_void(enc, MetalBindings.SelEndEncoding);
                _renderer.M4Queue.EndCommandBuffer(cb);

                nint[] cbArr = { cb };
                ulong sig = _renderer.M4Queue.CommitBatch(cbArr);

                for (int i = 0; i < 20 && _renderer.M4Queue.SignaledValue < sig; i++)
                {
                    Metal4Bindings.m4_wait_event_bool(_renderer.M4Queue.CompletionEvent, Metal4Bindings.SelWaitUntilSignaledValueTimeoutMS, sig, 250);
                }

                byte[] px = new byte[4];
                MTLRegion region = new(32, 32, 0, 1, 1, 1);
                unsafe
                {
                    fixed (byte* p = px)
                    {
                        MetalBindings.objc_msgSend_void(probe.TextureHandle, MetalBindings.SelGetBytesBytesPerRowFromRegionMipmapLevel, p, (nuint)4, &region, 0);
                    }
                }

                Logger.Warning?.Print(LogClass.Gpu, $"[GAME_DRAW_PROBE] draw={_totalDrawCount} gamePipeline=0x{gamePipeline:X} indexed={_probeIndexed} drewIndexed={drew} count={_probeCount} vb={_vertexBuffers.Length} ub={_uniformBuffers.Count} texF={_texturesFragment.Count} func={_depthTest.Func} pixel=({px[0]},{px[1]},{px[2]},{px[3]})");

                foreach ((int binding, (ITexture texture, ISampler sampler)) in _texturesFragment)
                {
                    if (texture is MetalTexture mt && mt.TextureHandle != nint.Zero)
                    {
                        int w = mt.Width > 0 ? mt.Width : 1;
                        int h = mt.Height > 0 ? mt.Height : 1;
                        int bpp = 4;
                        byte[] texBytes = new byte[Math.Min(w * h * bpp, 64)];
                        MTLRegion tr = new(0, 0, 0, (nuint)Math.Min(w, 4), (nuint)Math.Min(h, 4), 1);
                        unsafe
                        {
                            fixed (byte* tp = texBytes)
                            {
                                MetalBindings.objc_msgSend_void(mt.TextureHandle, MetalBindings.SelGetBytesBytesPerRowFromRegionMipmapLevel, tp, (nuint)(Math.Min(w, 4) * bpp), &tr, 0);
                            }
                        }
                        int nonzero = 0;
                        for (int i = 0; i < texBytes.Length; i++) if (texBytes[i] != 0) nonzero++;
                        Logger.Warning?.Print(LogClass.Gpu, $"[GAME_DRAW_PROBE] fragTex bind={binding} handle=0x{mt.TextureHandle:X} {w}x{h} nonzeroBytes={nonzero}/{texBytes.Length} sample0=[{texBytes[0]},{texBytes[1]},{texBytes[2]},{texBytes[3]}]");
                    }
                }

                _renderer.M4AllocatorPool.Release(alloc);
                MetalBindings.Release(passDesc);
                MetalBindings.Release(cb);
            }
            catch (Exception ex)
            {
                Logger.Error?.Print(LogClass.Gpu, $"[GAME_DRAW_PROBE] exception: {ex.Message}");
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

            bool hasLarge = false;
            for (int i = 0; i < colors.Length; i++)
            {
                if (colors[i] is MetalTexture mt && (mt.Width >= 960 || mt.Height >= 540))
                {
                    hasLarge = true;
                    break;
                }
            }

            if (_setTargetsLogCount++ < 100 || (_setTargetsLogCount % 600 == 0))
            {
                string targets = "";
                for (int i = 0; i < colors.Length; i++)
                {
                    targets += (colors[i] is MetalTexture mt ? $"[#{i}: 0x{mt.TextureHandle:X} {mt.Width}x{mt.Height} {mt.Format}] " : "[null] ");
                }
                Logger.Warning?.Print(LogClass.Gpu, $"[SET_TARGETS] #{_setTargetsLogCount}: count={colors.Length} {targets} depth={depthStencil != null}");
            }
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
            Dictionary<int, (ITexture Texture, ISampler Sampler)> target = stage switch
            {
                ShaderStage.Vertex => _texturesVertex,
                ShaderStage.Fragment => _texturesFragment,
                ShaderStage.Compute => _texturesCompute,
                _ => _texturesFragment
            };
            target[binding] = (texture, sampler);
        }

        public void SetImage(ShaderStage stage, int binding, ITexture texture)
        {
            Dictionary<int, ITexture> target = stage switch
            {
                ShaderStage.Vertex => _imagesVertex,
                ShaderStage.Fragment => _imagesFragment,
                ShaderStage.Compute => _imagesCompute,
                _ => _imagesFragment
            };
            target[binding] = texture;
        }

        public void SetTextureArray(ShaderStage stage, int binding, ITextureArray array) { }

        public void SetTextureArraySeparate(ShaderStage stage, int setIndex, ITextureArray array) { }

        public void SetImageArray(ShaderStage stage, int binding, IImageArray array) { }

        public void SetImageArraySeparate(ShaderStage stage, int setIndex, IImageArray array) { }

        public void SetBlendState(int index, BlendDescriptor blend)
        {
            if ((uint)index < MaxRenderTargets)
            {
                _blends[index] = blend;
            }
        }

        public void SetBlendState(AdvancedBlendDescriptor blend) { }

        public void SetDepthTest(DepthTestDescriptor depthTest)
        {
            _depthTest = depthTest;
            _depthDirty = true;
        }

        public void SetStencilTest(StencilTestDescriptor stencilTest)
        {
            _stencilTest = stencilTest;
            _depthDirty = true;
        }

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

        public void SetDepthBias(PolygonModeMask enables, float factor, float units, float clamp)
        {
            _depthBiasEnabled = enables != 0;
            _depthBiasFactor = factor;
            _depthBiasUnits = units;
            _depthBiasClamp = clamp;
        }

        public void SetPointParameters(float size, bool isProgramPointSize, bool enablePointSprite, Origin origin) { }

        public void SetAlphaTest(bool enable, float reference, CompareOp op) { }

        public void SetDepthMode(DepthMode mode) { }

        public void SetLineParameters(float width, bool smooth) { }

        public void SetLogicOpState(bool enable, LogicalOp op) { }

        public void SetMultisampleState(MultisampleDescriptor multisample)
        {
            _multisample = multisample;
        }

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

        public void Draw(int vertexCount, int instanceCount, int firstVertex, int firstInstance)        {
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

        public void DrawTexture(ITexture texture, ISampler sampler, Extents2DF srcRegion, Extents2DF dstRegion)
        {
            if (texture is not MetalTexture srcMetal || _colorTargetCount == 0 || _colorTargets[0] is not MetalTexture dstMetal)
            {
                return;
            }

            Logger.Warning?.Print(LogClass.Gpu, $"[BLIT_WRITER] src=0x{srcMetal.TextureHandle:X} {srcMetal.Width}x{srcMetal.Height} {srcMetal.Format} dst=0x{dstMetal.TextureHandle:X} {dstMetal.Width}x{dstMetal.Height} {dstMetal.Format} srcRegion={srcRegion} dstRegion={dstRegion}");

            EndRenderPass();

            bool linearFilter = true;
            if (sampler is MetalSampler ms)
            {
                linearFilter = ms.Info.MagFilter == MagFilter.Linear || ms.Info.MinFilter == MinFilter.Linear;
            }

            MetalFormatBlit blit = MetalTexture.GetFormatBlit(_device, _commandQueue);
            blit.Copy(srcMetal, dstMetal, srcRegion, dstRegion, linearFilter);
        }

        public void DispatchCompute(int groupsX, int groupsY, int groupsZ)
        {
            if (_program?.ComputeFunction == nint.Zero)
            {
                return;
            }

            if (groupsX <= 0 || groupsY <= 0 || groupsZ <= 0)
            {
                Logger.Error?.Print(LogClass.Gpu, $"[COMPUTE_REJECT] invalid dispatch dimensions ({groupsX}, {groupsY}, {groupsZ})");
                return;
            }

            // MTL4 command buffers are submitted in the order they are appended to
            // _frameBuffers. Close the active render pass before creating the compute
            // buffer so a guest render -> compute dependency cannot be submitted as
            // compute -> render at FlushFrame time.
            EndRenderPass();

            Logger.Warning?.Print(LogClass.Gpu, $"[COMPUTE_DISPATCH] groups=({groupsX},{groupsY},{groupsZ}) uniforms={_uniformBuffers.Count} storage={_storageBuffers.Count} textures={_texturesCompute.Count} images={_imagesCompute.Count}");

            int allocatorIndex = _renderer.M4AllocatorPool.Acquire();

            if (allocatorIndex < 0)
            {
                return;
            }

            nint commandBuffer = _renderer.M4Queue.BeginCommandBuffer(_device, _renderer.M4AllocatorPool.GetAllocatorHandle(allocatorIndex));
            nint encoder = nint.Zero;
            bool released = false;

            try
            {
                nint pipelineState = _program.GetOrCreateComputePipelineState();

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

                    // M4: compute binds ALL resources through a single MTL4ArgumentTable
                    // via setArgumentTable: (the M3 setBuffer:offset:atIndex: selector is
                    // not supported by the M4 compute context AGXG14GFamilyComputeContext_mtlnext).
                    if (EnsureComputeArgumentTable())
                    {
                        foreach ((_, BufferAssignment assignment) in _uniformBuffers)
                        {
                            BindTableBufferForSet(_argumentTableCompute, ShaderStage.Compute, assignment, UniformBufferSet, Interop.SpirvCross.MslResourceKind.UniformBuffer);
                        }

                        foreach ((_, BufferAssignment assignment) in _storageBuffers)
                        {
                            BindTableBufferForSet(_argumentTableCompute, ShaderStage.Compute, assignment, StorageBufferSet, Interop.SpirvCross.MslResourceKind.StorageBuffer);
                        }
                        
                        BindTexturesAndSamplers(_argumentTableCompute, ShaderStage.Compute, _texturesCompute);
                        BindImages(_argumentTableCompute, ShaderStage.Compute, _imagesCompute);

                        Metal4Bindings.m4_msgSend_void(encoder, Metal4Bindings.SelSetArgumentTableCompute, _argumentTableCompute);
                    }

                    MTLSize threadgroups = new((nuint)groupsX, (nuint)groupsY, (nuint)groupsZ);
                    MTLSize threadsPerGroup = new(64, 1, 1);

                    unsafe
                    {
                        MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelDispatchThreadgroupsThreadsPerThreadgroup, &threadgroups, &threadsPerGroup);
                    }

                    MetalBindings.objc_msgSend_void(encoder, MetalBindings.SelEndEncoding);
                    MetalBindings.Release(encoder);
                    encoder = nint.Zero;

                    _renderer.M4Queue.EndCommandBuffer(commandBuffer);

                    _frameBuffers.Add(commandBuffer);
                    _frameAllocatorIndices.Add(allocatorIndex);
                    commandBuffer = nint.Zero;
                    
                    released = true;
                }
                finally
                {
                    if (!released && commandBuffer != nint.Zero)
                    {
                        _renderer.M4Queue.EndCommandBuffer(commandBuffer);
                        _renderer.M4AllocatorPool.Release(allocatorIndex);
                    }
                }
            }
            finally
            {
                MetalBindings.Release(encoder);
                if (commandBuffer != nint.Zero)
                {
                    MetalBindings.Release(commandBuffer);
                }

                if (!released)
                {
                    _renderer.M4AllocatorPool.Release(allocatorIndex);
                }
            }
        }



        public void Barrier()
        {
            EndRenderPass();
        }

        public void CommandBufferBarrier()
        {
            EndRenderPass();
        }

        public void TextureBarrier()
        {
            EndRenderPass();
        }

        public void TextureBarrierTiled()
        {
            EndRenderPass();
        }

        public void ClearRenderTargetColor(int index, int layer, int layerCount, uint componentMask, ColorF color)
        {
            if (_renderEncoder != nint.Zero)
            {
                EndRenderPass();
            }

            if ((uint)index < (uint)_clearColors.Length)
            {
                _clearColorMask |= (1u << index);
                _clearColors[index] = color;
                _hasClearColor = true;
            }
        }

        public void ClearRenderTargetDepthStencil(
            int layer,
            int layerCount,
            float depthValue,
            bool depthMask,
            int stencilValue,
            int stencilMask)
        {
            if (_renderEncoder != nint.Zero)
            {
                EndRenderPass();
            }

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
                PrimitiveTopology.Lines or PrimitiveTopology.LineLoop => MetalBindings.MTLPrimitiveTypeLine,
                PrimitiveTopology.LineStrip => MetalBindings.MTLPrimitiveTypeLineStrip,
                PrimitiveTopology.TriangleStrip or PrimitiveTopology.QuadStrip => MetalBindings.MTLPrimitiveTypeTriangleStrip,
                _ => MetalBindings.MTLPrimitiveTypeTriangle,
            };
        }

        private static ulong ToTopologyClass(PrimitiveTopology topology)
        {
            return topology switch
            {
                PrimitiveTopology.Points => MetalBindings.MTLPrimitiveTopologyClassPoint,
                PrimitiveTopology.Lines or PrimitiveTopology.LineLoop or PrimitiveTopology.LineStrip => MetalBindings.MTLPrimitiveTopologyClassLine,
                PrimitiveTopology.Triangles or PrimitiveTopology.TriangleStrip or PrimitiveTopology.TriangleFan or PrimitiveTopology.Quads or PrimitiveTopology.QuadStrip or PrimitiveTopology.Polygon => MetalBindings.MTLPrimitiveTopologyClassTriangle,
                _ => MetalBindings.MTLPrimitiveTopologyClassUnspecified,
            };
        }

        private static ulong ToCompareFunction(CompareOp op) => MetalFormats.ToMtlCompareFunction(op);

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

            if (_argumentTableVertex != nint.Zero)
            {
                MetalBindings.Release(_argumentTableVertex);
                _argumentTableVertex = nint.Zero;
            }

            if (_argumentTableFragment != nint.Zero)
            {
                MetalBindings.Release(_argumentTableFragment);
                _argumentTableFragment = nint.Zero;
            }

            if (_argumentTableCompute != nint.Zero)
            {
                MetalBindings.Release(_argumentTableCompute);
                _argumentTableCompute = nint.Zero;
            }

            GC.SuppressFinalize(this);
        }
    }
}
