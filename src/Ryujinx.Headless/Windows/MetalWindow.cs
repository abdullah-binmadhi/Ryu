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
        private nint _metalView;

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

        protected override void InitializeWindowRenderer()
        {
            if (_metalView != nint.Zero)
            {
                return;
            }

            nint metalView = CreateMetalView();

            nint layer = nint.Zero;

            void AcquireLayer()
            {
                layer = (nint)SDL_Metal_GetLayer(metalView);
            }

            if (SDL3Driver.MainThreadDispatcher != null)
            {
                SDL3Driver.MainThreadDispatcher(AcquireLayer);
            }
            else
            {
                AcquireLayer();
            }

            if (layer != nint.Zero && Renderer?.Window is Ryujinx.Graphics.Metal.MetalWindow metalWindow)
            {
                metalWindow.SetLayer(layer);
            }

            _metalView = metalView;
        }

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
            nint metalView = nint.Zero;

            void CreateView()
            {
                metalView = (nint)SDL_Metal_CreateView(WindowHandle);
            }

            if (SDL3Driver.MainThreadDispatcher != null)
            {
                SDL3Driver.MainThreadDispatcher(CreateView);
            }
            else
            {
                CreateView();
            }

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
            if (_metalView != nint.Zero)
            {
                SDL_Metal_DestroyView(_metalView);
                _metalView = nint.Zero;
            }

            Device.DisposeGpu();
        }

        protected override void SwapBuffers() { }
    }
}
