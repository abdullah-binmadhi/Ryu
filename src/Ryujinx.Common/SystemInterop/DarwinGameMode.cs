using Ryujinx.Common.Logging;
using System;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Common.SystemInterop
{
    [SupportedOSPlatform("macos")]
    public static partial class DarwinGameMode
    {
        private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";
        private const string LibSystem = "/usr/lib/libSystem.B.dylib";
        private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [LibraryImport(ObjCLibrary, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr objc_getClass(string className);

        [LibraryImport(ObjCLibrary, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr sel_registerName(string selectorName);

        [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        private static partial IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

        [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        private static partial void objc_msgSend_ulong(IntPtr receiver, IntPtr selector, ulong arg);

        [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        private static partial IntPtr objc_msgSend_activity(IntPtr receiver, IntPtr selector, ulong options, IntPtr reason);

        [LibraryImport(LibSystem, EntryPoint = "pthread_set_qos_class_self_np")]
        private static partial int pthread_set_qos_class_self_np(int qosClass, int relativePriority);

        [LibraryImport(CoreFoundationLib, StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr CFStringCreateWithCString(IntPtr alloc, string str, uint encoding);

        [LibraryImport(CoreFoundationLib)]
        private static partial void CFRelease(IntPtr cf);

        // QOS_CLASS_USER_INTERACTIVE (0x21) = Performance Core Lock & Highest Scheduling Priority
        public const int QosClassUserInteractive = 0x21;

        // NSActivityOptions: LatencyCritical (0xFF00000000) | UserInitiated (0x00FFFFFF) | IdleSystemSleepAllowed (1 << 20)
        private const ulong NSActivityUserInitiatedAllowingIdleSystemSleep = 0x00FFFFFFUL | (1UL << 20);
        private const ulong NSActivityLatencyCritical = 0xFF00000000UL;

        private static IntPtr _activityToken = IntPtr.Zero;

        /// <summary>
        /// Applies all native Apple Silicon performance upgrades at once.
        /// </summary>
        public static void InitializePerformanceStack()
        {
            if (!OperatingSystem.IsMacOS())
            {
                return;
            }

            ConfigureMoltenVkEnvironment();
            ConfigureLowLatencyGc();
            TryBeginLatencyCriticalActivity();
            TrySetUserInteractiveQos();
            TryEnableGameMode();
        }

        /// <summary>
        /// Configures MoltenVK Metal command buffer prefilling and asynchronous submissions.
        /// </summary>
        public static void ConfigureMoltenVkEnvironment()
        {
            try
            {
                // Metal Command Buffer Prefilling (level 3 = aggressive parallel encoding)
                Environment.SetEnvironmentVariable("MVK_CONFIG_PREFILL_METAL_COMMAND_BUFFERS", "3");

                // Asynchronous Queue Submissions (decouples CPU draw submission from GPU queue execution)
                Environment.SetEnvironmentVariable("MVK_CONFIG_SYNCHRONOUS_QUEUE_SUBMITS", "0");

                // Automatic Metal device lost recovery
                Environment.SetEnvironmentVariable("MVK_CONFIG_RESUME_LOST_DEVICE", "1");
            }
            catch
            {
            }
        }

        /// <summary>
        /// Configures .NET 10 runtime GC for low-latency sustained gaming workloads.
        /// </summary>
        public static void ConfigureLowLatencyGc()
        {
            try
            {
                GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            }
            catch
            {
            }
        }

        /// <summary>
        /// Locks the calling thread (CPU JIT or GPU Render Loop) to Apple Silicon Performance Cores (3.5 GHz).
        /// </summary>
        public static bool TrySetUserInteractiveQos()
        {
            if (!OperatingSystem.IsMacOS())
            {
                return false;
            }

            try
            {
                int result = pthread_set_qos_class_self_np(QosClassUserInteractive, 0);
                return result == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Inhibits macOS App Nap and flags the emulator process as Latency Critical.
        /// </summary>
        public static bool TryBeginLatencyCriticalActivity()
        {
            if (!OperatingSystem.IsMacOS() || _activityToken != IntPtr.Zero)
            {
                return false;
            }

            try
            {
                IntPtr nsProcessInfoClass = objc_getClass("NSProcessInfo");
                if (nsProcessInfoClass == IntPtr.Zero) return false;

                IntPtr processInfoSel = sel_registerName("processInfo");
                IntPtr processInfo = objc_msgSend(nsProcessInfoClass, processInfoSel);
                if (processInfo == IntPtr.Zero) return false;

                IntPtr beginActivitySel = sel_registerName("beginActivityWithOptions:reason:");
                if (beginActivitySel == IntPtr.Zero) return false;

                IntPtr reason = CFStringCreateWithCString(IntPtr.Zero, "Ryu Bare-Metal Emulation Engine", 0x08000100);
                ulong options = NSActivityUserInitiatedAllowingIdleSystemSleep | NSActivityLatencyCritical;

                _activityToken = objc_msgSend_activity(processInfo, beginActivitySel, options, reason);

                if (reason != IntPtr.Zero)
                {
                    CFRelease(reason);
                }

                return _activityToken != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Requests macOS Game Mode hints for the application window.
        /// </summary>
        public static bool TryEnableGameMode()
        {
            if (!OperatingSystem.IsMacOS())
            {
                return false;
            }

            try
            {
                IntPtr nsAppClass = objc_getClass("NSApplication");
                if (nsAppClass == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr sharedAppSel = sel_registerName("sharedApplication");
                IntPtr nsApp = objc_msgSend(nsAppClass, sharedAppSel);
                if (nsApp == IntPtr.Zero)
                {
                    return false;
                }

                IntPtr setPresentationOptionsSel = sel_registerName("setPresentationOptions:");
                if (setPresentationOptionsSel != IntPtr.Zero)
                {
                    const ulong NSApplicationPresentationFullScreen = 1 << 10;
                    objc_msgSend_ulong(nsApp, setPresentationOptionsSel, NSApplicationPresentationFullScreen);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }
    }
}
