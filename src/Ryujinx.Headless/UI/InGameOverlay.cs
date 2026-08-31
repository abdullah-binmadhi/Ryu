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

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr objc_msgSend_IntPtr_string(IntPtr receiver, IntPtr selector, string arg1);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        private static partial IntPtr objc_msgSend_initWindow(IntPtr receiver, IntPtr selector, double x, double y, double w, double h, long styleMask, long backing, [MarshalAs(UnmanagedType.Bool)] bool defer);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        private static partial IntPtr objc_msgSend_initView(IntPtr receiver, IntPtr selector, double x, double y, double w, double h);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        private static partial void objc_msgSend_setFrame(IntPtr receiver, IntPtr selector, double x, double y, double w, double h, [MarshalAs(UnmanagedType.Bool)] bool display);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        private static partial IntPtr objc_msgSend_color(IntPtr receiver, IntPtr selector, double r, double g, double b, double a);

        private static IntPtr _hudPanel = IntPtr.Zero;
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
                if (_initialized && _hudPanel != IntPtr.Zero)
                {
                    if (_isVisible)
                    {
                        IntPtr orderFrontSel = sel_registerName("orderFrontRegardless");
                        objc_msgSend(_hudPanel, orderFrontSel);
                    }
                    else
                    {
                        IntPtr orderOutSel = sel_registerName("orderOut:");
                        objc_msgSend_void_IntPtr(_hudPanel, orderOutSel, IntPtr.Zero);
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
                if (_initialized) return;

                try
                {
                    IntPtr nsAppClass = objc_getClass("NSApplication");
                    if (nsAppClass != IntPtr.Zero)
                    {
                        objc_msgSend(nsAppClass, sel_registerName("sharedApplication"));
                    }

                    IntPtr nsPanelClass = objc_getClass("NSPanel");
                    if (nsPanelClass == IntPtr.Zero) nsPanelClass = objc_getClass("NSWindow");

                    IntPtr allocSel = sel_registerName("alloc");
                    IntPtr initSel = sel_registerName("initWithContentRect:styleMask:backing:defer:");
                    IntPtr panelAlloc = objc_msgSend(nsPanelClass, allocSel);

                    // Position initially in top-right area (x: 800, y: 700, w: 320, h: 36)
                    _hudPanel = objc_msgSend_initWindow(panelAlloc, initSel, 100, 700, 320, 36, 0, 2, false);

                    if (_hudPanel == IntPtr.Zero)
                    {
                        Logger.Warning?.Print(LogClass.Application, "Could not initialize native in-game OSD panel.");
                        return;
                    }

                    // Window properties: borderless, transparent, floating
                    objc_msgSend_void_bool(_hudPanel, sel_registerName("setOpaque:"), false);
                    objc_msgSend_void_bool(_hudPanel, sel_registerName("setHasShadow:"), true);
                    objc_msgSend_void_bool(_hudPanel, sel_registerName("setIgnoresMouseEvents:"), true);

                    // Level 25 = kCGOverlayWindowLevelKey / Status level (floats above all fullscreen spaces)
                    objc_msgSend_void_long(_hudPanel, sel_registerName("setLevel:"), 25);

                    // Collection behavior: CanJoinAllSpaces (1) | FullScreenAuxiliary (256) | Stationary (16) = 273
                    objc_msgSend_void_long(_hudPanel, sel_registerName("setCollectionBehavior:"), 273);

                    // Background color: transparent
                    IntPtr nsColorClass = objc_getClass("NSColor");
                    IntPtr clearColor = objc_msgSend(nsColorClass, sel_registerName("clearColor"));
                    objc_msgSend_void_IntPtr(_hudPanel, sel_registerName("setBackgroundColor:"), clearColor);

                    IntPtr contentView = objc_msgSend(_hudPanel, sel_registerName("contentView"));

                    // Create NSTextField for OSD text
                    IntPtr nsTextFieldClass = objc_getClass("NSTextField");
                    IntPtr labelAlloc = objc_msgSend(nsTextFieldClass, allocSel);
                    _hudLabel = objc_msgSend_initView(labelAlloc, sel_registerName("initWithFrame:"), 0, 0, 320, 36);

                    if (_hudLabel != IntPtr.Zero)
                    {
                        objc_msgSend_void_bool(_hudLabel, sel_registerName("setBezeled:"), false);
                        objc_msgSend_void_bool(_hudLabel, sel_registerName("setDrawsBackground:"), true);
                        objc_msgSend_void_bool(_hudLabel, sel_registerName("setEditable:"), false);
                        objc_msgSend_void_bool(_hudLabel, sel_registerName("setSelectable:"), false);

                        // Dark translucent background color for pill
                        IntPtr colorWithAlphaSel = sel_registerName("colorWithCalibratedRed:green:blue:alpha:");
                        IntPtr pillBgColor = objc_msgSend_color(nsColorClass, colorWithAlphaSel, 0.05, 0.05, 0.05, 0.85);
                        if (pillBgColor != IntPtr.Zero)
                        {
                            objc_msgSend_void_IntPtr(_hudLabel, sel_registerName("setBackgroundColor:"), pillBgColor);
                        }

                        // Text Color: bright white
                        IntPtr whiteColor = objc_msgSend(nsColorClass, sel_registerName("whiteColor"));
                        objc_msgSend_void_IntPtr(_hudLabel, sel_registerName("setTextColor:"), whiteColor);

                        // Bold system font
                        IntPtr nsFontClass = objc_getClass("NSFont");
                        IntPtr boldFont = objc_msgSend_IntPtr(nsFontClass, sel_registerName("boldSystemFontOfSize:"), (IntPtr)12);
                        if (boldFont != IntPtr.Zero)
                        {
                            objc_msgSend_void_IntPtr(_hudLabel, sel_registerName("setFont:"), boldFont);
                        }

                        // Alignment: Center (1)
                        objc_msgSend_void_long(_hudLabel, sel_registerName("setAlignment:"), 1);

                        // Add to contentView
                        objc_msgSend_void_IntPtr(contentView, sel_registerName("addSubview:"), _hudLabel);
                    }

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

        public static void UpdateOverlay(double fps, double frameTimeMs, double onePercentLow, string filterStr)
        {
            if (!OperatingSystem.IsMacOS() || !_initialized || _hudPanel == IntPtr.Zero || !_isVisible)
            {
                return;
            }

            try
            {
                string text = $"FPS: {fps,5:F1} ({frameTimeMs,4:F1}ms) | 1% Low: {onePercentLow,4:F1} | {filterStr}";

                IntPtr nsStringClass = objc_getClass("NSString");
                IntPtr stringSel = sel_registerName("stringWithUTF8String:");
                IntPtr nsStr = objc_msgSend_IntPtr_string(nsStringClass, stringSel, text);

                if (nsStr != IntPtr.Zero && _hudLabel != IntPtr.Zero)
                {
                    objc_msgSend_void_IntPtr(_hudLabel, sel_registerName("setStringValue:"), nsStr);
                }

                objc_msgSend(_hudPanel, sel_registerName("orderFrontRegardless"));
            }
            catch
            {
            }
        }
    }
}
