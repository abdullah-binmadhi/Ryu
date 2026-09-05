using Ryujinx.Cpu.AppleHv;
using Ryujinx.Graphics.GAL.Multithreading;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace Ryujinx.Headless.UI
{
    public static partial class TerminalHud
    {
        private static bool _isEnabled;
        private static Thread _hudThread;
        private static double _currentFps;
        private static double _onePercentLow;
        private static double _frameTimeMs;
        private static double _fifoPercent;
        private static string _gameTitle = "Game";
        private static readonly Stopwatch _uptime = new();

        // CPU telemetry (computed per HUD tick, ~2 Hz).
        private static double _hostCpuPercent;
        private static double _guestVcpuBusyPercent;
        private static double _exitsPerSecond;
        private static long _lastSampleTicks;
        private static TimeSpan _lastCpuTime;

        // GPU wait telemetry (ThreadedRenderer blocking waits, ms/s and counts/s).
        private static double _gpuWaitMsPerSec;
        private static double _gpuSyncWaitsPerSec;
        private static double _gpuQueueFullSleepsPerSec;
        private static double _commandsPerSec;
        private static string _topCommands = string.Empty;

        // ProcessFrame component timing averages (per HUD tick).
        private static double _shaderCacheMs;
        private static double _preFrameMs;
        private static double _dispatchMs;
        private static double _shaderCacheSum;
        private static double _preFrameSum;
        private static double _dispatchSum;
        private static int _frameTimingSamples;

        // Optional per-second CSV benchmark log (enabled via --fps-log).
        private static bool _csvLogEnabled;
        private static string _csvLogPath;

        public static bool CsvLogEnabled
        {
            get => _csvLogEnabled;
            set => _csvLogEnabled = value;
        }

        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr objc_getClass(string className);

        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr sel_registerName(string selectorName);

        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static partial IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static partial long objc_msgSend_long(IntPtr receiver, IntPtr selector);

        public static void Start(string title = "Nintendo Switch Game")
        {
            _gameTitle = title;
            _isEnabled = true;
            _uptime.Start();

            if (_csvLogEnabled)
            {
                try
                {
                    string logDir = Ryujinx.Common.Configuration.AppDataManager.LogsDirPath;
                    Directory.CreateDirectory(logDir);
                    _csvLogPath = Path.Combine(logDir, "fps_log.csv");
                    bool isNewFile = !File.Exists(_csvLogPath);
                    using StreamWriter writer = new(_csvLogPath, append: true);
                    if (isNewFile)
                    {
                        writer.WriteLine("unix_time_s,uptime_s,fps,frame_time_ms,one_percent_low_fps,fifo_percent,host_cpu_pct,guest_vcpu_busy_pct,exits_per_sec,shader_cache_ms,preframe_ms,dispatch_ms,gpu_wait_ms_s,gpu_sync_waits_s,gpu_queue_full_sleeps_s,commands_per_sec,top_commands");
                    }
                }
                catch
                {
                    _csvLogEnabled = false;
                }
            }

            _hudThread = new Thread(HudLoop)
            {
                Name = "Ryu.TerminalHud",
                IsBackground = true,
            };

            _hudThread.Start();
        }

        public static void Stop()
        {
            _isEnabled = false;
            _uptime.Stop();
        }

        public static double CurrentFps => _currentFps;
        public static double FrameTimeMs => _frameTimeMs;
        public static double OnePercentLow => _onePercentLow;

        public static void UpdateMetrics(double fps, double frameTimeMs, double onePercentLow = 0, double fifoPercent = 0)
        {
            _currentFps = fps;
            _frameTimeMs = frameTimeMs;
            _fifoPercent = fifoPercent;

            // Real sliding-window percentile fed by the render loop; keep last value while warming up.
            if (onePercentLow > 0)
            {
                _onePercentLow = onePercentLow;
            }
        }

        public static void AddFrameTimings(double shaderCacheMs, double preFrameMs, double dispatchMs)
        {
            _shaderCacheSum += shaderCacheMs;
            _preFrameSum += preFrameMs;
            _dispatchSum += dispatchMs;
            _frameTimingSamples++;
        }

        private static string GetDarwinThermalState()
        {
            if (!OperatingSystem.IsMacOS())
            {
                return "Nominal";
            }

            try
            {
                IntPtr nsProcessInfoClass = objc_getClass("NSProcessInfo");
                if (nsProcessInfoClass == IntPtr.Zero) return "Nominal";

                IntPtr processInfoSel = sel_registerName("processInfo");
                IntPtr processInfo = objc_msgSend(nsProcessInfoClass, processInfoSel);
                if (processInfo == IntPtr.Zero) return "Nominal";

                IntPtr thermalStateSel = sel_registerName("thermalState");
                long state = objc_msgSend_long(processInfo, thermalStateSel);

                return state switch
                {
                    0 => "\u001b[32mNominal\u001b[0m",
                    1 => "\u001b[33mFair\u001b[0m",
                    2 => "\u001b[31mHeavy (Throttling Risk)\u001b[0m",
                    3 => "\u001b[35mCritical\u001b[0m",
                    _ => "Unknown",
                };
            }
            catch
            {
                return "Nominal";
            }
        }

        private static void HudLoop()
        {
            if (OperatingSystem.IsMacOS())
            {
                Common.SystemInterop.DarwinThreadScheduler.SetBackgroundQoS();
            }

            int iteration = 0;

            while (_isEnabled)
            {
                try
                {
                    UpdateCpuTelemetry();
                    AverageFrameTimings();

                    long memoryBytes = Process.GetCurrentProcess().WorkingSet64;
                    double memoryMb = memoryBytes / (1024.0 * 1024.0);
                    string thermal = GetDarwinThermalState();

                    // Print formatted in-place HUD line
                    Console.Write($"\r\u001b[2K\u001b[1;36m[Ryu]\u001b[0m FPS: \u001b[1;32m{_currentFps,5:F1}\u001b[0m ({_frameTimeMs,4:F1}ms) | 1% Low: \u001b[1;33m{_onePercentLow,5:F1}\u001b[0m | FIFO: \u001b[1;35m{_fifoPercent:00.0}%\u001b[0m | Disp:{_dispatchMs,5:F1}ms | GPUwait:{_gpuWaitMsPerSec,4:F0}ms/s | Cmd:{_commandsPerSec / 1000.0,4:F1}k/s | CPU: \u001b[1;36m{_hostCpuPercent,5:F0}%\u001b[0m | vCPU: \u001b[1;36m{_guestVcpuBusyPercent,3:F0}%\u001b[0m ({_exitsPerSecond / 1000.0,4:F1}k/s) | RAM: \u001b[1;34m{memoryMb:F0} MB\u001b[0m | Thermal: {thermal} | Uptime: {_uptime.Elapsed:mm\\:ss}");

                    // Write one CSV row every second (loop ticks every 500 ms).
                    if (_csvLogEnabled && (iteration++ & 1) == 0)
                    {
                        AppendCsvRow();
                    }
                }
                catch
                {
                }

                Thread.Sleep(500);
            }
        }

        private static void AverageFrameTimings()
        {
            int samples = _frameTimingSamples;

            if (samples > 0)
            {
                _shaderCacheMs = _shaderCacheSum / samples;
                _preFrameMs = _preFrameSum / samples;
                _dispatchMs = _dispatchSum / samples;

                _shaderCacheSum = 0;
                _preFrameSum = 0;
                _dispatchSum = 0;
                _frameTimingSamples = 0;
            }
        }

        private static void UpdateCpuTelemetry()
        {
            long nowTicks = Stopwatch.GetTimestamp();
            TimeSpan nowCpu = Process.GetCurrentProcess().TotalProcessorTime;

            if (_lastSampleTicks > 0)
            {
                double wallSeconds = (nowTicks - _lastSampleTicks) / (double)Stopwatch.Frequency;

                if (wallSeconds > 0)
                {
                    _hostCpuPercent = (nowCpu - _lastCpuTime).TotalSeconds / wallSeconds * 100.0;

                    if (OperatingSystem.IsMacOS())
                    {
                        (long guestRunTicks, long exitCount, int vcpus) = HvExecutionStats.GetAndReset();

                        if (vcpus > 0)
                        {
                            double guestSeconds = guestRunTicks / (double)Stopwatch.Frequency;
                            _guestVcpuBusyPercent = guestSeconds / (wallSeconds * vcpus) * 100.0;
                        }

                        _exitsPerSecond = exitCount / wallSeconds;
                    }

                    (long syncTicks, long syncCount, long invokeTicks, long invokeCount, long frameTicks, long queueFullSleeps) = ThreadedRendererStats.GetAndReset();

                    double gpuWaitSeconds = (syncTicks + invokeTicks + frameTicks) / (double)Stopwatch.Frequency;
                    _gpuWaitMsPerSec = gpuWaitSeconds / wallSeconds * 1000.0;
                    _gpuSyncWaitsPerSec = (syncCount + invokeCount) / wallSeconds;
                    _gpuQueueFullSleepsPerSec = queueFullSleeps / wallSeconds;

                    long[] commandCounts = ThreadedRendererStats.GetAndResetCommandCounts();
                    long totalCommands = 0;

                    int[] topIndices = new int[5];

                    for (int i = 0; i < commandCounts.Length; i++)
                    {
                        totalCommands += commandCounts[i];

                        for (int rank = 0; rank < topIndices.Length; rank++)
                        {
                            if (commandCounts[i] > commandCounts[topIndices[rank]])
                            {
                                for (int shift = topIndices.Length - 1; shift > rank; shift--)
                                {
                                    topIndices[shift] = topIndices[shift - 1];
                                }

                                topIndices[rank] = i;
                                break;
                            }
                        }
                    }

                    _commandsPerSec = totalCommands / wallSeconds;
                    _topCommands = string.Join(';', topIndices.Select(i => $"{(CommandType)i}={commandCounts[i]}"));
                }
            }

            _lastSampleTicks = nowTicks;
            _lastCpuTime = nowCpu;
        }

        private static void AppendCsvRow()
        {
            try
            {
                string line = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:F3},{1:F1},{2:F2},{3:F2},{4:F2},{5:F2},{6:F1},{7:F1},{8:F0},{9:F2},{10:F2},{11:F2},{12:F2},{13:F1},{14:F1},{15:F0},\"{16}\"",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                    _uptime.Elapsed.TotalSeconds,
                    _currentFps,
                    _frameTimeMs,
                    _onePercentLow,
                    _fifoPercent,
                    _hostCpuPercent,
                    _guestVcpuBusyPercent,
                    _exitsPerSecond,
                    _shaderCacheMs,
                    _preFrameMs,
                    _dispatchMs,
                    _gpuWaitMsPerSec,
                    _gpuSyncWaitsPerSec,
                    _gpuQueueFullSleepsPerSec,
                    _commandsPerSec,
                    _topCommands.Replace('\"', '\''));

                File.AppendAllText(_csvLogPath, line + Environment.NewLine);
            }
            catch
            {
                _csvLogEnabled = false;
            }
        }
    }
}
