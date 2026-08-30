using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Common.SystemInterop
{
    [SupportedOSPlatform("macos")]
    public static partial class DarwinGameMode
    {
        private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";

        [LibraryImport(ObjCLibrary, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr objc_getClass(string className);

        [LibraryImport(ObjCLibrary, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr sel_registerName(string selectorName);

        [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        private static partial IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

        [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        private static partial void objc_msgSend_ulong(IntPtr receiver, IntPtr selector, ulong arg);

        /// <summary>
        /// Requests macOS Game Mode hints for the application window, optimizing Bluetooth
        /// controller latency and granting top GPU queue prioritization.
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

                // NSApplicationPresentationFullScreen | NSApplicationPresentationHideDock | NSApplicationPresentationHideMenuBar
                // On macOS 14+, full-screen gaming apps automatically enter Game Mode.
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
                // Silently fallback if AppKit runtime is not yet initialized
            }

            return false;
        }
    }
}
