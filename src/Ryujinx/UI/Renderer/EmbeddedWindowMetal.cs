using SPB.Platform.Metal;
using SPB.Platform.Metal;
using SPB.Windowing;
using System;

namespace Ryujinx.Ava.UI.Renderer
{
    public class EmbeddedWindowMetal : EmbeddedWindow
    {
        public nint GetLayer()
        {
            if (OperatingSystem.IsMacOS())
            {
                return MetalLayer;
            }

            throw new PlatformNotSupportedException();
        }
    }
}
