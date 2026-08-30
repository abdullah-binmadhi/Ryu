using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Common.SystemInterop
{
    [SupportedOSPlatform("macos")]
    public static partial class DarwinThreadScheduler
    {
        // Darwin QoS classes (sys/qos.h)
        public const int QOS_CLASS_USER_INTERACTIVE = 0x21;
        public const int QOS_CLASS_USER_INITIATED   = 0x19;
        public const int QOS_CLASS_DEFAULT          = 0x15;
        public const int QOS_CLASS_UTILITY          = 0x11;
        public const int QOS_CLASS_BACKGROUND       = 0x09;
        public const int QOS_CLASS_UNSPECIFIED      = 0x00;

        [LibraryImport("libSystem.dylib", EntryPoint = "pthread_set_qos_class_self_np")]
        private static partial int pthread_set_qos_class_self_np(int qosClass, int relativePriority);

        /// <summary>
        /// Sets the Quality of Service (QoS) class for the current calling thread.
        /// </summary>
        /// <param name="qosClass">Darwin QoS class identifier.</param>
        /// <param name="relativePriority">Relative priority offset (usually 0, range -15 to 0).</param>
        /// <returns>True if successfully set, false otherwise.</returns>
        public static bool SetCurrentThreadQoS(int qosClass, int relativePriority = 0)
        {
            if (!OperatingSystem.IsMacOS())
            {
                return false;
            }

            try
            {
                return pthread_set_qos_class_self_np(qosClass, relativePriority) == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Locks the calling thread to Apple Silicon Performance Cores (P-Cores) at maximum IPC frequency.
        /// Ideal for Guest JIT threads, GPU render dispatch, and Audio DSP workers.
        /// </summary>
        public static bool SetInteractiveQoS()
        {
            return SetCurrentThreadQoS(QOS_CLASS_USER_INTERACTIVE);
        }

        /// <summary>
        /// Confines the calling thread to Apple Silicon Efficiency Cores (E-Cores) to prevent latency
        /// interference on emulation loops. Ideal for disk caches, shader translation, and telemetry.
        /// </summary>
        public static bool SetBackgroundQoS()
        {
            return SetCurrentThreadQoS(QOS_CLASS_BACKGROUND);
        }
    }
}
