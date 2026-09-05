using Ryujinx.Graphics.Metal.Interop;
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Metal 4 command submission layer.
    ///
    /// Wraps an <c>MTL4CommandQueue</c> and provides the parallel-encoder submission
    /// primitive validated in the prototype phase:
    ///
    ///   * worker threads each encode into their OWN <c>MTL4CommandBuffer</c> begun with
    ///     their OWN per-thread <c>MTL4CommandAllocator</c>,
    ///   * <c>commit:count:</c> submits the whole batch as one group,
    ///   * an <c>MTLSharedEvent</c> signalled on the queue provides block-free CPU
    ///     completion detection (<c>waitUntilSignaledValue:timeoutMS:</c>), so no ObjC
    ///     blocks need to be constructed from C#.
    ///
    /// This replaces the single-threaded MTLCommandBuffer path that bottlenecked the
    /// old backend (a per-thread encoder pool was the documented perf design; Metal 4
    /// makes it a first-class, validated path).
    /// </summary>
    [SupportedOSPlatform("macos26.0")]
    public sealed class Metal4CommandQueue : IDisposable
    {
        private nint _queue;
        private nint _completionEvent;
        private nint _residencySet;

        private ulong _completionValue;
        private readonly object _lock = new();

        public nint Handle => _queue;
        public bool IsValid => _queue != nint.Zero;
        public nint CompletionEvent => _completionEvent;
        public nint ResidencySet => _residencySet;
        public ulong LastSignaledValue => _completionValue;
        public ulong SignaledValue => _completionEvent != nint.Zero
            ? MetalBindings.objc_msgSend_ulong_ret(_completionEvent, MetalBindings.sel_registerName("signaledValue"))
            : 0;

        public Metal4CommandQueue(nint device)
        {
            _queue = Metal4Bindings.m4_msgSend(device, Metal4Bindings.SelNewMTL4CommandQueue);
            if (_queue == nint.Zero)
            {
                throw new InvalidOperationException("MTL4CommandQueue creation failed (needs macOS 26)");
            }

            _completionEvent = Metal4Bindings.m4_msgSend(device, Metal4Bindings.SelNewSharedEvent);
            _residencySet = CreateResidencySet(device);

            if (_residencySet != nint.Zero)
            {
                unsafe
                {
                    nint* residencySets = stackalloc nint[1];
                    residencySets[0] = _residencySet;
                    Metal4Bindings.m4_msgSend_void_array(_queue, Metal4Bindings.SelAddResidencySetsCount, residencySets, 1);
                }
            }
        }

        private static unsafe nint CreateResidencySet(nint device)
        {
            nint descriptor = Metal4Bindings.Metal4New("MTLResidencySetDescriptor");

            if (descriptor == nint.Zero)
            {
                return nint.Zero;
            }

            try
            {
                nint error = nint.Zero;
                return MetalBindings.objc_msgSend(device, Metal4Bindings.SelNewResidencySetWithDescriptorError, descriptor, (nint)(&error));
            }
            finally
            {
                MetalBindings.Release(descriptor);
            }
        }

        /// <summary>
        /// Adds a Metal allocation to the queue-owned residency set. Metal 4 does not
        /// implicitly make resources resident when shaders access them through argument
        /// tables or GPU addresses.
        /// </summary>
        public void AddResidencyResource(nint resource)
        {
            if (_residencySet == nint.Zero || resource == nint.Zero)
            {
                return;
            }

            lock (_lock)
            {
                Metal4Bindings.m4_msgSend_void(_residencySet, Metal4Bindings.SelAddAllocation, resource);
                Metal4Bindings.m4_msgSend_void(_residencySet, Metal4Bindings.SelCommitResidencySet);
                Metal4Bindings.m4_msgSend_void(_residencySet, Metal4Bindings.SelRequestResidency);
            }
        }

        /// <summary>
        /// Begins a new command buffer bound to <paramref name="allocator"/> (the calling
        /// thread's own allocator), encodes with render/compute encoders, then returns it
        /// alongside its end-byte via <see cref="EndCommandBuffer"/>.
        /// </summary>
        public nint BeginCommandBuffer(nint device, nint allocator)
        {
            nint cb = Metal4Bindings.m4_msgSend(device, Metal4Bindings.SelNewCommandBuffer);
            Metal4Bindings.m4_msgSend_void(cb, Metal4Bindings.SelBeginCommandBufferWithAllocator, allocator);
            return cb;
        }

        public void EndCommandBuffer(nint commandBuffer)
        {
            Metal4Bindings.m4_msgSend_void(commandBuffer, Metal4Bindings.SelEndCommandBuffer);
        }

        /// <summary>
        /// Commits a batch of parallel-encoded command buffers as a group and waits for
        /// GPU completion using the shared event (block-free). Returns the new completion value.
        /// </summary>
        public ulong SubmitAndWait(ReadOnlySpan<nint> commandBuffers, ulong timeoutMS = 5000)
        {
            if (commandBuffers.IsEmpty)
            {
                return _completionValue;
            }

            ulong signalValue;
            lock (_lock)
            {
                signalValue = ++_completionValue;
                unsafe
                {
                    fixed (nint* buffers = commandBuffers)
                    {
                        Metal4Bindings.m4_msgSend_void_array(_queue, Metal4Bindings.SelCommitCount, buffers, (nuint)commandBuffers.Length);
                    }
                }

                // Schedule the GPU-side signal AFTER the committed group completes.
                Metal4Bindings.m4_msgSend_void(_queue, Metal4Bindings.SelSignalEventValue, _completionEvent, signalValue);
            }

            // Block-free CPU wait on the shared event.
            bool signaled = Metal4Bindings.m4_wait_event_bool(_completionEvent, Metal4Bindings.SelWaitUntilSignaledValueTimeoutMS, signalValue, timeoutMS);
            if (!signaled)
            {
                Ryujinx.Common.Logging.Logger.Error?.Print(Ryujinx.Common.Logging.LogClass.Gpu, $"MTL4 shared-event wait timed out after {timeoutMS} ms (signal={signalValue}).");
            }

            LogCommandBufferResults(commandBuffers, signalValue, signaled);
            return signalValue;
        }

        /// <summary>
        /// Asynchronously commits a batch of command buffers using commit:count: and schedules the shared-event signal.
        /// </summary>
        private static void LogCommandBufferResults(ReadOnlySpan<nint> commandBuffers, ulong signalValue, bool completed)
        {
            // MTL4CommandBuffer does not implement the legacy MTLCommandBuffer
            // status selector. Querying SelStatus here aborts the process with
            // NSInvalidArgumentException on AGXG14GFamilyCommandBuffer_mtlnext.
            // Completion is established by the MTLSharedEvent; only inspect the
            // M4 error property when that completion signal was not observed.
            if (completed)
            {
                return;
            }

            for (int i = 0; i < commandBuffers.Length; i++)
            {
                nint commandBuffer = commandBuffers[i];
                nint error = commandBuffer != nint.Zero
                    ? MetalBindings.objc_msgSend(commandBuffer, Metal4Bindings.SelError)
                    : nint.Zero;
                string description = error != nint.Zero ? MetalBindings.GetErrorDescription(error) : "none";
                Ryujinx.Common.Logging.Logger.Error?.Print(
                    Ryujinx.Common.Logging.LogClass.Gpu,
                    $"MTL4 command buffer {i} result: signal={signalValue} completed=false error={description}");
            }
        }

        public ulong CommitBatch(ReadOnlySpan<nint> commandBuffers)
        {
            if (commandBuffers.IsEmpty)
            {
                return _completionValue;
            }

            ulong signalValue;
            lock (_lock)
            {
                signalValue = ++_completionValue;
                unsafe
                {
                    fixed (nint* buffers = commandBuffers)
                    {
                        Metal4Bindings.m4_msgSend_void_array(_queue, Metal4Bindings.SelCommitCount, buffers, (nuint)commandBuffers.Length);
                    }
                }

                Metal4Bindings.m4_msgSend_void(_queue, Metal4Bindings.SelSignalEventValue, _completionEvent, signalValue);
            }

            return signalValue;
        }

        public void Dispose()
        {
            if (IsValid)
            {
                MetalBindings.Release(_residencySet);
                MetalBindings.Release(_queue);
                MetalBindings.Release(_completionEvent);
                _residencySet = nint.Zero;
                _queue = nint.Zero;
                _completionEvent = nint.Zero;
            }
        }
    }

    /// <summary>
    /// A per-thread command allocator. Metal 4 requires that each thread encoding
    /// concurrently owns a dedicated <c>MTL4CommandAllocator</c>; allocators are
    /// reusable after their command buffer completes.
    /// </summary>
    [SupportedOSPlatform("macos26.0")]
    public sealed class Metal4CommandAllocator : IDisposable
    {
        private nint _handle;

        public nint Handle => _handle;
        public bool IsValid => _handle != nint.Zero;

        public Metal4CommandAllocator(nint device)
        {
            _handle = Metal4Bindings.m4_msgSend(device, Metal4Bindings.SelNewCommandAllocator);
            if (_handle == nint.Zero)
            {
                throw new InvalidOperationException("MTL4CommandAllocator creation failed");
            }
        }

        public void Dispose()
        {
            if (IsValid)
            {
                MetalBindings.Release(_handle);
                _handle = nint.Zero;
            }
        }
    }

    /// <summary>
    /// A pool of <c>MTL4CommandAllocator</c>s shared by the render pipeline. An
    /// allocator is bound to a command buffer via <c>beginCommandBufferWithAllocator:</c>
    /// and must not be reused until that command buffer completes. This pool hands
    /// out allocators round-robin, tracking in-use slots, and grows on demand so a
    /// frame with many concurrent render passes never exhausts the pool.
    /// </summary>
    [SupportedOSPlatform("macos26.0")]
    public sealed class Metal4CommandAllocatorPool : IDisposable
    {
        private readonly nint _device;
        private readonly int _maxPoolSize;
        private readonly List<Metal4CommandAllocator> _allocators = new();
        private bool[] _inUse = Array.Empty<bool>();
        private int _next;
        private readonly object _lock = new();

        public Metal4CommandAllocatorPool(nint device, int initialPoolSize = 4, int maxPoolSize = 128)
        {
            _device = device;
            _maxPoolSize = Math.Clamp(maxPoolSize, 1, 1024);

            for (int i = 0; i < Math.Clamp(initialPoolSize, 1, _maxPoolSize); i++)
            {
                _allocators.Add(new Metal4CommandAllocator(_device));
            }

            Array.Resize(ref _inUse, _allocators.Count);
        }

        public int Size => _allocators.Count;

        /// <summary>
        /// Acquires an allocator for a new command buffer. Grows the pool when all
        /// allocators are bound to live command buffers. Returns -1 above the cap.
        /// </summary>
        public int Acquire()
        {
            lock (_lock)
            {
                int count = _allocators.Count;

                for (int i = 0; i < count; i++)
                {
                    int index = (_next + i) % count;

                    if (!_inUse[index])
                    {
                        _inUse[index] = true;
                        _next = (index + 1) % count;
                        return index;
                    }
                }

                if (count >= _maxPoolSize)
                {
                    return -1;
                }

                int newIndex = count;
                _allocators.Add(new Metal4CommandAllocator(_device));
                Array.Resize(ref _inUse, newIndex + 1);
                _inUse[newIndex] = true;
                _next = 0;
                return newIndex;
            }
        }

        public nint GetAllocatorHandle(int index)
        {
            return _allocators[index].Handle;
        }

        /// <summary>
        /// Returns an allocator to the pool. Call only after the command buffer
        /// recorded on it has completed.
        /// </summary>
        public void Release(int index)
        {
            lock (_lock)
            {
                _inUse[index] = false;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                for (int i = 0; i < _allocators.Count; i++)
                {
                    _allocators[i].Dispose();
                }

                _allocators.Clear();
            }
        }
    }
}