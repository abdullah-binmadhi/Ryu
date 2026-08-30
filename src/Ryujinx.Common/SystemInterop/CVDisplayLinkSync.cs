using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Common.SystemInterop
{
    [SupportedOSPlatform("macos")]
    public sealed partial class CVDisplayLinkSync : IDisposable
    {
        private const string CoreVideoLibrary = "/System/Library/Frameworks/CoreVideo.framework/CoreVideo";

        [StructLayout(LayoutKind.Sequential)]
        public struct CVTime
        {
            public long TimeValue;
            public int TimeScale;
            public int Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CVSMPTETime
        {
            public short Subframes;
            public short SubframeDivisor;
            public uint Counter;
            public uint Type;
            public uint Flags;
            public short Hours;
            public short Minutes;
            public short Seconds;
            public short Frames;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CVTimeStamp
        {
            public uint Version;
            public int VideoTimeScale;
            public long VideoTime;
            public ulong HostTime;
            public double RateScalar;
            public long VideoRefreshPeriod;
            public CVSMPTETime SmpteTime;
            public ulong Flags;
            public ulong Reserved;
        }

        [LibraryImport(CoreVideoLibrary)]
        private static partial int CVDisplayLinkCreateWithActiveCGDisplays(out IntPtr displayLink);

        [LibraryImport(CoreVideoLibrary)]
        private static partial int CVDisplayLinkSetOutputCallback(IntPtr displayLink, IntPtr callback, IntPtr userInfo);

        [LibraryImport(CoreVideoLibrary)]
        private static partial int CVDisplayLinkStart(IntPtr displayLink);

        [LibraryImport(CoreVideoLibrary)]
        private static partial int CVDisplayLinkStop(IntPtr displayLink);

        [LibraryImport(CoreVideoLibrary)]
        private static partial void CVDisplayLinkRelease(IntPtr displayLink);

        [LibraryImport(CoreVideoLibrary)]
        private static partial CVTime CVDisplayLinkGetNominalOutputVideoRefreshPeriod(IntPtr displayLink);

        private IntPtr _displayLink;
        private readonly AutoResetEvent _vsyncSignal;
        private GCHandle _gcHandle;
        private bool _isRunning;
        private bool _disposed;
        private double _refreshRateHz = 60.0;
        private long _tickCounter;

        public double RefreshRateHz => _refreshRateHz;
        public bool IsRunning => _isRunning;

        public CVDisplayLinkSync()
        {
            _vsyncSignal = new AutoResetEvent(false);

            if (!OperatingSystem.IsMacOS())
            {
                return;
            }

            try
            {
                int result = CVDisplayLinkCreateWithActiveCGDisplays(out _displayLink);
                if (result != 0 || _displayLink == IntPtr.Zero)
                {
                    return;
                }

                _gcHandle = GCHandle.Alloc(this, GCHandleType.Normal);
                IntPtr contextPtr = GCHandle.ToIntPtr(_gcHandle);

                unsafe
                {
                    delegate* unmanaged[Cdecl]<IntPtr, CVTimeStamp*, CVTimeStamp*, ulong, ulong*, IntPtr, int> callbackPtr = &DisplayLinkCallback;
                    CVDisplayLinkSetOutputCallback(_displayLink, (IntPtr)callbackPtr, contextPtr);
                }

                UpdateRefreshRate();
            }
            catch
            {
                _displayLink = IntPtr.Zero;
            }
        }

        public void UpdateRefreshRate()
        {
            if (_displayLink != IntPtr.Zero)
            {
                CVTime period = CVDisplayLinkGetNominalOutputVideoRefreshPeriod(_displayLink);
                if (period.TimeValue > 0 && period.TimeScale > 0)
                {
                    _refreshRateHz = (double)period.TimeScale / period.TimeValue;
                }
            }
        }

        public int GetCadenceDivisor(int targetFps)
        {
            if (targetFps <= 0)
            {
                return 1;
            }

            double refresh = _refreshRateHz > 0 ? _refreshRateHz : 60.0;
            int divisor = (int)Math.Max(1, Math.Round(refresh / targetFps));
            return divisor;
        }

        public bool Start()
        {
            if (_displayLink == IntPtr.Zero || _isRunning)
            {
                return false;
            }

            int result = CVDisplayLinkStart(_displayLink);
            if (result == 0)
            {
                _isRunning = true;
                return true;
            }

            return false;
        }

        public void Stop()
        {
            if (_displayLink != IntPtr.Zero && _isRunning)
            {
                CVDisplayLinkStop(_displayLink);
                _isRunning = false;
            }
        }

        /// <summary>
        /// Suspends the calling thread until the next hardware display refresh tick (0% CPU spin-wait).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool WaitForVsync(int timeoutMs = 20)
        {
            return _vsyncSignal.WaitOne(timeoutMs);
        }

        /// <summary>
        /// Suspends the calling thread for a specific number of hardware display refresh ticks (ProMotion 120Hz cadence pacing).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool WaitForVsyncCadence(int cadence, int timeoutMs = 30)
        {
            if (cadence <= 1)
            {
                return _vsyncSignal.WaitOne(timeoutMs);
            }

            long startTicks = Interlocked.Read(ref _tickCounter);
            long targetTicks = startTicks + cadence;

            while (Interlocked.Read(ref _tickCounter) < targetTicks)
            {
                if (!_vsyncSignal.WaitOne(timeoutMs))
                {
                    return false;
                }
            }

            return true;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        private static unsafe int DisplayLinkCallback(
            IntPtr displayLink,
            CVTimeStamp* inNow,
            CVTimeStamp* inOutputTime,
            ulong flagsIn,
            ulong* flagsOut,
            IntPtr displayLinkContext)
        {
            if (displayLinkContext != IntPtr.Zero)
            {
                try
                {
                    GCHandle handle = GCHandle.FromIntPtr(displayLinkContext);
                    if (handle.IsAllocated && handle.Target is CVDisplayLinkSync sync)
                    {
                        Interlocked.Increment(ref sync._tickCounter);
                        sync._vsyncSignal.Set();
                    }
                }
                catch
                {
                    // Zero allocations and silence exceptions in unmanaged kernel callback
                }
            }

            return 0;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Stop();

            if (_displayLink != IntPtr.Zero)
            {
                CVDisplayLinkRelease(_displayLink);
                _displayLink = IntPtr.Zero;
            }

            if (_gcHandle.IsAllocated)
            {
                _gcHandle.Free();
            }

            _vsyncSignal.Dispose();
        }
    }
}
