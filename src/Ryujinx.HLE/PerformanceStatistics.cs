using Ryujinx.Common;
using System;
using System.Threading;
using System.Timers;

namespace Ryujinx.HLE
{
    public class PerformanceStatistics
    {
        private readonly Switch _device;

        private const int FrameTypeGame = 0;
        private const int PercentTypeFifo = 0;

        private readonly double[] _frameRate;
        private readonly double[] _accumulatedFrameTime;
        private readonly double[] _previousFrameTime;

        // Sliding-window frame time history for percentile (1% low) statistics.
        // Sized to hold ~10 seconds of frames at 30 FPS with zero per-frame allocations.
        private const int FrameTimeHistorySize = 300;
        private readonly double[] _frameTimeHistory = new double[FrameTimeHistorySize];
        private readonly double[] _frameTimeScratch = new double[FrameTimeHistorySize];
        private int _frameTimeCount;
        private int _frameTimeIndex;

        private readonly double[] _averagePercent;
        private readonly double[] _accumulatedActiveTime;
        private readonly double[] _percentLastEndTime;
        private readonly double[] _percentStartTime;

        private readonly long[] _framesRendered;
        private readonly double[] _percentTime;

        private readonly Lock[] _frameLock = [new()];
        private readonly Lock[] _percentLock = [new()];

        // Per-frame timing breakdown of Switch.ProcessFrame components (latest values, ticks).
        private long _lastShaderCacheTicks;
        private long _lastPreFrameTicks;
        private long _lastDispatchTicks;

        private readonly double _ticksToSeconds;

        private readonly System.Timers.Timer _resetTimer;

        public PerformanceStatistics(Switch device)
        {
            _device = device;

            _frameRate = new double[1];
            _accumulatedFrameTime = new double[1];
            _previousFrameTime = new double[1];

            _averagePercent = new double[1];
            _accumulatedActiveTime = new double[1];
            _percentLastEndTime = new double[1];
            _percentStartTime = new double[1];

            _framesRendered = new long[1];
            _percentTime = new double[1];

            _resetTimer = new(750);

            _resetTimer.Elapsed += ResetTimerElapsed;
            _resetTimer.AutoReset = true;

            _resetTimer.Start();

            _ticksToSeconds = 1.0 / PerformanceCounter.TicksPerSecond;
        }

        private void ResetTimerElapsed(object sender, ElapsedEventArgs e)
        {
            CalculateFrameRate(FrameTypeGame);
            CalculateAveragePercent(PercentTypeFifo);
        }

        private void CalculateFrameRate(int frameType)
        {
            double frameRate = 0;

            lock (_frameLock[frameType])
            {
                if (_accumulatedFrameTime[frameType] > 0)
                {
                    frameRate = _framesRendered[frameType] / _accumulatedFrameTime[frameType];
                }

                _frameRate[frameType] = frameRate;
                _framesRendered[frameType] = 0;
                _accumulatedFrameTime[frameType] = 0;
            }
        }

        private void CalculateAveragePercent(int percentType)
        {
            // If start time is non-zero, a percent reading is still being measured.
            // If there aren't any readings, the default should be 100% if still being measured, or 0% if not.
            double percent = (_percentStartTime[percentType] == 0) ? 0 : 100;

            lock (_percentLock[percentType])
            {
                if (_percentTime[percentType] > 0)
                {
                    percent = (_accumulatedActiveTime[percentType] / _percentTime[percentType]) * 100;
                }

                _averagePercent[percentType] = percent;
                _percentTime[percentType] = 0;
                _accumulatedActiveTime[percentType] = 0;
            }
        }

        public void RecordGameFrameTime()
        {
            RecordFrameTime(FrameTypeGame);
        }

        public void RecordFifoStart()
        {
            StartPercentTime(PercentTypeFifo);
        }

        public void RecordFifoEnd()
        {
            EndPercentTime(PercentTypeFifo);
        }

        private void StartPercentTime(int percentType)
        {
            double currentTime = PerformanceCounter.ElapsedTicks * _ticksToSeconds;

            _percentStartTime[percentType] = currentTime;
        }

