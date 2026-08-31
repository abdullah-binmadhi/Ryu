using Ryujinx.Common.Logging;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Headless.UI
{
    [SupportedOSPlatform("macos")]
    public static partial class InGameOverlay
    {
        private const string ObjCRuntime = "/usr/lib/libobjc.A.dylib";
        private const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [LibraryImport(ObjCRuntime, StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr sel_registerName(string name);

        [LibraryImport(ObjCRuntime, StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr objc_getClass(string name);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        private static partial IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        private static partial IntPtr objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        private static partial void objc_msgSend_void_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        private static partial void objc_msgSend_void_bool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.Bool)] bool arg1);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        private static partial void objc_msgSend_void_long(IntPtr receiver, IntPtr selector, long arg1);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        private static partial void objc_msgSend_setFrame(IntPtr receiver, IntPtr selector, double x, double y, double w, double h);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        private static partial IntPtr objc_msgSend_color(IntPtr receiver, IntPtr selector, double r, double g, double b, double a);

        [LibraryImport(CoreFoundationLib, StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr CFStringCreateWithCString(IntPtr alloc, string str, uint encoding);

        [LibraryImport(CoreFoundationLib)]
        private static partial void CFRelease(IntPtr cf);

        private const uint kCFStringEncodingUTF8 = 0x08000100;

        private static IntPtr _targetWindow = IntPtr.Zero;
        private static IntPtr _hudLabel = IntPtr.Zero;
        private static bool _isVisible = true;
        private static bool _initialized = false;
        private static readonly object _lock = new();

        public static bool IsVisible
        {
            get => _isVisible;
            set
            {
                _isVisible = value;
                if (_hudLabel != IntPtr.Zero)
                {
                    objc_msgSend_void_bool(_hudLabel, sel_registerName("setHidden:"), !_isVisible);
                }
            }
        }

        public static void Initialize()
        {
            if (!OperatingSystem.IsMacOS() || _initialized)
            {
                return;
            }

            lock (_lock)
            {
                if (_initialized && _hudLabel != IntPtr.Zero)
                {
                    return;
                }

                try
                {
                    IntPtr nsAppClass = objc_getClass("NSApplication");
                    if (nsAppClass == IntPtr.Zero) return;

                    IntPtr nsApp = objc_msgSend(nsAppClass, sel_registerName("sharedApplication"));
                    if (nsApp == IntPtr.Zero) return;

                    IntPtr windows = objc_msgSend(nsApp, sel_registerName("windows"));
                    if (windows == IntPtr.Zero) return;

                    IntPtr count = objc_msgSend(windows, sel_registerName("count"));
                    if ((long)count == 0) return;

                    _targetWindow = objc_msgSend_IntPtr(windows, sel_registerName("objectAtIndex:"), IntPtr.Zero);
                    if (_targetWindow == IntPtr.Zero) return;

                    IntPtr contentView = objc_msgSend(_targetWindow, sel_registerName("contentView"));
                    if (contentView == IntPtr.Zero) return;

                    IntPtr nsTextFieldClass = objc_getClass("NSTextField");
                    IntPtr nsFontClass = objc_getClass("NSFont");
                    IntPtr nsColorClass = objc_getClass("NSColor");

                    IntPtr initialStr = CFStringCreateWithCString(IntPtr.Zero, "FPS: 60.0  (16.6ms)  |  1% Low: 58.5", kCFStringEncodingUTF8);
                    _hudLabel = objc_msgSend_IntPtr(nsTextFieldClass, sel_registerName("labelWithString:"), initialStr);
                    if (initialStr != IntPtr.Zero)
                    {
                        CFRelease(initialStr);
                    }

                    if (_hudLabel != IntPtr.Zero)
                    {
                        // Position in top-left of content view
                        // In macOS Cocoa: y: 0 is bottom. Autoresizing mask 8 = NSViewMinYMargin (sticks to top)
                        objc_msgSend_setFrame(_hudLabel, sel_registerName("setFrame:"), 18, 18, 550, 28);
                        objc_msgSend_void_long(_hudLabel, sel_registerName("setAutoresizingMask:"), 8 | 1); // Sticks to top & left

                        // Lime Green (#00FF59)
                        IntPtr colorWithAlphaSel = sel_registerName("colorWithCalibratedRed:green:blue:alpha:");
                        IntPtr textColor = objc_msgSend_color(nsColorClass, colorWithAlphaSel, 0.0, 1.0, 0.35, 1.0);
                        if (textColor != IntPtr.Zero)
                        {
                            objc_msgSend_void_IntPtr(_hudLabel, sel_registerName("setTextColor:"), textColor);
                        }

                        // Bold Font (14pt)
                        IntPtr boldFont = objc_msgSend_IntPtr(nsFontClass, sel_registerName("boldSystemFontOfSize:"), (IntPtr)14);
                        if (boldFont != IntPtr.Zero)
                        {
                            objc_msgSend_void_IntPtr(_hudLabel, sel_registerName("setFont:"), boldFont);
                        }

                        // Transparent & non-interactive
                        objc_msgSend_void_bool(_hudLabel, sel_registerName("setDrawsBackground:"), false);
                        objc_msgSend_void_bool(_hudLabel, sel_registerName("setBezeled:"), false);
                        objc_msgSend_void_bool(_hudLabel, sel_registerName("setSelectable:"), false);
                        objc_msgSend_void_bool(_hudLabel, sel_registerName("setHidden:"), !_isVisible);

                        // Add directly into game window content view
                        objc_msgSend_void_IntPtr(contentView, sel_registerName("addSubview:"), _hudLabel);
                    }

                    _initialized = true;
                    Logger.Info?.Print(LogClass.Application, "Native In-Game OSD HUD Overlay attached to game window view.");
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Application, $"In-Game OSD init notice: {ex.Message}");
                }
            }
        }

        public static void UpdateOverlay(double fps, double frameTimeMs, double onePercentLow, string filterStr)
        {
            if (!OperatingSystem.IsMacOS())
            {
                return;
            }

            if (!_initialized || _hudLabel == IntPtr.Zero)
            {
                Initialize();
            }

            if (_hudLabel == IntPtr.Zero)
            {
                return;
            }

            if (!_isVisible)
            {
                objc_msgSend_void_bool(_hudLabel, sel_registerName("setHidden:"), true);
                return;
            }

            try
            {
                string text = $"FPS: {fps,5:F1} ({frameTimeMs,4:F1}ms)  |  1% Low: {onePercentLow,4:F1}";

                IntPtr cfString = CFStringCreateWithCString(IntPtr.Zero, text, kCFStringEncodingUTF8);
                if (cfString != IntPtr.Zero)
                {
                    objc_msgSend_void_IntPtr(_hudLabel, sel_registerName("setStringValue:"), cfString);
                    CFRelease(cfString);
                }

                objc_msgSend_void_bool(_hudLabel, sel_registerName("setHidden:"), false);
            }
            catch
            {
            }
        }
    }
}
