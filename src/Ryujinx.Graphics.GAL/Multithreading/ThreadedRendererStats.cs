using System;
using System.Threading;

namespace Ryujinx.Graphics.GAL.Multithreading
{
    /// <summary>
    /// Global counters for ThreadedRenderer blocking operations.
    /// These waits happen on the GPU main (FIFO) thread and directly extend
    /// frame dispatch time when the backend (e.g. MoltenVK on macOS) is slow
    /// to execute queued commands or resolve syncs.
    /// </summary>
    public static class ThreadedRendererStats
    {
        private static long _syncWaitTicks;
        private static long _syncWaitCount;
        private static long _invokeWaitTicks;
        private static long _invokeWaitCount;
        private static long _frameWaitTicks;
        private static long _queueFullSleeps;

        private static readonly long[] _commandCounts = new long[256];

        public static void RecordCommand(byte commandType) => Interlocked.Increment(ref _commandCounts[commandType]);

        public static void RecordSyncWait(long ticks)
        {
            Interlocked.Add(ref _syncWaitTicks, ticks);
            Interlocked.Increment(ref _syncWaitCount);
        }

        public static void RecordInvokeWait(long ticks)
        {
            Interlocked.Add(ref _invokeWaitTicks, ticks);
            Interlocked.Increment(ref _invokeWaitCount);
        }

        public static void RecordFrameWait(long ticks)
        {
            Interlocked.Add(ref _frameWaitTicks, ticks);
        }

        public static void RecordQueueFullSleep() => Interlocked.Increment(ref _queueFullSleeps);

        public static (long SyncTicks, long SyncCount, long InvokeTicks, long InvokeCount, long FrameTicks, long QueueFullSleeps) GetAndReset()
        {
            return (
                Interlocked.Exchange(ref _syncWaitTicks, 0),
                Interlocked.Exchange(ref _syncWaitCount, 0),
                Interlocked.Exchange(ref _invokeWaitTicks, 0),
                Interlocked.Exchange(ref _invokeWaitCount, 0),
                Interlocked.Exchange(ref _frameWaitTicks, 0),
                Interlocked.Exchange(ref _queueFullSleeps, 0));
        }

        /// <summary>
        /// Returns per-command-type execution counts since the previous call and resets them.
        /// The index is the <see cref="CommandType"/> enum value.
        /// </summary>
        public static long[] GetAndResetCommandCounts()
        {
            long[] copy = new long[_commandCounts.Length];

            for (int i = 0; i < _commandCounts.Length; i++)
            {
                copy[i] = Interlocked.Exchange(ref _commandCounts[i], 0);
            }

            return copy;
        }
    }
}
