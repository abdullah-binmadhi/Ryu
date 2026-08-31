using Ryujinx.Common.Logging;
using System;
using System.Diagnostics;
using System.Linq;

namespace Ryujinx.Graphics.Vulkan
{
    internal class AutoFlushCounter
    {
        // How often to flush on framebuffer change.
        private readonly static long _framebufferFlushTimer = Stopwatch.Frequency / 1000; // (1ms)

        // How often to flush on draw when fast flush mode is enabled.
        private readonly static long _drawFlushTimer = Stopwatch.Frequency / 666; // (1.5ms)
        private readonly static long _drawFlushTimerMoltenVk = Stopwatch.Frequency / 120; // (8.3ms for Apple Silicon TBDR)

        // Average wait time that triggers fast flush mode to be entered.
        private readonly static long _fastFlushEnterThreshold = Stopwatch.Frequency / 666; // (1.5ms)
        private readonly static long _fastFlushEnterThresholdMoltenVk = Stopwatch.Frequency / 120; // (8.3ms)

        // Average wait time that triggers fast flush mode to be exited.
        private readonly static long _fastFlushExitThreshold = Stopwatch.Frequency / 10000; // (0.1ms)

        // Number of frames to average waiting times over.
        private const int SyncWaitAverageCount = 20;

        private const int MinDrawCountForFlush = 10;
        private const int MinDrawCountForFlushMoltenVk = 64;
        private const int MinConsecutiveQueryForFlush = 10;
        private const int InitialQueryCountForFlush = 32;

        private readonly VulkanRenderer _gd;

        private long _lastFlush;
        private ulong _lastDrawCount;
        private bool _hasPendingQuery;
        private int _consecutiveQueries;
        private int _queryCount;

        private readonly int[] _queryCountHistory = new int[3];
        private int _queryCountHistoryIndex;
        private int _remainingQueries;

        private readonly long[] _syncWaitHistory = new long[SyncWaitAverageCount];
        private int _syncWaitHistoryIndex;

        private bool _fastFlushMode;

        public AutoFlushCounter(VulkanRenderer gd)
        {
            _gd = gd;
        }

        public void RegisterFlush(ulong drawCount)
        {
            _lastFlush = Stopwatch.GetTimestamp();
            _lastDrawCount = drawCount;

            _hasPendingQuery = false;
            _consecutiveQueries = 0;
        }

        public bool RegisterPendingQuery()
        {
            _hasPendingQuery = true;
            _consecutiveQueries++;
            _remainingQueries--;

            _queryCountHistory[_queryCountHistoryIndex]++;

            // Interrupt render passes to flush queries, so that early results arrive sooner.
            if (++_queryCount == InitialQueryCountForFlush)
            {
                return true;
            }

            return false;
        }

        public int GetRemainingQueries()
        {
            if (_remainingQueries <= 0)
            {
                _remainingQueries = 16;
            }

            if (_queryCount < InitialQueryCountForFlush)
            {
                return Math.Min(InitialQueryCountForFlush - _queryCount, _remainingQueries);
            }

            return _remainingQueries;
        }

        public bool ShouldFlushQuery()
        {
            return _hasPendingQuery;
        }

        public bool ShouldFlushDraw(ulong drawCount)
        {
            if (_fastFlushMode)
            {
                long minDraws = _gd.IsMoltenVk ? MinDrawCountForFlushMoltenVk : MinDrawCountForFlush;
                long draws = (long)(drawCount - _lastDrawCount);

                if (draws < minDraws)
                {
                    if (draws == 0)
                    {
                        _lastFlush = Stopwatch.GetTimestamp();
                    }

                    return false;
                }

                long flushTimeout = _gd.IsMoltenVk ? _drawFlushTimerMoltenVk : _drawFlushTimer;

                long now = Stopwatch.GetTimestamp();

                return now > _lastFlush + flushTimeout;
            }

            return false;
        }

        public bool ShouldFlushAttachmentChange(ulong drawCount)
        {
            _queryCount = 0;

            // Flush when there's an attachment change out of a large block of queries.
            if (_consecutiveQueries > MinConsecutiveQueryForFlush)
            {
                return true;
            }

            _consecutiveQueries = 0;

            long minDraws = _gd.IsMoltenVk ? MinDrawCountForFlushMoltenVk : MinDrawCountForFlush;
            long draws = (long)(drawCount - _lastDrawCount);

            if (draws < minDraws)
            {
                if (draws == 0)
                {
                    _lastFlush = Stopwatch.GetTimestamp();
                }

                return false;
            }

            long flushTimeout = _framebufferFlushTimer;

            long now = Stopwatch.GetTimestamp();

            return now > _lastFlush + flushTimeout;
        }

        public void Present()
        {
            // Query flush prediction.

            _queryCountHistoryIndex = (_queryCountHistoryIndex + 1) % 3;

            _remainingQueries = _queryCountHistory.Max() + 10;

            _queryCountHistory[_queryCountHistoryIndex] = 0;

            // Fast flush mode toggle.

            _syncWaitHistory[_syncWaitHistoryIndex] = _gd.SyncManager.GetAndResetWaitTicks();

            _syncWaitHistoryIndex = (_syncWaitHistoryIndex + 1) % SyncWaitAverageCount;

            long averageWait = (long)_syncWaitHistory.Average();

            long enterThreshold = _gd.IsMoltenVk ? _fastFlushEnterThresholdMoltenVk : _fastFlushEnterThreshold;

            if (_fastFlushMode ? averageWait < _fastFlushExitThreshold : averageWait > enterThreshold)
            {
                _fastFlushMode = !_fastFlushMode;
                Logger.Debug?.PrintMsg(LogClass.Gpu, $"Switched fast flush mode: ({_fastFlushMode})");
            }
        }
    }
}
