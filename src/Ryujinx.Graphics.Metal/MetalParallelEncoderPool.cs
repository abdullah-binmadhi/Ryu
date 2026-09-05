using Ryujinx.Graphics.Metal.Interop;
using System;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// Multi-threaded parallel encoder pool for Metal 4 on Apple Silicon.
    ///
    /// Distributes render and compute encoding across multiple worker threads (typically
    /// matching the host's 4 Performance cores). Each worker encodes into its own dedicated
    /// <c>MTL4CommandBuffer</c> backed by its own <c>MTL4CommandAllocator</c> and stage-level
    /// <c>MTL4ArgumentTable</c> instances.
    ///
    /// The resulting command buffers are submitted in order as a single batch using
    /// <c>MTL4CommandQueue.commit:count:</c>, bypassing single-core driver bottlenecks.
    /// </summary>
    [SupportedOSPlatform("macos26.0")]
    public sealed class MetalParallelEncoderPool : IDisposable
    {
        private readonly nint _device;
        private readonly Metal4CommandQueue _queue;
        private readonly Metal4CommandAllocatorPool _allocatorPool;
        private readonly int _workerCount;

        private readonly nint[] _stageTables;
        private readonly bool _initialized;

        public int WorkerCount => _workerCount;

        public MetalParallelEncoderPool(
            nint device,
            Metal4CommandQueue queue,
            Metal4CommandAllocatorPool allocatorPool,
            int workerCount = 4)
        {
            _device = device;
            _queue = queue;
            _allocatorPool = allocatorPool;
            _workerCount = Math.Clamp(workerCount, 1, 16);

            _stageTables = new nint[_workerCount];

            try
            {
                nint atDesc = Metal4Bindings.Metal4New("MTL4ArgumentTableDescriptor");
                MetalBindings.objc_msgSend_void(atDesc, Metal4Bindings.SelSetMaxBufferBindCount, (nuint)31);
                MetalBindings.objc_msgSend_void(atDesc, Metal4Bindings.SelSetMaxTextureBindCount, (nuint)128);
                MetalBindings.objc_msgSend_void(atDesc, Metal4Bindings.SelSetMaxSamplerStateBindCount, (nuint)16);

                for (int i = 0; i < _workerCount; i++)
                {
                    _stageTables[i] = MetalBindings.objc_msgSend(_device, Metal4Bindings.SelNewArgumentTableWithDescriptorError, atDesc, nint.Zero);
                }

                MetalBindings.Release(atDesc);
                _initialized = true;
            }
            catch
            {
                _initialized = false;
            }
        }

        /// <summary>
        /// Executes a parallel encoding job across all worker threads.
        /// </summary>
        /// <param name="taskCount">The number of work items to encode.</param>
        /// <param name="encodeCallback">Callback receiving (taskIndex, workerIndex, commandBuffer, argumentTable).</param>
        /// <param name="waitForGpu">Whether to synchronously wait for GPU completion.</param>
        public void EncodeParallel(int taskCount, Action<int, int, nint, nint> encodeCallback, bool waitForGpu = true)
        {
            if (!_initialized || taskCount <= 0)
            {
                return;
            }

            int count = Math.Min(taskCount, _workerCount);
            nint[] commandBuffers = new nint[count];
            int[] allocIndices = new int[count];
            Thread[] threads = new Thread[count];

            for (int i = 0; i < count; i++)
            {
                int workerIndex = i;
                int taskIndex = i;

                allocIndices[workerIndex] = _allocatorPool.Acquire();
                nint allocHandle = _allocatorPool.GetAllocatorHandle(allocIndices[workerIndex]);

                threads[i] = new Thread(() =>
                {
                    nint cb = _queue.BeginCommandBuffer(_device, allocHandle);
                    encodeCallback(taskIndex, workerIndex, cb, _stageTables[workerIndex]);
                    _queue.EndCommandBuffer(cb);
                    commandBuffers[workerIndex] = cb;
                });
                threads[i].Priority = ThreadPriority.Highest;
                threads[i].Start();
            }

            for (int i = 0; i < count; i++)
            {
                threads[i].Join();
            }

            if (waitForGpu)
            {
                _queue.SubmitAndWait(commandBuffers);
            }
            else
            {
                _queue.CommitBatch(commandBuffers);
            }

            for (int i = 0; i < count; i++)
            {
                if (allocIndices[i] >= 0)
                {
                    _allocatorPool.Release(allocIndices[i]);
                }
            }
        }

        public void Dispose()
        {
            for (int i = 0; i < _stageTables.Length; i++)
            {
                if (_stageTables[i] != nint.Zero)
                {
                    MetalBindings.Release(_stageTables[i]);
                    _stageTables[i] = nint.Zero;
                }
            }
        }
    }
}
