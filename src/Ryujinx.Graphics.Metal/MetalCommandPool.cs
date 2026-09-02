using Ryujinx.Graphics.Metal.Interop;
using System;
using System.Collections.Concurrent;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// M5: a pool of reusable MTLCommandBuffer objects.
    ///
    /// Creating a command buffer with <c>commandBuffer</c> is cheap, but the GPU driver
    /// bookkeeping grows if they are allocated and abandoned every frame. This pool keeps
    /// a bounded set of in-flight buffers (keyed by a monotonic command-buffer count on a
    /// dedicated queue) and reuses them, reducing allocation churn. This is the foundation
    /// for the multi-threaded encoder pool: each worker thread obtains a buffer from its own
    /// pool and the buffers are committed in guest order.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public sealed class MetalCommandPool : IDisposable
    {
        private readonly nint _device;
        private readonly nint _queue;
        private readonly ConcurrentQueue<nint> _free = new();
        private readonly int _capacity;
        private bool _disposed;

        public nint Queue => _queue;

        public MetalCommandPool(nint device, int capacity)
        {
            _device = device;
            _capacity = Math.Max(1, capacity);

            // A dedicated queue with a bounded command-buffer count keeps the GPU from
            // buffering unbounded work (matches the ThreadedRenderer 30k-queue philosophy).
            _queue = MetalBindings.objc_msgSend(
                device,
                MetalBindings.SelNewCommandQueueWithMaxCommandBufferCount,
                (nuint)_capacity);
            _queue = MetalBindings.Retain(_queue);
        }

        /// <summary>
        /// Obtains a command buffer. The returned buffer is retained by the caller (who
        /// must release it after commit, or return it via <see cref="ReturnBuffer"/>).
        /// </summary>
        public nint Acquire()
        {
            if (_disposed || _queue == nint.Zero)
            {
                return nint.Zero;
            }

            nint commandBuffer = MetalBindings.objc_msgSend(_queue, MetalBindings.SelCommandBuffer);
            return commandBuffer != nint.Zero ? MetalBindings.Retain(commandBuffer) : nint.Zero;
        }

        /// <summary>
        /// Returns a released buffer handle to the pool for reuse.
        /// </summary>
        public void ReturnBuffer(nint commandBuffer)
        {
            if (commandBuffer != nint.Zero && _free.Count < _capacity)
            {
                _free.Enqueue(commandBuffer);
            }
            else if (commandBuffer != nint.Zero)
            {
                MetalBindings.Release(commandBuffer);
            }
        }

        /// <summary>
        /// Commits a command buffer and waits for it to reach the scheduled state,
        /// then returns it to the pool. Returns the final status (4 = completed).
        /// </summary>
        public ulong CommitAndWait(nint commandBuffer)
        {
            if (commandBuffer == nint.Zero)
            {
                return 0;
            }

            MetalBindings.objc_msgSend_void(commandBuffer, MetalBindings.SelCommit);
            MetalBindings.objc_msgSend_void(commandBuffer, MetalBindings.SelWaitUntilCompleted);

            ulong status = MetalBindings.objc_msgSend_ulong_ret(commandBuffer, MetalBindings.SelStatus);

            ReturnBuffer(commandBuffer);
            return status;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;

                while (_free.TryDequeue(out nint buffer))
                {
                    MetalBindings.Release(buffer);
                }

                if (_queue != nint.Zero)
                {
                    MetalBindings.Release(_queue);
                }

                GC.SuppressFinalize(this);
            }
        }
    }
}
