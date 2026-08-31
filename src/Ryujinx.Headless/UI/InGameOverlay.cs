using System;
using System.Diagnostics;
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

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        private static partial void objc_msgSend_void_double4(IntPtr receiver, IntPtr selector, double x, double y, double w, double h);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr objc_msgSend_IntPtr_string(IntPtr receiver, IntPtr selector, string arg1);

        [StructLayout(LayoutKind.Sequential)]
        private struct NSRect
        {
            public double X;
            public double Y;
            public double Width;
            public double Height;

            public NSRect(double x, double y, double w, double h)
            {
                X = x;
                Y = y;
                Width = w;
                Height = h;
            }
        }

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        private static partial IntPtr objc_msgSend_initWindow(IntPtr receiver, IntPtr selector, NSRect rect, long styleMask, long backing, [MarshalAs(UnmanagedType.Bool)] bool defer);

        [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
        private static partial IntPtr objc_msgSend_initView(IntPtr receiver, IntPtr selector, NSRect rect);

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
                    IntPtr nsPanelClass = objc_getClass("NSPanel");
                    if (nsPanelClass == IntPtr.Zero) nsPanelClass = objc_getClass("NSWindow");

                    IntPtr allocSel = sel_registerName("alloc");
                    IntPtr initSel = sel_registerName("initWithContentRect:styleMask:backing:defer:");
                    IntPtr panelAlloc = objc_msgSend(nsPanelClass, allocSel);

                    // Top-right pill geometry: 320x36 pt
                    NSRect frame = new(50, 50, 340, 36);
                    _hudPanel = objc_msgSend_initWindow(panelAlloc, initSel, frame, 0, 2, false);

                    if (_hudPanel == IntPtr.Zero) return;

                    // Configure window properties
                    IntPtr setOpaqueSel = sel_registerName("setOpaque:");
                    objc_msgSend_void_bool(_hudPanel, setOpaqueSel, false);

                    IntPtr setHasShadowSel = sel_registerName("setHasShadow:");
                    objc_msgSend_void_bool(_hudPanel, setHasShadowSel, true);

                    IntPtr setIgnoresMouseSel = sel_registerName("setIgnoresMouseEvents:");
                    objc_msgSend_void_bool(_hudPanel, setIgnoresMouseSel, true);

                    // Set Window Level to Floating / Status (25 = kCGOverlayWindowLevel)
                    IntPtr setLevelSel = sel_registerName("setLevel:");
                    objc_msgSend_void_long(_hudPanel, setLevelSel, 25);

                    // Collection Behavior: CanJoinAllSpaces (1) | FullScreenAuxiliary (256) | Stationary (16) = 273
                    IntPtr setCollectionSel = sel_registerName("setCollectionBehavior:");
                    objc_msgSend_void_long(_hudPanel, setCollectionSel, 273);

                    // Background color: Translucent Dark Glass (rgba 0, 0, 0, 0.72)
                    IntPtr nsColorClass = objc_getClass("NSColor");
                    IntPtr colorWithAlphaSel = sel_registerName("colorWithCalibratedRed:green:blue:alpha:");
                    IntPtr bgColor = objc_msgSend(nsColorClass, allocSel);

                    // Use clear color or semi-transparent background
                    IntPtr clearColorSel = sel_registerName("clearColor");
                    IntPtr clearColor = objc_msgSend(nsColorClass, clearColorSel);

                    IntPtr setBgColorSel = sel_registerName("setBackgroundColor:");
                    objc_msgSend_void_IntPtr(_hudPanel, setBgColorSel, clearColor);

                    // Content View
                    IntPtr contentViewSel = sel_registerName("contentView");
                    IntPtr contentView = objc_msgSend(_hudPanel, contentViewSel);

                    // Create Visual Effect View (Frosted Glass Pill)
                    IntPtr nsVisualEffectClass = objc_getClass("NSVisualEffectView");
                    if (nsVisualEffectClass != IntPtr.Zero)
                    {
                        IntPtr effectAlloc = objc_msgSend(nsVisualEffectClass, allocSel);
                        NSRect effectFrame = new(0, 0, 340, 36);
                        IntPtr effectView = objc_msgSend_initView(effectAlloc, sel_registerName("initWithFrame:"), effectFrame);

                        if (effectView != IntPtr.Zero)
                        {
                            IntPtr setMaterialSel = sel_registerName("setMaterial:");
                            objc_msgSend_void_long(effectView, setMaterialSel, 2); // NSVisualEffectMaterialHUD

                            IntPtr setBlendingSel = sel_registerName("setBlendingMode:");
                            objc_msgSend_void_long(effectView, setBlendingSel, 0); // BehindWindow

                            IntPtr setStateSel = sel_registerName("setState:");
                            objc_msgSend_void_long(effectView, setStateSel, 1); // Active

                            // Rounded corners
                            IntPtr setWantsLayerSel = sel_registerName("setWantsLayer:");
                            objc_msgSend_void_bool(effectView, setWantsLayerSel, true);

                            IntPtr layerSel = sel_registerName("layer");
                            IntPtr layer = objc_msgSend(effectView, layerSel);
                            if (layer != IntPtr.Zero)
                            {
                                IntPtr setCornerRadiusSel = sel_registerName("setCornerRadius:");
                                IntPtr setMasksToBoundsSel = sel_registerName("setMasksToBounds:");
                                objc_msgSend_void_double4(layer, setCornerRadiusSel, 8.0, 0, 0, 0);
                                objc_msgSend_void_bool(layer, setMasksToBoundsSel, true);
                            }

                            IntPtr addSubviewSel = sel_registerName("addSubview:");
                            objc_msgSend_void_IntPtr(contentView, addSubviewSel, effectView);
                            contentView = effectView;
                        }
                    }

                    // Create Label (NSTextField)
                    IntPtr nsTextFieldClass = objc_getClass("NSTextField");
                    IntPtr labelAlloc = objc_msgSend(nsTextFieldClass, allocSel);
                    NSRect labelFrame = new(8, 2, 324, 28);
                    _hudLabel = objc_msgSend_initView(labelAlloc, sel_registerName("initWithFrame:"), labelFrame);

                    if (_hudLabel != IntPtr.Zero)
                    {
                        objc_msgSend_void_bool(_hudLabel, sel_registerName("setBezeled:"), false);
                        objc_msgSend_void_bool(_hudLabel, sel_registerName("setDrawsBackground:"), false);
                        objc_msgSend_void_bool(_hudLabel, sel_registerName("setEditable:"), false);
                        objc_msgSend_void_bool(_hudLabel, sel_registerName("setSelectable:"), false);

                        // White Text
                        IntPtr whiteColorSel = sel_registerName("whiteColor");
                        IntPtr whiteColor = objc_msgSend(nsColorClass, whiteColorSel);
                        objc_msgSend_void_IntPtr(_hudLabel, sel_registerName("setTextColor:"), whiteColor);

                        // Monospaced System Font (13pt, Bold)
                        IntPtr nsFontClass = objc_getClass("NSFont");
                        IntPtr monoFontSel = sel_registerName("monospacedDigitSystemFontOfSize:weight:");
                        IntPtr font = objc_msgSend(nsFontClass, allocSel);
                        // Fallback bold font
                        IntPtr boldFontSel = sel_registerName("boldSystemFontOfSize:");
                        IntPtr boldFont = objc_msgSend_IntPtr(nsFontClass, boldFontSel, (IntPtr)12);
                        if (boldFont != IntPtr.Zero)
                        {
                            objc_msgSend_void_IntPtr(_hudLabel, sel_registerName("setFont:"), boldFont);
                        }

                        // Add to contentView
                        objc_msgSend_void_IntPtr(contentView, sel_registerName("addSubview:"), _hudLabel);
                    }

                    if (_isVisible)
                    {
                        objc_msgSend(_hudPanel, sel_registerName("orderFrontRegardless"));
                    }

                    _initialized = true;
                }
                catch
                {
                    // Fallback gracefully
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
                // Reposition to top-right of main screen
                IntPtr nsScreenClass = objc_getClass("NSScreen");
                IntPtr mainScreenSel = sel_registerName("mainScreen");
                IntPtr mainScreen = objc_msgSend(nsScreenClass, mainScreenSel);

                if (mainScreen != IntPtr.Zero)
                {
                    // Get screen frame
                    IntPtr frameSel = sel_registerName("frame");
                    // Update label text
                    string text = $"FPS: {fps,5:F1} ({frameTimeMs,4:F1}ms) | 1% Low: {onePercentLow,4:F1} | {filterStr}";

                    IntPtr nsStringClass = objc_getClass("NSString");
                    IntPtr stringSel = sel_registerName("stringWithUTF8String:");
                    IntPtr nsStr = objc_msgSend_IntPtr_string(nsStringClass, stringSel, text);

                    if (nsStr != IntPtr.Zero && _hudLabel != IntPtr.Zero)
                    {
                        IntPtr setStringSel = sel_registerName("setStringValue:");
                        objc_msgSend_void_IntPtr(_hudLabel, setStringSel, nsStr);
                    }

                    // Order front
                    objc_msgSend(_hudPanel, sel_registerName("orderFrontRegardless"));
                }
            }
            catch
            {
            }
        }

        public static void SetPosition(int windowX, int windowY, int windowWidth, int windowHeight, bool isFullscreen)
        {
            if (!OperatingSystem.IsMacOS() || !_initialized || _hudPanel == IntPtr.Zero)
            {
                return;
            }

            try
            {
                // In macOS Cocoa coordinates: origin (0, 0) is bottom-left of screen
                IntPtr nsScreenClass = objc_getClass("NSScreen");
                IntPtr mainScreen = objc_msgSend(nsScreenClass, sel_registerName("mainScreen"));

                double screenHeight = 900;
                double screenWidth = 1440;

                // Position in top-right corner with 24pt margin
                double overlayWidth = 340;
                double overlayHeight = 36;
                double posX = isFullscreen ? (screenWidth - overlayWidth - 24) : (windowX + windowWidth - overlayWidth - 16);
                double posY = isFullscreen ? (screenHeight - overlayHeight - 32) : (screenHeight - windowY - overlayHeight - 32);

                if (posX < 10) posX = 24;
                if (posY < 10) posY = 24;

                NSRect newFrame = new(posX, posY, overlayWidth, overlayHeight);
                IntPtr setFrameSel = sel_registerName("setFrame:display:");
                objc_msgSend_void_double4(_hudPanel, setFrameSel, posX, posY, overlayWidth, overlayHeight);
            }
            catch
            {
            }
        }
    }
}
