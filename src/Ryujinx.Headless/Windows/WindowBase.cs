using Humanizer;
using LibHac.Ns;
using LibHac.Util;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Logging;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.GAL.Multithreading;
using Ryujinx.Graphics.OpenGL;
using Ryujinx.Headless.UI;
using Ryujinx.HLE.HOS.Applets;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.HLE.HOS.Services.Am.AppletOE.ApplicationProxyService.ApplicationProxy.Types;
using Ryujinx.HLE.Loaders.Processes;
using Ryujinx.HLE.UI;
using Ryujinx.Input;
using Ryujinx.Input.HLE;
using Ryujinx.Input.SDL3;
using Ryujinx.SDL3.Common;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SDL;
using static SDL.SDL3;
using AntiAliasing = Ryujinx.Common.Configuration.AntiAliasing;
using ScalingFilter = Ryujinx.Common.Configuration.ScalingFilter;
using Switch = Ryujinx.HLE.Switch;
using UserProfile = Ryujinx.HLE.HOS.Services.Account.Acc.UserProfile;

namespace Ryujinx.Headless
{
    abstract unsafe partial class WindowBase : IHostUIHandler, IDisposable
    {
        protected const int DefaultWidth = 1280;
        protected const int DefaultHeight = 720;
        public int TargetFps { get; set; } = 60;
        private SDL_WindowFlags DefaultFlags = SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY | SDL_WindowFlags.SDL_WINDOW_RESIZABLE | SDL_WindowFlags.SDL_WINDOW_INPUT_FOCUS;
        private SDL_WindowFlags FullscreenFlag = 0;

        private static readonly ConcurrentQueue<Action> _mainThreadActions = new();

        public static void QueueMainThreadAction(Action action)
        {
            _mainThreadActions.Enqueue(action);
        }

        public NpadManager NpadManager { get; }
        public TouchScreenManager TouchScreenManager { get; }
        public Switch Device { get; private set; }
        public IRenderer Renderer { get; private set; }

        protected SDL_Window* WindowHandle { get; set; }

