using Ryujinx.Graphics.Metal.Interop;
using System;
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

        private ulong _completionValue;
        private readonly object _lock = new();

        public nint Handle => _queue;
        public bool IsValid => _queue != nint.Zero;

        public Metal4CommandQueue(nint device)
        {
            _queue = Metal4Bindings.m4_msgSend(device, Metal4Bindings.SelNewMTL4CommandQueue);
            if (_queue == nint.Zero)
            {
                throw new InvalidOperationException("MTL4CommandQueue creation failed (needs macOS 26)");
            }

            _completionEvent = Metal4Bindings.m4_msgSend(device, Metal4Bindings.SelNewSharedEvent);
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
                // The return is BOOL: waitUntilSignaledValue:timeoutMS: -> YES if value reached.
                throw new TimeoutException($"MTL4 shared-event wait timed out after {timeoutMS} ms");
            }

            return signalValue;
        }

        public void Dispose()
        {
            if (IsValid)
            {
                MetalBindings.Release(_queue);
                MetalBindings.Release(_completionEvent);
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
}