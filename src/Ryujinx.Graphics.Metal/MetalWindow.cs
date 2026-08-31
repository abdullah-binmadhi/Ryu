using Ryujinx.Common.Configuration;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Metal.Interop;
using System;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    [SupportedOSPlatform("macos")]
    public class MetalWindow : IWindow
    {
        private readonly MetalRenderer _renderer;
        private readonly nint _device;
        private readonly nint _commandQueue;

        private AntiAliasing _antiAliasing;
        private ScalingFilter _scalingFilter;
        private float _scalingFilterLevel;
        private int _width = 1280;
        private int _height = 720;

        public MetalWindow(MetalRenderer renderer, nint device, nint commandQueue)
        {
            _renderer = renderer;
            _device = device;
            _commandQueue = commandQueue;
        }

        public void Present(ITexture texture, ImageCrop crop, Action swapBuffersCallback)
        {
            swapBuffersCallback();
        }

        public void SetSize(int width, int height)
        {
            _width = width;
            _height = height;
        }

        public void ChangeVSyncMode(VSyncMode vSyncMode) { }

        public void SetAntiAliasing(AntiAliasing antialiasing)
        {
            _antiAliasing = antialiasing;
        }

        public void SetScalingFilter(ScalingFilter type)
        {
            _scalingFilter = type;
        }

        public void SetScalingFilterLevel(float level)
        {
            _scalingFilterLevel = level;
        }

        public void SetColorSpacePassthrough(bool colorSpacePassThroughEnabled) { }

        public void SetOsdText(string text, bool visible) { }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
