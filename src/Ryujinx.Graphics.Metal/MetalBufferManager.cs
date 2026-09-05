using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Metal.Interop;
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    public class MetalBufferManager : IDisposable
    {
        private readonly nint _device;
        private readonly Metal4CommandQueue _m4Queue;
        private readonly ConcurrentDictionary<int, nint> _buffers = new();
        private int _nextHandle = 1;

        public MetalBufferManager(nint device, Metal4CommandQueue m4Queue)
        {
            _device = device;
            _m4Queue = m4Queue;
        }

        private static BufferHandle CreateHandle(int id)
        {
            ulong val = (ulong)id;
            return Unsafe.As<ulong, BufferHandle>(ref val);
        }

        private static int GetHandleId(BufferHandle handle)
        {
            return (int)handle;
        }

        public BufferHandle CreateBuffer(int size)
        {
            nint buffer = MetalBindings.objc_msgSend(
                _device,
                MetalBindings.SelNewBufferWithLengthOptions,
                (nuint)size,
                (nuint)(MetalBindings.MTLResourceStorageModeShared | MetalBindings.MTLResourceCPUCacheModeDefaultCache));

            _m4Queue.AddResidencyResource(buffer);
            int handle = Interlocked.Increment(ref _nextHandle);
            _buffers[handle] = buffer;
            return CreateHandle(handle);
        }

        public BufferHandle CreateBuffer(nint pointer, int size)
        {
            nint buffer = MetalBindings.objc_msgSend(
                _device,
                MetalBindings.SelNewBufferWithBytesNoCopyOptions,
                pointer,
                (nuint)size,
                (nuint)(MetalBindings.MTLResourceStorageModeShared | MetalBindings.MTLResourceCPUCacheModeDefaultCache),
                nint.Zero);

            _m4Queue.AddResidencyResource(buffer);
            int handle = Interlocked.Increment(ref _nextHandle);
            _buffers[handle] = buffer;
            return CreateHandle(handle);
        }

        public nint GetBuffer(BufferHandle handle)
        {
            _buffers.TryGetValue(GetHandleId(handle), out nint buffer);
            return buffer;
        }

        public unsafe PinnedSpan<byte> GetData(BufferHandle handle, int offset, int size)
        {
            if (_buffers.TryGetValue(GetHandleId(handle), out nint buffer) && buffer != nint.Zero)
            {
                nint contents = MetalBindings.objc_msgSend(buffer, MetalBindings.SelContents);
                byte* ptr = (byte*)contents + offset;
                return new PinnedSpan<byte>(ptr, size);
            }
            return PinnedSpan<byte>.UnsafeFromSpan(ReadOnlySpan<byte>.Empty);
        }

        public unsafe void SetData(BufferHandle handle, int offset, ReadOnlySpan<byte> data)
        {
            if (_buffers.TryGetValue(GetHandleId(handle), out nint buffer) && buffer != nint.Zero)
            {
                nint contents = MetalBindings.objc_msgSend(buffer, MetalBindings.SelContents);
                byte* dest = (byte*)contents + offset;
                fixed (byte* src = data)
                {
                    Buffer.MemoryCopy(src, dest, data.Length, data.Length);
                }
            }
        }

        public void DeleteBuffer(BufferHandle handle)
        {
            if (_buffers.TryRemove(GetHandleId(handle), out nint buffer) && buffer != nint.Zero)
            {
                MetalBindings.objc_msgSend_void(buffer, MetalBindings.SelRelease);
            }
        }

        public void Dispose()
        {
            foreach (var kvp in _buffers)
            {
                if (kvp.Value != nint.Zero)
                {
                    MetalBindings.objc_msgSend_void(kvp.Value, MetalBindings.SelRelease);
                }
            }
            _buffers.Clear();
            GC.SuppressFinalize(this);
        }
    }
}