        public IHostUITheme HostUITheme { get; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public SDL_DisplayID DisplayId { get; set; }
        public bool IsFullscreen { get; set; }
        public bool IsExclusiveFullscreen { get; set; }
        public int ExclusiveFullscreenWidth { get; set; }
        public int ExclusiveFullscreenHeight { get; set; }
        public AntiAliasing AntiAliasing { get; set; }
        public ScalingFilter ScalingFilter { get; set; }
        public int ScalingFilterLevel { get; set; }

        protected SDL3MouseDriver MouseDriver;
        private readonly InputManager _inputManager;
        private readonly IKeyboard _keyboardInterface;
        protected readonly GraphicsDebugLevel GlLogLevel;
        private readonly Stopwatch _chrono;
        private readonly long _ticksPerFrame;
        private readonly CancellationTokenSource _gpuCancellationTokenSource;
        private readonly ManualResetEvent _gpuDoneEvent;

        private long _ticks;
        private bool _isActive;
        private bool _isStopped;
        private bool _lastFullscreenHotkeyDown;
        private bool _lastF1KeyDown;
        private bool _lastF2KeyDown;
        private bool _lastF3KeyDown;
        private bool _lastF4KeyDown;
        private bool _lastF5KeyDown;
        private bool _lastF6KeyDown;
        private bool _lastF7KeyDown;
        private bool _showOsd = true;
        private string _baseWindowTitle = "Ryu";
        private readonly Stopwatch _osdTimer = new();
        private SDL_WindowID _windowId;

        private string _gpuDriverName;

        private readonly AspectRatio _aspectRatio;
        private readonly bool _enableMouse;
        private readonly bool _ignoreControllerApplet;

        public WindowBase(
            InputManager inputManager,
            GraphicsDebugLevel glLogLevel,
            AspectRatio aspectRatio,
            bool enableMouse,
            HideCursorMode hideCursorMode,
            bool ignoreControllerApplet)
        {
            MouseDriver = new SDL3MouseDriver(hideCursorMode);
            _inputManager = inputManager;
            _inputManager.SetMouseDriver(MouseDriver);
            NpadManager = _inputManager.CreateNpadManager();
            TouchScreenManager = _inputManager.CreateTouchScreenManager();
            _keyboardInterface = (IKeyboard)_inputManager.KeyboardDriver.GetGamepad("0");
            GlLogLevel = glLogLevel;
            _chrono = new Stopwatch();
            _ticksPerFrame = Stopwatch.Frequency / TargetFps;
            _gpuCancellationTokenSource = new CancellationTokenSource();
            _gpuDoneEvent = new ManualResetEvent(false);
            _aspectRatio = aspectRatio;
            _enableMouse = enableMouse;
            _ignoreControllerApplet = ignoreControllerApplet;
            HostUITheme = new HeadlessHostUiTheme();

            SDL3Driver.Instance.Initialize();
        }

        public void Initialize(Switch device, List<InputConfig> inputConfigs, bool enableKeyboard, bool enableMouse)
        {
            Device = device;

            IRenderer renderer = Device.Gpu.Renderer;

            if (renderer is ThreadedRenderer tr)
            {
                renderer = tr.BaseRenderer;
            }

            Renderer = renderer;

            NpadManager.Initialize(device, inputConfigs, enableKeyboard, enableMouse);
            TouchScreenManager.Initialize(device);
        }

        private void InitializeWindow()
        {
            ProcessResult activeProcess = Device.Processes.ActiveApplication;
            ApplicationControlProperty nacp = activeProcess.ApplicationControlProperties;
            int desiredLanguage = (int)Device.System.State.DesiredTitleLanguage;

            string titleNameSection = string.IsNullOrWhiteSpace(nacp.Title[desiredLanguage].NameString.ToString()) ? string.Empty : $" - {nacp.Title[desiredLanguage].NameString.ToString()}";
            string titleVersionSection = string.IsNullOrWhiteSpace(nacp.DisplayVersionString.ToString()) ? string.Empty : $" v{nacp.DisplayVersionString.ToString()}";
            string titleIdSection = string.IsNullOrWhiteSpace(activeProcess.ProgramIdText) ? string.Empty : $" ({activeProcess.ProgramIdText.ToUpper()})";
            string titleArchSection = activeProcess.Is64Bit ? " (64-bit)" : " (32-bit)";

            Width = DefaultWidth;
            Height = DefaultHeight;
            DefaultFlags = SDL_WindowFlags.SDL_WINDOW_HIGH_PIXEL_DENSITY | SDL_WindowFlags.SDL_WINDOW_RESIZABLE | SDL_WindowFlags.SDL_WINDOW_INPUT_FOCUS;

            if (IsExclusiveFullscreen)
            {
                Width = ExclusiveFullscreenWidth;
                Height = ExclusiveFullscreenHeight;
                FullscreenFlag = SDL_WindowFlags.SDL_WINDOW_FULLSCREEN;
            }
            else if (IsFullscreen)
            {
                FullscreenFlag = SDL_WindowFlags.SDL_WINDOW_FULLSCREEN;
            }
            else
            {
                FullscreenFlag = 0;
            }

            SDL_PropertiesID props = SDL_CreateProperties();
            SDL_SetStringProperty(props, SDL_PROP_WINDOW_CREATE_TITLE_STRING, $"Ryu {Program.Version}{titleNameSection}{titleVersionSection}{titleIdSection}{titleArchSection}");
            SDL_SetNumberProperty(props, SDL_PROP_WINDOW_CREATE_X_NUMBER, SDL_WINDOWPOS_CENTERED_DISPLAY(DisplayId));
            SDL_SetNumberProperty(props, SDL_PROP_WINDOW_CREATE_Y_NUMBER, SDL_WINDOWPOS_CENTERED_DISPLAY(DisplayId));
            SDL_SetNumberProperty(props, SDL_PROP_WINDOW_CREATE_WIDTH_NUMBER, Width);
            SDL_SetNumberProperty(props, SDL_PROP_WINDOW_CREATE_HEIGHT_NUMBER, Height);
            SDL_SetNumberProperty(props, SDL_PROP_WINDOW_CREATE_FLAGS_NUMBER, (long)(DefaultFlags | FullscreenFlag | WindowFlags));

            WindowHandle = SDL_CreateWindowWithProperties(props);
            SDL_DestroyProperties(props);

            if (WindowHandle == null)
            {
                string errorMessage = $"SDL_CreateWindow failed with error \"{SDL_GetError()}\"";

                Logger.Error?.Print(LogClass.Application, errorMessage);

                throw new Exception(errorMessage);
            }

            if (IsFullscreen || IsExclusiveFullscreen)
            {
                SDL_SetWindowFullscreen(WindowHandle, true);
            }

            _windowId = SDL_GetWindowID(WindowHandle);
            SDL3Driver.Instance.RegisterWindow(_windowId, HandleWindowEvent);

            // Initialize In-Game HUD Overlay
            if (OperatingSystem.IsMacOS())
            {
                InGameOverlay.Initialize();
            }

            // Start in-terminal Telemetry HUD
            TerminalHud.Start(titleNameSection.TrimStart('-', ' '));
        }

        [LibraryImport("/usr/lib/libobjc.A.dylib", StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr sel_registerName(string name);

        [LibraryImport("/usr/lib/libobjc.A.dylib", StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr objc_getClass(string name);

        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static partial IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

        [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static partial void objc_msgSend_void_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

        private long _lastFullscreenToggleTimestamp;

        public void ToggleFullscreen()
        {
            if (WindowHandle == null)
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            if ((now - _lastFullscreenToggleTimestamp) * 1000 / Stopwatch.Frequency < 300)
            {
                return;
            }
            _lastFullscreenToggleTimestamp = now;

            bool targetFullscreen = !IsFullscreen;
            IsFullscreen = targetFullscreen;

            if (OperatingSystem.IsMacOS())
            {
                try
                {
                    IntPtr nsApp = objc_msgSend(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                    IntPtr keyWindow = objc_msgSend(nsApp, sel_registerName("keyWindow"));
                    if (keyWindow == IntPtr.Zero) keyWindow = objc_msgSend(nsApp, sel_registerName("mainWindow"));
                    if (keyWindow != IntPtr.Zero)
                    {
                        objc_msgSend_void_IntPtr(keyWindow, sel_registerName("toggleFullScreen:"), IntPtr.Zero);
                        Logger.Info?.Print(LogClass.Application, $"Native macOS Fullscreen toggled: {(targetFullscreen ? "Enabled" : "Disabled")}");
                        return;
                    }
                }
                catch
                {
                }
            }

            SDL_SetWindowFullscreen(WindowHandle, targetFullscreen);
            Logger.Info?.Print(LogClass.Application, $"Fullscreen mode toggled: {(targetFullscreen ? "Enabled" : "Disabled")}");
        }

        private void HandleWindowEvent(SDL_Event evnt)
        {
            if (evnt.Type == SDL_EventType.SDL_EVENT_QUIT || evnt.Type == SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED)
            {
                Exit();
                return;
            }

            if (evnt.Type == SDL_EventType.SDL_EVENT_KEY_DOWN)
            {
                SDL_Keymod mod = evnt.key.mod;
                SDL_Keycode key = evnt.key.key;
                SDL_Scancode scancode = evnt.key.scancode;

                bool isGui = (mod & (SDL_Keymod.SDL_KMOD_GUI | SDL_Keymod.SDL_KMOD_CTRL)) != 0;
                bool isAlt = (mod & SDL_Keymod.SDL_KMOD_ALT) != 0;

                // Handle Cmd+Q (macOS) or Alt+F4 / Ctrl+Q (Windows/Linux)
                bool isMacQuit = OperatingSystem.IsMacOS() && isGui && (key == SDL_Keycode.SDLK_Q || scancode == SDL_Scancode.SDL_SCANCODE_Q);
                bool isWinQuit = (isAlt && (key == SDL_Keycode.SDLK_F4 || scancode == SDL_Scancode.SDL_SCANCODE_F4)) ||
                                 (isGui && (key == SDL_Keycode.SDLK_Q || scancode == SDL_Scancode.SDL_SCANCODE_Q));

                if (isMacQuit || isWinQuit)
                {
                    Exit();
                    return;
                }
            }

            if ((uint)evnt.Type >= (uint)SDL_EventType.SDL_EVENT_WINDOW_FIRST && (uint)evnt.Type <= (uint)SDL_EventType.SDL_EVENT_WINDOW_LAST)
            {
                switch (evnt.Type)
                {
                    case SDL_EventType.SDL_EVENT_WINDOW_RESIZED:
                    case SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
                        Width = evnt.window.data1;
                        Height = evnt.window.data2;
                        Renderer?.Window?.SetSize(Width, Height);
                        MouseDriver?.SetClientSize(Width, Height);
                        break;

                    case SDL_EventType.SDL_EVENT_WINDOW_ENTER_FULLSCREEN:
                        IsFullscreen = true;
                        break;

                    case SDL_EventType.SDL_EVENT_WINDOW_LEAVE_FULLSCREEN:
                        IsFullscreen = false;
                        break;

                    case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                        Exit();
                        break;
                }
            }
            else
            {
                MouseDriver?.Update(evnt);
            }
        }

        protected abstract void InitializeWindowRenderer();

        protected abstract void InitializeRenderer();

        protected abstract void FinalizeWindowRenderer();

        protected abstract void SwapBuffers();

        public abstract SDL_WindowFlags WindowFlags { get; }

        private string GetGpuDriverName()
        {
            return Renderer?.GetHardwareInfo().GpuDriver ?? "Default";
        }

        private void SetAntiAliasing()
        {
            Renderer?.Window?.SetAntiAliasing(AntiAliasing);
        }

        private void SetScalingFilter()
        {
            Renderer?.Window?.SetScalingFilter(ScalingFilter);
            Renderer?.Window?.SetScalingFilterLevel(ScalingFilterLevel);
        }

        public void ShowQuickSettingsMenu()
        {
            bool isDocked = Device?.System?.State?.DockedMode ?? true;
            string message = "--- Ryu Live Quick Settings & Controls ---\n\n" +
                             $"• Target Framerate : {TargetFps} FPS  (Press F2 or Cmd+2 to toggle)\n" +
                             $"• Scaling Filter   : {ScalingFilter}  (Press F3 or Cmd+3 to cycle)\n" +
                             $"• FSR Sharpening   : {ScalingFilterLevel}%  (Press F4 or Cmd+4 to cycle)\n" +
                             $"• Anti-Aliasing    : {AntiAliasing}  (Press F5 or Cmd+5 to toggle)\n" +
                             $"• Operation Mode   : {(isDocked ? "Docked" : "Handheld")}  (Press F6 or Cmd+6 to toggle)\n" +
                             $"• On-Screen OSD    : {(_showOsd ? "Enabled" : "Disabled")}  (Press F7 or Cmd+7 to toggle)\n" +
                             $"• Fullscreen Mode  : {(IsFullscreen ? "Active" : "Windowed")}  (Press Cmd+F or F11 to toggle)\n" +
                             $"• Instant Quit     : Cmd+Q\n\n" +
                             "Click OK to resume gameplay.";

            SDL_ShowSimpleMessageBox(SDL_MessageBoxFlags.SDL_MESSAGEBOX_INFORMATION, "Ryu In-Game Quick Settings", message, WindowHandle);
        }

        public void CycleTargetFps()
        {
            int newFps = TargetFps switch
            {
                30 => 60,
                60 => 120,
                120 => 30,
                _ => 60
            };

            SetTargetFps(newFps);
        }

        public void SetTargetFps(int targetFps)
        {
            TargetFps = targetFps;
            if (Device != null)
            {
                Device.CustomVSyncInterval = targetFps;
                Device.CustomVSyncIntervalEnabled = targetFps != 30;
            }
            Logger.Info?.Print(LogClass.Application, $"Target FPS switched to: {targetFps} FPS");
        }

        public void CycleScalingFilter()
        {
            ScalingFilter newFilter = ScalingFilter switch
            {
                ScalingFilter.Bilinear => ScalingFilter.Fsr,
                ScalingFilter.Fsr => ScalingFilter.Nearest,
                _ => ScalingFilter.Bilinear
            };

            ScalingFilter = newFilter;
            Renderer?.Window?.SetScalingFilter(newFilter);
            Logger.Info?.Print(LogClass.Application, $"Scaling Filter switched to: {newFilter}");
        }

        public void CycleScalingFilterLevel()
        {
            int newLevel = ScalingFilterLevel switch
            {
                80 => 100,
                100 => 50,
                50 => 20,
                _ => 80
            };

            ScalingFilterLevel = newLevel;
            Renderer?.Window?.SetScalingFilterLevel(newLevel);
            Logger.Info?.Print(LogClass.Application, $"FSR Sharpening Level switched to: {newLevel}%");
        }

        public void ToggleAntiAliasing()
        {
            AntiAliasing newAa = AntiAliasing == AntiAliasing.None ? AntiAliasing.SmaaUltra : AntiAliasing.None;
            AntiAliasing = newAa;
            Renderer?.Window?.SetAntiAliasing(newAa);
            Logger.Info?.Print(LogClass.Application, $"Anti-Aliasing switched to: {newAa}");
        }

        public void ToggleDockedMode()
        {
            if (Device?.System?.State != null)
            {
                bool newDocked = !Device.System.State.DockedMode;
                Device.System.State.DockedMode = newDocked;
                Logger.Info?.Print(LogClass.Application, $"Operation Mode switched to: {(newDocked ? "Docked" : "Handheld")}");
            }
        }

        public void Render()
        {
            InitializeWindowRenderer();

            Device.Gpu.Renderer.Initialize(GlLogLevel);

            InitializeRenderer();

            SetAntiAliasing();

            SetScalingFilter();

            _gpuDriverName = GetGpuDriverName();

            Device.Gpu.Renderer.RunLoop(() =>
            {
                Device.Gpu.SetGpuThread();
                Device.Gpu.InitializeShaderCache(_gpuCancellationTokenSource.Token);

                while (_isActive)
                {
                    if (_isStopped)
                    {
                        return;
                    }

                    _ticks += _chrono.ElapsedTicks;

                    _chrono.Restart();

                    if (Device.WaitFifo())
                    {
                        Device.Statistics.RecordFifoStart();
                        Device.ProcessFrame();
                        Device.Statistics.RecordFifoEnd();
                    }

                    while (Device.ConsumeFrameAvailable())
                    {
                        Device.PresentFrame(SwapBuffers);
                    }

                    // Feed metrics to HUD
                    TerminalHud.UpdateMetrics(Device.Statistics.GetGameFrameRate(), Device.Statistics.GetGameFrameTime());

                    if (_ticks >= _ticksPerFrame)
                    {
                        _ticks = Math.Min(_ticks - _ticksPerFrame, _ticksPerFrame);
                    }
                }

                // Make sure all commands in the run loop are fully executed before leaving the loop.
                if (Device.Gpu.Renderer is ThreadedRenderer threaded)
                {
                    threaded.FlushThreadedCommands();
                }

                _gpuDoneEvent.Set();
            });

            FinalizeWindowRenderer();
        }

        public void Exit()
        {
            if (_isStopped)
            {
                return;
            }

            _isStopped = true;
            _isActive = false;

            _gpuCancellationTokenSource.Cancel();

            TouchScreenManager?.Dispose();
            NpadManager?.Dispose();

            if (WindowHandle != null)
            {
                SDL3Driver.Instance.UnregisterWindow(_windowId);
                SDL_DestroyWindow(WindowHandle);
                WindowHandle = null;
            }
        }

        public static void ProcessMainThreadQueue()
        {
            while (_mainThreadActions.TryDequeue(out Action action))
            {
                action();
            }
        }

        public void MainLoop()
        {
            while (_isActive)
            {
                UpdateFrame();

                SDL_PumpEvents();

                ProcessMainThreadQueue();

                Thread.Sleep(1);
            }
        }

        private bool UpdateFrame()
        {
            if (!_isActive)
            {
                return true;
            }

            if (_isStopped)
            {
                return false;
            }

            NpadManager.Update();

            bool hasTouch = false;

            if (!_enableMouse)
            {
                hasTouch = TouchScreenManager.Update(true, (_inputManager.MouseDriver as SDL3MouseDriver).IsButtonPressed(MouseButton.Button1), _aspectRatio.ToFloat());
            }

            if (!hasTouch)
            {
                TouchScreenManager.Update(false);
            }

            // Check hardware hotkeys directly from SDL state
            SDLBool* keyboardState = SDL_GetKeyboardState(null);
            if (keyboardState != null)
            {
                SDL_Keymod mod = SDL_GetModState();
                bool isGui = (mod & (SDL_Keymod.SDL_KMOD_GUI | SDL_Keymod.SDL_KMOD_CTRL)) != 0;
                bool isAlt = (mod & SDL_Keymod.SDL_KMOD_ALT) != 0;

                bool f1Pressed = keyboardState[(int)SDL_Scancode.SDL_SCANCODE_F1] || (isGui && keyboardState[(int)SDL_Scancode.SDL_SCANCODE_1]) || (isGui && keyboardState[(int)SDL_Scancode.SDL_SCANCODE_COMMA]);
                bool f2Pressed = keyboardState[(int)SDL_Scancode.SDL_SCANCODE_F2] || (isGui && keyboardState[(int)SDL_Scancode.SDL_SCANCODE_2]);
                bool f3Pressed = keyboardState[(int)SDL_Scancode.SDL_SCANCODE_F3] || (isGui && keyboardState[(int)SDL_Scancode.SDL_SCANCODE_3]);
                bool f4Pressed = keyboardState[(int)SDL_Scancode.SDL_SCANCODE_F4] || (isGui && keyboardState[(int)SDL_Scancode.SDL_SCANCODE_4]);
                bool f5Pressed = keyboardState[(int)SDL_Scancode.SDL_SCANCODE_F5] || (isGui && keyboardState[(int)SDL_Scancode.SDL_SCANCODE_5]);
                bool f6Pressed = keyboardState[(int)SDL_Scancode.SDL_SCANCODE_F6] || (isGui && keyboardState[(int)SDL_Scancode.SDL_SCANCODE_6]);
                bool f7Pressed = keyboardState[(int)SDL_Scancode.SDL_SCANCODE_F7] || (isGui && keyboardState[(int)SDL_Scancode.SDL_SCANCODE_7]);
                bool f11Pressed = keyboardState[(int)SDL_Scancode.SDL_SCANCODE_F11];
                bool fPressed = keyboardState[(int)SDL_Scancode.SDL_SCANCODE_F];
                bool returnPressed = keyboardState[(int)SDL_Scancode.SDL_SCANCODE_RETURN];
                bool qPressed = keyboardState[(int)SDL_Scancode.SDL_SCANCODE_Q];

                if (isGui && qPressed)
                {
                    Exit();
                    return false;
                }

                // F1: Quick Settings Menu
                if (f1Pressed && !_lastF1KeyDown)
                {
                    ShowQuickSettingsMenu();
                }
                _lastF1KeyDown = f1Pressed;

                // F2: Cycle Target FPS (30 / 60 / 120)
                if (f2Pressed && !_lastF2KeyDown)
                {
                    CycleTargetFps();
                }
                _lastF2KeyDown = f2Pressed;

                // F3: Cycle Scaling Filter (Bilinear / FSR / Nearest)
                if (f3Pressed && !_lastF3KeyDown)
                {
                    CycleScalingFilter();
                }
                _lastF3KeyDown = f3Pressed;

                // F4: Cycle FSR Sharpening Level
                if (f4Pressed && !_lastF4KeyDown)
                {
                    CycleScalingFilterLevel();
                }
                _lastF4KeyDown = f4Pressed;

                // F5: Toggle Anti-Aliasing
                if (f5Pressed && !_lastF5KeyDown)
                {
                    ToggleAntiAliasing();
                }
                _lastF5KeyDown = f5Pressed;

                // F6: Toggle Docked / Handheld Mode
                if (f6Pressed && !_lastF6KeyDown)
                {
                    ToggleDockedMode();
                }
                _lastF6KeyDown = f6Pressed;

                // F7: Toggle OSD Telemetry
                if (f7Pressed && !_lastF7KeyDown)
                {
                    _showOsd = !_showOsd;
                    Renderer?.Window?.SetOsdText(string.Empty, _showOsd);
                    if (!_showOsd && WindowHandle != null)
                    {
                        SDL_SetWindowTitle(WindowHandle, _baseWindowTitle);
                    }
                    Logger.Info?.Print(LogClass.Application, $"In-Game OSD HUD toggled: {(_showOsd ? "Enabled" : "Disabled")}");
                }
                _lastF7KeyDown = f7Pressed;

                // Fullscreen
                bool isFullscreenHotkeyDown = f11Pressed || (isGui && fPressed) || (isAlt && returnPressed);
                if (isFullscreenHotkeyDown && !_lastFullscreenHotkeyDown)
                {
                    ToggleFullscreen();
                }
                _lastFullscreenHotkeyDown = isFullscreenHotkeyDown;
            }

            // Update real-time on-screen GPU HUD overlay & window title telemetry
            if (WindowHandle != null && _osdTimer.ElapsedMilliseconds >= 250)
            {
                _osdTimer.Restart();
                double fps = Device?.Statistics?.GetGameFrameRate() ?? Ryujinx.Headless.UI.TerminalHud.CurrentFps;
                double frameTime = Device?.Statistics?.GetGameFrameTime() ?? Ryujinx.Headless.UI.TerminalHud.FrameTimeMs;
                double low1Percent = Ryujinx.Headless.UI.TerminalHud.OnePercentLow;
                string filterStr = ScalingFilter == ScalingFilter.Fsr ? $"FSR {ScalingFilterLevel}%" : ScalingFilter.ToString();
                string modeStr = (Device?.System?.State?.DockedMode ?? true) ? "Docked" : "Handheld";
                string fullStr = IsFullscreen ? "Fullscreen" : "Windowed";

                string osdText = $"FPS: {fps,5:F1} ({frameTime,4:F1}ms) | 1% Low: {low1Percent,4:F1}";
                Renderer?.Window?.SetOsdText(osdText, _showOsd);

                if (_showOsd)
                {
                    SDL_SetWindowTitle(WindowHandle, $"{_baseWindowTitle} | {osdText} | {filterStr} | {modeStr} | {fullStr}");
                }
            }

            Device.Hid.DebugPad.Update();
            MouseDriver.UpdatePosition();

            return true;
        }

        public void Execute()
        {
            _chrono.Restart();
            _isActive = true;

            InitializeWindow();

            Thread renderLoopThread = new(Render)
            {
                Name = "GUI.RenderLoop",
            };
            renderLoopThread.Start();

            MainLoop();

            _gpuDoneEvent.WaitOne(500);
            _gpuDoneEvent.Dispose();

            Exit();

            Environment.Exit(0);
        }

        public bool DisplayInputDialog(SoftwareKeyboardUIArgs args, out string userText)
        {
            userText = "Ryu";
            return true;
        }

        public bool DisplayMessageDialog(string title, string message)
        {
            SDL_ShowSimpleMessageBox(SDL_MessageBoxFlags.SDL_MESSAGEBOX_INFORMATION, title, message, WindowHandle);
            return true;
        }

        public bool DisplayCabinetDialog(out string userText)
        {
            userText = "Ryu";
            return true;
        }

        public void DisplayCabinetMessageDialog()
        {
            SDL_ShowSimpleMessageBox(SDL_MessageBoxFlags.SDL_MESSAGEBOX_INFORMATION, "Cabinet Dialog", "Please scan your Amiibo now.", WindowHandle);
        }

        public bool DisplayMessageDialog(ControllerAppletUIArgs args)
        {
            if (_ignoreControllerApplet)
                return false;

            string playerCount = args.PlayerCountMin == args.PlayerCountMax ? $"exactly {args.PlayerCountMin}" : $"{args.PlayerCountMin}-{args.PlayerCountMax}";

            string message = $"Application requests {playerCount} {"player".ToQuantity(args.PlayerCountMin + args.PlayerCountMax, ShowQuantityAs.None)} with:\n\n"
                           + $"TYPES: {args.SupportedStyles}\n\n"
                           + $"PLAYERS: {string.Join(", ", args.SupportedPlayers)}\n\n"
                           + (args.IsDocked ? "Docked mode set. Handheld is also invalid.\n\n" : string.Empty)
                           + "Please reconfigure Input now and then press OK.";

            return DisplayMessageDialog("Controller Applet", message);
        }

        public IDynamicTextInputHandler CreateDynamicTextInputHandler()
        {
            return new HeadlessDynamicTextInputHandler();
        }

        public void ExecuteProgram(Switch device, ProgramSpecifyKind kind, ulong value)
        {
            device.Configuration.UserChannelPersistence.ExecuteProgram(kind, value);
            Exit();
        }

        public unsafe bool DisplayErrorAppletDialog(string title, string message, string[] buttonsText, (uint Module, uint Description)? errorCode = null)
        {
            SDL_MessageBoxButtonData[] buttons = new SDL_MessageBoxButtonData[buttonsText.Length];

            for (int i = 0; i < buttonsText.Length; i++)
            {
                string buttonText = buttonsText[i];
                fixed (byte* pButtonText = &buttonText.ToBytes()[0])
                buttons[i] = new SDL_MessageBoxButtonData
                {
                    buttonID = i,
                    text = pButtonText,
                };
            }

            fixed (byte* pTitle = &title.ToBytes()[0])
            fixed (byte* pMessage = &message.ToBytes()[0])
            fixed (SDL_MessageBoxButtonData* p = &buttons[0])
            {
                SDL_MessageBoxData data = new()
                {
                    title = pTitle,
                    message = pMessage,
                    buttons = p,
                    numbuttons = buttonsText.Length,
                    window = WindowHandle
                };

                SDL_ShowMessageBox(&data, null);
            }

            return true;
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                _isActive = false;
                TouchScreenManager?.Dispose();
                NpadManager.Dispose();

                SDL3Driver.Instance.UnregisterWindow(_windowId);

                SDL_DestroyWindow(WindowHandle);

                SDL3Driver.Instance.Dispose();
            }
        }

        public UserProfile ShowPlayerSelectDialog()
        {
            return AccountSaveDataManager.GetLastUsedUser();
        }
        
        public void TakeScreenshot()
        {
            throw new NotImplementedException();
        }
    }
}