        private void EndPercentTime(int percentType)
        {
            double currentTime = PerformanceCounter.ElapsedTicks * _ticksToSeconds;
            double elapsedTime = currentTime - _percentLastEndTime[percentType];
            double elapsedActiveTime = currentTime - _percentStartTime[percentType];

            lock (_percentLock[percentType])
            {
                _accumulatedActiveTime[percentType] += elapsedActiveTime;
                _percentTime[percentType] += elapsedTime;
            }

            _percentLastEndTime[percentType] = currentTime;
            _percentStartTime[percentType] = 0;
        }

        private void RecordFrameTime(int frameType)
        {
            double currentFrameTime = PerformanceCounter.ElapsedTicks * _ticksToSeconds;
            double previousFrameTime = _previousFrameTime[frameType];
            double elapsedFrameTime = currentFrameTime - previousFrameTime;

            _previousFrameTime[frameType] = currentFrameTime;

            // The first frame has no valid delta (previous time is zero): elapsed is
            // time since process start and would pollute the average frame time (and
            // thus the reported FPS frame-time). Skip accumulation until a real frame
            // boundary exists.
            if (previousFrameTime == 0)
            {
                return;
            }

            lock (_frameLock[frameType])
            {
                if (elapsedFrameTime > 0)
                {
                    _frameTimeHistory[_frameTimeIndex] = elapsedFrameTime;
                    _frameTimeIndex = (_frameTimeIndex + 1) % FrameTimeHistorySize;

                    if (_frameTimeCount < FrameTimeHistorySize)
                    {
                        _frameTimeCount++;
                    }
                }

                _accumulatedFrameTime[frameType] += elapsedFrameTime;

                _framesRendered[frameType]++;
            }
        }

        public double GetGameFrameRate()
        {
            return _frameRate[FrameTypeGame];
        }

        public double GetFifoPercent()
        {
            return _averagePercent[PercentTypeFifo];
        }

        /// <summary>
        /// Records the Stopwatch tick breakdown of the last Switch.ProcessFrame call.
        /// </summary>
        public void RecordProcessFrameTimings(long shaderCacheTicks, long preFrameTicks, long dispatchTicks)
        {
            Volatile.Write(ref _lastShaderCacheTicks, shaderCacheTicks);
            Volatile.Write(ref _lastPreFrameTicks, preFrameTicks);
            Volatile.Write(ref _lastDispatchTicks, dispatchTicks);
        }

        /// <summary>
        /// Returns the last recorded ProcessFrame component timings in Stopwatch ticks.
        /// </summary>
        public (long ShaderCache, long PreFrame, long Dispatch) GetLastProcessFrameTimings()
        {
            return (Volatile.Read(ref _lastShaderCacheTicks), Volatile.Read(ref _lastPreFrameTicks), Volatile.Read(ref _lastDispatchTicks));
        }

        public double GetGameFrameTime()
        {
            double frameRate = _frameRate[FrameTypeGame];

            return frameRate <= 0 ? 0 : 1000 / frameRate;
        }

        /// <summary>
        /// Returns the frame rate of the slowest 1% of frames in the sliding history window.
        /// This is the standard "1% low" metric and is 0 until enough frames have been recorded.
        /// </summary>
        public double GetOnePercentLowFrameRate()
        {
            int frameType = FrameTypeGame;

            lock (_frameLock[frameType])
            {
                int count = _frameTimeCount;

                if (count < 30)
                {
                    return 0;
                }

                // Copy the ring buffer into scratch space in order, then sort ascending.
                Array.Copy(_frameTimeHistory, _frameTimeScratch, FrameTimeHistorySize);
                Array.Sort(_frameTimeScratch, 0, count);

                // 1% low = 99th percentile of frame times (slowest 1% of frames).
                int percentileIndex = Math.Min(count - 1, (int)(count * 0.99));

                double slowFrameTime = _frameTimeScratch[percentileIndex];

                return slowFrameTime > 0 ? 1.0 / slowFrameTime : 0;
            }
        }

        public string FormatFifoPercent()
        {
            double fifoPercent = GetFifoPercent();

            return $"FIFO: {fifoPercent:00.00}%";
        }
    }
}
