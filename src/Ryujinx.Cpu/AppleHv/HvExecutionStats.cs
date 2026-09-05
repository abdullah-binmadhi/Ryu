using System;
using System.Threading;

namespace Ryujinx.Cpu.AppleHv
{
    /// <summary>
    /// Lightweight global counters for AppleHv guest execution telemetry.
    /// Used by the HUD / CSV benchmark log to determine whether the guest
    /// vCPUs are saturated (compute or exit bound) or starved (waiting on HLE).
    /// </summary>
    public static class HvExecutionStats
    {
        private static long _activeVcpus;
        private static long _guestRunTicks; // Stopwatch ticks spent inside hv_vcpu_run
        private static long _exitCount;

        public static int ActiveVcpuCount => (int)Volatile.Read(ref _activeVcpus);

        public static void OnVcpuCreated() => Interlocked.Increment(ref _activeVcpus);

        public static void OnVcpuDestroyed() => Interlocked.Decrement(ref _activeVcpus);

        public static void RecordRun(long ticks)
        {
            Interlocked.Add(ref _guestRunTicks, ticks);
            Interlocked.Increment(ref _exitCount);
        }

        /// <summary>
        /// Returns total guest-run ticks and exit count since the previous call, and resets them.
        /// Sampling this at ~1 Hz makes lost-update races between the exchange and concurrent
        /// adds statistically negligible.
        /// </summary>
        public static (long GuestRunTicks, long ExitCount, int Vcpus) GetAndReset()
        {
            long guestRunTicks = Interlocked.Exchange(ref _guestRunTicks, 0);
            long exitCount = Interlocked.Exchange(ref _exitCount, 0);
            return (guestRunTicks, exitCount, ActiveVcpuCount);
        }
    }
}
