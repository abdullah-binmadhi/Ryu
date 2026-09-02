using Ryujinx.Graphics.Metal.Interop;
using System;

namespace Ryujinx.Graphics.Metal
{
    internal class MetalFXUpscaler : IDisposable
    {
        private nint _device;
        private nint _scaler;

        public MetalFXUpscaler(nint device, nuint inputWidth, nuint inputHeight, nuint outputWidth, nuint outputHeight, nuint inputColorFormat, nuint outputColorFormat)
        {
            _device = device;
            Initialize(inputWidth, inputHeight, outputWidth, outputHeight, inputColorFormat, outputColorFormat);
        }

        private void Initialize(nuint inputWidth, nuint inputHeight, nuint outputWidth, nuint outputHeight, nuint inputColorFormat, nuint outputColorFormat)
        {
            // Dynamically load MetalFX framework
            nint handle = MetalBindings.dlopen("/System/Library/Frameworks/MetalFX.framework/MetalFX", 1); // RTLD_LAZY
            if (handle == nint.Zero)
            {
                throw new Exception("Could not load MetalFX framework.");
            }

            nint descriptorClass = MetalBindings.objc_getClass("MTLFXSpatialScalerDescriptor");
            if (descriptorClass == nint.Zero)
            {
                throw new Exception("MTLFXSpatialScalerDescriptor class not found.");
            }

            nint selAlloc = MetalBindings.sel_registerName("alloc");
            nint selInit = MetalBindings.sel_registerName("init");

            nint descriptor = MetalBindings.objc_msgSend(descriptorClass, selAlloc);
            descriptor = MetalBindings.objc_msgSend(descriptor, selInit);

            MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelColorTextureFormat, inputColorFormat);
            MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelOutputTextureFormat, outputColorFormat);
            MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelInputWidth, inputWidth);
            MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelInputHeight, inputHeight);
            MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelOutputWidth, outputWidth);
            MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelOutputHeight, outputHeight);

            _scaler = MetalBindings.objc_msgSend(descriptor, MetalBindings.SelNewSpatialScalerWithDevice, _device);
            if (_scaler == nint.Zero)
            {
                throw new Exception($"Failed to create MTLFXSpatialScaler. InputFormat={inputColorFormat}, OutputFormat={outputColorFormat}, InputSize={inputWidth}x{inputHeight}, OutputSize={outputWidth}x{outputHeight}");
            }
            if (_scaler == nint.Zero)
            {
                throw new Exception("Failed to create MTLFXSpatialScaler.");
            }

            MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelRelease);
        }

        public void Encode(nint commandBuffer, nint colorTexture, nint outputTexture)
        {
            if (_scaler != nint.Zero)
            {
                MetalBindings.objc_msgSend_void(_scaler, MetalBindings.SelEncodeToCommandBufferColorTextureOutputTexture, commandBuffer, colorTexture, outputTexture);
            }
        }

        public void Dispose()
        {
            if (_scaler != nint.Zero)
            {
                MetalBindings.objc_msgSend_void(_scaler, MetalBindings.SelRelease);
                _scaler = nint.Zero;
            }
        }
    }
}
