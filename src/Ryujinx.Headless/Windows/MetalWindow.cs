using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using Ryujinx.Input.HLE;
using Ryujinx.SDL3.Common;
using System;
using SDL;
using static SDL.SDL3;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Headless
{
    [SupportedOSPlatform("macos")]
    class MetalWindow : WindowBase
    {
        public MetalWindow(
            InputManager inputManager,
            GraphicsDebugLevel glLogLevel,
            AspectRatio aspectRatio,
            bool enableMouse,
            HideCursorMode hideCursorMode,
            bool ignoreControllerApplet)
            : base(inputManager, glLogLevel, aspectRatio, enableMouse, hideCursorMode, ignoreControllerApplet)
        {
        }

        public override SDL_WindowFlags WindowFlags => SDL_WindowFlags.SDL_WINDOW_METAL;

        protected override void InitializeWindowRenderer() { }

        protected override void InitializeRenderer()
        {
            if (IsExclusiveFullscreen)
            {
                Renderer?.Window?.SetSize(ExclusiveFullscreenWidth, ExclusiveFullscreenHeight);
                MouseDriver?.SetClientSize(ExclusiveFullscreenWidth, ExclusiveFullscreenHeight);
            }
            else
            {
                Renderer?.Window?.SetSize(DefaultWidth, DefaultHeight);
                MouseDriver?.SetClientSize(DefaultWidth, DefaultHeight);
            }
        }

        public unsafe nint CreateMetalView()
        {
            nint metalView = (nint)SDL_Metal_CreateView(WindowHandle);
            if (metalView == nint.Zero)
            {
                string errorMessage = $"SDL_Metal_CreateView failed with error \"{SDL_GetError()}\"";
                Logger.Error?.Print(LogClass.Application, errorMessage);
                throw new Exception(errorMessage);
            }
            return metalView;
        }

        protected override void FinalizeWindowRenderer()
        {
            Device.DisposeGpu();
        }

        protected override void SwapBuffers() { }
    }
}
