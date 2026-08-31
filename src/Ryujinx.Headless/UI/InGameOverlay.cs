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
        private static partial void objc_msgSend_addChildWindow(IntPtr receiver, IntPtr selector, IntPtr childWindow, long ordered);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        private static partial IntPtr objc_msgSend_initWindow(IntPtr receiver, IntPtr selector, double x, double y, double w, double h, long styleMask, long backing, [MarshalAs(UnmanagedType.Bool)] bool defer);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        private static partial void objc_msgSend_setFrame(IntPtr receiver, IntPtr selector, double x, double y, double w, double h);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        private static partial IntPtr objc_msgSend_color(IntPtr receiver, IntPtr selector, double r, double g, double b, double a);

        [LibraryImport(CoreFoundationLib, StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr CFStringCreateWithCString(IntPtr alloc, string str, uint encoding);

        [LibraryImport(CoreFoundationLib)]
        private static partial void CFRelease(IntPtr cf);

        private const uint kCFStringEncodingUTF8 = 0x08000100;

        private static IntPtr _hudPanel = IntPtr.Zero;
        private static IntPtr _hudLabel = IntPtr.Zero;
        private static bool _isVisible = true;
        private static bool _initialized = false;
        private static bool _attachedToParent = false;
        private static readonly object _lock = new();

        public static bool IsVisible
        {
            get => _isVisible;
            set
            {
                _isVisible = value;
                if (_hudPanel != IntPtr.Zero)
                {
                    if (_isVisible)
                    {
                        objc_msgSend(_hudPanel, sel_registerName("orderFrontRegardless"));
                    }
                    else
                    {
                        objc_msgSend_void_IntPtr(_hudPanel, sel_registerName("orderOut:"), IntPtr.Zero);
                    }
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
                if (_initialized && _hudPanel != IntPtr.Zero)
                {
                    return;
                }

                try
                {
                    IntPtr nsAppClass = objc_getClass("NSApplication");
                    IntPtr nsApp = IntPtr.Zero;
                    if (nsAppClass != IntPtr.Zero)
                    {
                        nsApp = objc_msgSend(nsAppClass, sel_registerName("sharedApplication"));
                    }

                    IntPtr nsPanelClass = objc_getClass("NSPanel");
                    if (nsPanelClass == IntPtr.Zero)
                    {
                        nsPanelClass = objc_getClass("NSWindow");
                    }

                    IntPtr allocSel = sel_registerName("alloc");
                    IntPtr initSel = sel_registerName("initWithContentRect:styleMask:backing:defer:");
                    IntPtr panelAlloc = objc_msgSend(nsPanelClass, allocSel);

                    // Top-left HUD panel: x: 24, y: 720 (top of viewport), w: 500, h: 36
                    // styleMask: 0 (Borderless)
                    _hudPanel = objc_msgSend_initWindow(panelAlloc, initSel, 24, 720, 500, 36, 0, 2, false);

                    if (_hudPanel == IntPtr.Zero)
                    {
                        Logger.Warning?.Print(LogClass.Application, "Could not initialize native OSD panel.");
                        return;
                    }

                    // Pure transparent window - NO background bar
                    objc_msgSend_void_bool(_hudPanel, sel_registerName("setOpaque:"), false);
                    objc_msgSend_void_bool(_hudPanel, sel_registerName("setHasShadow:"), false);
                    objc_msgSend_void_bool(_hudPanel, sel_registerName("setIgnoresMouseEvents:"), true);
                    objc_msgSend_void_bool(_hudPanel, sel_registerName("setHidesOnDeactivate:"), false);

                    // Maximum Window Level (1000 = kCGScreenSaverWindowLevel / overlay above all spaces)
                    objc_msgSend_void_long(_hudPanel, sel_registerName("setLevel:"), 1000);

                    // FullScreenAuxiliary (256) | CanJoinAllSpaces (1) | Stationary (16) | IgnoresCycle (64) = 337
                    objc_msgSend_void_long(_hudPanel, sel_registerName("setCollectionBehavior:"), 337);

                    // Transparent background
                    IntPtr nsColorClass = objc_getClass("NSColor");
                    IntPtr clearColor = objc_msgSend(nsColorClass, sel_registerName("clearColor"));
                    objc_msgSend_void_IntPtr(_hudPanel, sel_registerName("setBackgroundColor:"), clearColor);

                    IntPtr contentView = objc_msgSend(_hudPanel, sel_registerName("contentView"));

                    // Create NSTextField via labelWithString
                    IntPtr nsTextFieldClass = objc_getClass("NSTextField");
                    IntPtr nsFontClass = objc_getClass("NSFont");

                    IntPtr initialStr = CFStringCreateWithCString(IntPtr.Zero, "FPS: 60.0  (16.6ms)  |  1% Low: 58.5", kCFStringEncodingUTF8);
                    _hudLabel = objc_msgSend_IntPtr(nsTextFieldClass, sel_registerName("labelWithString:"), initialStr);
                    if (initialStr != IntPtr.Zero)
                    {
                        CFRelease(initialStr);
                    }

                    if (_hudLabel != IntPtr.Zero)
                    {
                        objc_msgSend_setFrame(_hudLabel, sel_registerName("setFrame:"), 0, 0, 500, 36);

                        // Bright Lime Green (#00FF59)
                        IntPtr colorWithAlphaSel = sel_registerName("colorWithCalibratedRed:green:blue:alpha:");
                        IntPtr textColor = objc_msgSend_color(nsColorClass, colorWithAlphaSel, 0.0, 1.0, 0.35, 1.0);
                        if (textColor != IntPtr.Zero)
                        {
                            objc_msgSend_void_IntPtr(_hudLabel, sel_registerName("setTextColor:"), textColor);
                        }

                        // Bold Font (15pt)
                        IntPtr boldFont = objc_msgSend_IntPtr(nsFontClass, sel_registerName("boldSystemFontOfSize:"), (IntPtr)15);
                        if (boldFont != IntPtr.Zero)
                        {
                            objc_msgSend_void_IntPtr(_hudLabel, sel_registerName("setFont:"), boldFont);
                        }

                        // Transparent label background
                        objc_msgSend_void_bool(_hudLabel, sel_registerName("setDrawsBackground:"), false);
                        objc_msgSend_void_bool(_hudLabel, sel_registerName("setBezeled:"), false);
                        objc_msgSend_void_bool(_hudLabel, sel_registerName("setSelectable:"), false);

                        objc_msgSend_void_IntPtr(contentView, sel_registerName("addSubview:"), _hudLabel);
                    }

                    TryAttachToGameWindow();

                    if (_isVisible)
                    {
                        objc_msgSend(_hudPanel, sel_registerName("orderFrontRegardless"));
                    }

                    _initialized = true;
                    Logger.Info?.Print(LogClass.Application, "Native In-Game OSD HUD Overlay initialized successfully.");
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Application, $"Failed to initialize In-Game OSD: {ex.Message}");
                }
            }
        }

        private static void TryAttachToGameWindow()
        {
            if (_hudPanel == IntPtr.Zero) return;

            try
            {
                IntPtr nsApp = objc_msgSend(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                if (nsApp == IntPtr.Zero) return;

                IntPtr windows = objc_msgSend(nsApp, sel_registerName("windows"));
                if (windows == IntPtr.Zero) return;

                IntPtr count = objc_msgSend(windows, sel_registerName("count"));
                for (int i = 0; i < (long)count; i++)
                {
                    IntPtr win = objc_msgSend_IntPtr(windows, sel_registerName("objectAtIndex:"), (IntPtr)i);
                    if (win != IntPtr.Zero && win != _hudPanel)
                    {
                        // NSWindowAbove = 1
                        objc_msgSend_addChildWindow(win, sel_registerName("addChildWindow:ordered:"), _hudPanel, 1);
                        _attachedToParent = true;
                        break;
                    }
                }
            }
            catch
            {
            }
        }

        public static void UpdateOverlay(double fps, double frameTimeMs, double onePercentLow, string filterStr)
        {
            if (!OperatingSystem.IsMacOS())
            {
                return;
            }

            if (!_initialized)
            {
                Initialize();
            }

            if (_hudPanel == IntPtr.Zero)
            {
                return;
            }

            if (!_attachedToParent)
            {
                TryAttachToGameWindow();
            }

            if (!_isVisible)
            {
                objc_msgSend_void_IntPtr(_hudPanel, sel_registerName("orderOut:"), IntPtr.Zero);
                return;
            }

            try
            {
                string text = $"FPS: {fps,5:F1}  ({frameTimeMs,4:F1}ms)  |  1% Low: {onePercentLow,4:F1}";

                IntPtr cfString = CFStringCreateWithCString(IntPtr.Zero, text, kCFStringEncodingUTF8);
                if (cfString != IntPtr.Zero)
                {
                    if (_hudLabel != IntPtr.Zero)
                    {
                        objc_msgSend_void_IntPtr(_hudLabel, sel_registerName("setStringValue:"), cfString);
                    }
                    CFRelease(cfString);
                }

                objc_msgSend(_hudPanel, sel_registerName("orderFrontRegardless"));
            }
            catch
            {
            }
        }
    }
}
