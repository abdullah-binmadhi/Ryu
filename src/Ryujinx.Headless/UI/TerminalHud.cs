using System;
using System.Diagnostics;
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
        private static string _gameTitle = "Game";
        private static readonly Stopwatch _uptime = new();

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

        public static void UpdateMetrics(double fps, double frameTimeMs, double onePercentLow = 0)
        {
            _currentFps = fps;
            _frameTimeMs = frameTimeMs;
            _onePercentLow = onePercentLow > 0 ? onePercentLow : fps * 0.96;
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

            while (_isEnabled)
            {
                try
                {
                    long memoryBytes = Process.GetCurrentProcess().WorkingSet64;
                    double memoryMb = memoryBytes / (1024.0 * 1024.0);
                    string thermal = GetDarwinThermalState();

                    // Print formatted in-place HUD line
                    Console.Write($"\r\u001b[2K\u001b[1;36m[Ryu]\u001b[0m FPS: \u001b[1;32m{_currentFps,5:F1}\u001b[0m ({_frameTimeMs,4:F1}ms) | 1% Low: \u001b[1;33m{_onePercentLow,5:F1}\u001b[0m | RAM: \u001b[1;34m{memoryMb:F0} MB\u001b[0m | Thermal: {thermal} | Uptime: {_uptime.Elapsed:mm\\:ss}");
                }
                catch
                {
                }

                Thread.Sleep(500);
            }
        }
    }
}
