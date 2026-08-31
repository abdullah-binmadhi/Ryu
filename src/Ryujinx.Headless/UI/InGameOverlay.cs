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
        private static partial void objc_msgSend_void_ulong(IntPtr receiver, IntPtr selector, ulong arg1);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        private static partial void objc_msgSend_void_double(IntPtr receiver, IntPtr selector, double arg1);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        private static partial IntPtr objc_msgSend_initView(IntPtr receiver, IntPtr selector, double x, double y, double w, double h);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        private static partial IntPtr objc_msgSend_color(IntPtr receiver, IntPtr selector, double r, double g, double b, double a);

        [LibraryImport(CoreFoundationLib, StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr CFStringCreateWithCString(IntPtr alloc, string str, uint encoding);

        [LibraryImport(CoreFoundationLib)]
        private static partial void CFRelease(IntPtr cf);

        private const uint kCFStringEncodingUTF8 = 0x08000100;

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
                if (_initialized && _hudLabel != IntPtr.Zero) return;

                try
                {
                    IntPtr nsAppClass = objc_getClass("NSApplication");
                    if (nsAppClass == IntPtr.Zero) return;

                    IntPtr nsApp = objc_msgSend(nsAppClass, sel_registerName("sharedApplication"));
                    if (nsApp == IntPtr.Zero) return;

                    IntPtr gameWindow = objc_msgSend(nsApp, sel_registerName("keyWindow"));
                    if (gameWindow == IntPtr.Zero) gameWindow = objc_msgSend(nsApp, sel_registerName("mainWindow"));

                    if (gameWindow == IntPtr.Zero)
                    {
                        IntPtr windowsArray = objc_msgSend(nsApp, sel_registerName("windows"));
                        if (windowsArray != IntPtr.Zero)
                        {
                            IntPtr firstObjSel = sel_registerName("firstObject");
                            gameWindow = objc_msgSend(windowsArray, firstObjSel);
                        }
                    }

                    if (gameWindow == IntPtr.Zero) return;

                    IntPtr mainContentView = objc_msgSend(gameWindow, sel_registerName("contentView"));
                    if (mainContentView == IntPtr.Zero) return;

                    IntPtr allocSel = sel_registerName("alloc");
                    IntPtr nsTextFieldClass = objc_getClass("NSTextField");
                    IntPtr nsColorClass = objc_getClass("NSColor");
                    IntPtr nsFontClass = objc_getClass("NSFont");

                    // Create NSTextField pinned at top-left
                    IntPtr labelAlloc = objc_msgSend(nsTextFieldClass, allocSel);
                    _hudLabel = objc_msgSend_initView(labelAlloc, sel_registerName("initWithFrame:"), 20, 680, 500, 30);

                    if (_hudLabel == IntPtr.Zero) return;

                    // Autoresizing mask: NSViewMinYMargin (8) keeps it pinned to top of window
                    objc_msgSend_void_ulong(_hudLabel, sel_registerName("setAutoresizingMask:"), 8);

                    // Transparent styling - NO black bars or background boxes
                    objc_msgSend_void_bool(_hudLabel, sel_registerName("setBezeled:"), false);
                    objc_msgSend_void_bool(_hudLabel, sel_registerName("setDrawsBackground:"), false);
                    objc_msgSend_void_bool(_hudLabel, sel_registerName("setEditable:"), false);
                    objc_msgSend_void_bool(_hudLabel, sel_registerName("setSelectable:"), false);

                    // Crucial for CAMetalLayer composition: enable Layer-backed rendering with highest Z-order
                    objc_msgSend_void_bool(_hudLabel, sel_registerName("setWantsLayer:"), true);
                    IntPtr layer = objc_msgSend(_hudLabel, sel_registerName("layer"));
                    if (layer != IntPtr.Zero)
                    {
                        objc_msgSend_void_double(layer, sel_registerName("setZPosition:"), 99999.0);
                    }

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

                    // Initial string
                    IntPtr initialStr = CFStringCreateWithCString(IntPtr.Zero, "FPS: 60.0  (16.6ms)  |  1% Low: 58.5", kCFStringEncodingUTF8);
                    if (initialStr != IntPtr.Zero)
                    {
                        objc_msgSend_void_IntPtr(_hudLabel, sel_registerName("setStringValue:"), initialStr);
                        CFRelease(initialStr);
                    }

                    // Add subview on top of Metal layer
                    objc_msgSend_void_IntPtr(mainContentView, sel_registerName("addSubview:"), _hudLabel);
                    objc_msgSend_void_bool(_hudLabel, sel_registerName("setHidden:"), !_isVisible);

                    _initialized = true;
                    Logger.Info?.Print(LogClass.Application, "Layer-backed In-Game OSD initialized on CAMetalLayer.");
                }
                catch (Exception ex)
                {
                    Logger.Warning?.Print(LogClass.Application, $"Failed to attach In-Game OSD: {ex.Message}");
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
                if (!_initialized || _hudLabel == IntPtr.Zero) return;
            }

            if (!_isVisible)
            {
                return;
            }

            try
            {
                string text = $"FPS: {fps,5:F1}  ({frameTimeMs,4:F1}ms)  |  1% Low: {onePercentLow,4:F1}";

                IntPtr cfString = CFStringCreateWithCString(IntPtr.Zero, text, kCFStringEncodingUTF8);
                if (cfString != IntPtr.Zero)
                {
                    objc_msgSend_void_IntPtr(_hudLabel, sel_registerName("setStringValue:"), cfString);
                    CFRelease(cfString);
                }
            }
            catch
            {
            }
        }
    }
}
