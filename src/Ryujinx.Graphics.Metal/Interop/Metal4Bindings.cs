using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal.Interop
{
    /// <summary>
    /// Metal 4 (macOS 26) bindings: MTL4CommandQueue parallel encoding surface,
    /// MTL4Compiler + MTL4PipelineDataSetSerializer AOT surface and the MTL4ArgumentTable
    /// binding model. Extends the proven objc_msgSend interop pattern.
    ///
    /// Source of truth: MacOSX26.sdk Metal4 headers + Metal.apinotes (Swift names).
    /// Selector names are the Objective-C forms throughout.
    /// </summary>
    [SupportedOSPlatform("macos26.0")]
    public static unsafe partial class Metal4Bindings
    {
        private const string ObjCLib = "/usr/lib/libobjc.A.dylib";

        // ---- objc_msgSend variants needed only by the Metal 4 surface ----
        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial nint m4_msgSend(nint receiver, nint selector);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial nint m4_msgSend(nint receiver, nint selector, ulong arg1);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial nint m4_msgSend(nint receiver, nint selector, nuint arg1, nuint arg2);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial nint m4_msgSend(nint receiver, nint selector, nint arg1, nuint arg2, nint arg3);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial nint m4_msgSend(nint receiver, nint selector, nint arg1, nint arg2);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static unsafe partial nint m4_msgSend(nint receiver, nint selector, void* bytes, nuint length, nuint options);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial nint m4_msgSend(nint receiver, nint selector, nint arg1, nint arg2, nint arg3, nint arg4);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void m4_msgSend_void(nint receiver, nint selector, ulong arg1, nuint arg2);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void m4_msgSend_void(nint receiver, nint selector);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void m4_msgSend_void(nint receiver, nint selector, nint arg1);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void m4_msgSend_void(nint receiver, nint selector, ulong arg1, ulong arg2);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void m4_msgSend_void(nint receiver, nint selector, nint arg1, nuint arg2);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void m4_msgSend_void(nint receiver, nint selector, nuint arg1);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void m4_msgSend_void(nint receiver, nint selector, nint arg1, ulong arg2);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void m4_msgSend_void(nint receiver, nint selector, nuint arg1, nuint arg2, nuint arg3);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void m4_msgSend_void(nint receiver, nint selector, nuint arg1, nuint arg2, nuint arg3, nuint arg4);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void m4_msgSend_void(nint receiver, nint selector, nint arg1, nuint arg2, nuint arg3);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void m4_msgSend_void(nint receiver, nint selector, nint arg1, ulong arg2, nint arg3);

        // drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferLength:instanceCount:
        // indexBuffer is an MTLGPUAddress (ulong); the M4 indexed draw binds the index buffer by
        // GPU address + length rather than by MTLBuffer object + offset.
        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void m4_msgSend_void(nint receiver, nint selector, nuint arg1, nuint arg2, nuint arg3, ulong arg4, nuint arg5, nuint arg6);

        // commit:count: — first arg is a C array of MTL4CommandBuffer ptrs
        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void m4_msgSend_void_array(nint receiver, nint selector, nint* buffers, nuint count);

        // commit:count:options:
        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void m4_msgSend_void_array_opts(nint receiver, nint selector, nint* buffers, nuint count, nint options);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void m4_msgSend_void_block(nint receiver, nint selector, void* block);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool m4_msgSend_bool(nint receiver, nint selector, nint arg1, nint arg2, nint arg3, nint arg4);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool m4_msgSend_bool_2err(nint receiver, nint selector, nint arg1, nint arg2);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool m4_wait_event_bool(nint receiver, nint selector, ulong value, ulong timeoutMS);

        // double return (CFTimeInterval: GPUStartTime / GPUEndTime)
        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial double m4_msgSend_double(nint receiver, nint selector);

        // ---- Factory selectors (device) ----
        public static readonly nint SelNewMTL4CommandQueue = MetalBindings.sel_registerName("newMTL4CommandQueue");
        public static readonly nint SelNewCommandBuffer = MetalBindings.sel_registerName("newCommandBuffer");
        public static readonly nint SelNewCommandAllocator = MetalBindings.sel_registerName("newCommandAllocator");
        public static readonly nint SelNewArgumentTableWithDescriptorError = MetalBindings.sel_registerName("newArgumentTableWithDescriptor:error:");
        public static readonly nint SelNewCompilerWithDescriptorError = MetalBindings.sel_registerName("newCompilerWithDescriptor:error:");
        public static readonly nint SelNewResidencySetWithDescriptorError = MetalBindings.sel_registerName("newResidencySetWithDescriptor:error:");
        public static readonly nint SelNewPipelineDataSetSerializerWithDescriptor = MetalBindings.sel_registerName("newPipelineDataSetSerializerWithDescriptor:");
        public static readonly nint SelNewArchiveWithURLError = MetalBindings.sel_registerName("newArchiveWithURL:error:");

        // ---- MTL4CommandQueue ----
        public static readonly nint SelCommitCount = MetalBindings.sel_registerName("commit:count:");
        public static readonly nint SelCommitCountOptions = MetalBindings.sel_registerName("commit:count:options:");
        public static readonly nint SelAddResidencySetsCount = MetalBindings.sel_registerName("addResidencySets:count:");

        // ---- MTL4ArgumentTableDescriptor / MTL4ArgumentTable ----
        public static readonly nint SelNewBufferWithBytesLengthOptions = MetalBindings.sel_registerName("newBufferWithBytes:length:options:");
        public static readonly nint SelSetMaxBufferBindCount = MetalBindings.sel_registerName("setMaxBufferBindCount:");
        public static readonly nint SelSetMaxTextureBindCount = MetalBindings.sel_registerName("setMaxTextureBindCount:");
        public static readonly nint SelSetMaxSamplerStateBindCount = MetalBindings.sel_registerName("setMaxSamplerStateBindCount:");
        public static readonly nint SelSetAddressAtIndex = MetalBindings.sel_registerName("setAddress:atIndex:");
        public static readonly nint SelSetResourceAtBufferIndex = MetalBindings.sel_registerName("setResource:atBufferIndex:");
        public static readonly nint SelSetTextureAtIndex = MetalBindings.sel_registerName("setTexture:atIndex:");
        public static readonly nint SelSetSamplerStateAtIndex = MetalBindings.sel_registerName("setSamplerState:atIndex:");

        // ---- MTLResidencySet ----
        public static readonly nint SelAddAllocation = MetalBindings.sel_registerName("addAllocation:");
        public static readonly nint SelRemoveAllocation = MetalBindings.sel_registerName("removeAllocation:");
        public static readonly nint SelCommitResidencySet = MetalBindings.sel_registerName("commit");
        public static readonly nint SelRequestResidency = MetalBindings.sel_registerName("requestResidency");

        // ---- MTL4Compiler / descriptors ----
        public static readonly nint SelNewLibraryWithDescriptorError = MetalBindings.sel_registerName("newLibraryWithDescriptor:error:");
        public static readonly nint SelNewRenderPipelineStateWithDescriptorCompilerTaskOptionsError = MetalBindings.sel_registerName("newRenderPipelineStateWithDescriptor:compilerTaskOptions:error:");
        public static readonly nint SelSetConfiguration = MetalBindings.sel_registerName("setConfiguration:");
        public static readonly nint SelSetPipelineDataSetSerializer = MetalBindings.sel_registerName("setPipelineDataSetSerializer:");
        public static readonly nint SelSetSource = MetalBindings.sel_registerName("setSource:");
        public static readonly nint SelSetOptions = MetalBindings.sel_registerName("setOptions:");
        public static readonly nint SelSetName = MetalBindings.sel_registerName("setName:");
        public static readonly nint SelSetLibrary = MetalBindings.sel_registerName("setLibrary:");
        public static readonly nint SelSetLanguageVersion = MetalBindings.sel_registerName("setLanguageVersion:");
        public static readonly nint SelSerializeAsPipelinesScriptWithError = MetalBindings.sel_registerName("serializeAsPipelinesScriptWithError:");
        public static readonly nint SelSerializeAsArchiveAndFlushToURLError = MetalBindings.sel_registerName("serializeAsArchiveAndFlushToURL:error:");
        public static readonly nint SelSetVertexFunctionDescriptor = MetalBindings.sel_registerName("setVertexFunctionDescriptor:");
        public static readonly nint SelSetFragmentFunctionDescriptor = MetalBindings.sel_registerName("setFragmentFunctionDescriptor:");

        // ---- MTL4CommandBuffer / render encoder ----
        public static readonly nint SelBeginCommandBufferWithAllocator = MetalBindings.sel_registerName("beginCommandBufferWithAllocator:");
        public static readonly nint SelEndCommandBuffer = MetalBindings.sel_registerName("endCommandBuffer");
        public static readonly nint SelRenderCommandEncoderWithDescriptor = MetalBindings.sel_registerName("renderCommandEncoderWithDescriptor:");
        public static readonly nint SelRenderCommandEncoderWithDescriptorOptions = MetalBindings.sel_registerName("renderCommandEncoderWithDescriptor:options:");
        public static readonly nint SelSetRenderPipelineState = MetalBindings.sel_registerName("setRenderPipelineState:");
        public static readonly nint SelSetArgumentTableAtStages = MetalBindings.sel_registerName("setArgumentTable:atStages:");
        public static readonly nint SelSetArgumentTableCompute = MetalBindings.sel_registerName("setArgumentTable:");
        public static readonly nint SelDrawPrimitivesVertexStartVertexCount = MetalBindings.sel_registerName("drawPrimitives:vertexStart:vertexCount:");
        public static readonly nint SelDrawIndexedPrimitivesIndexCountIndexTypeIndexBufferLengthInstanceCount = MetalBindings.sel_registerName("drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferLength:instanceCount:");

        // ---- commit feedback ----
        public static readonly nint SelAddFeedbackHandler = MetalBindings.sel_registerName("addFeedbackHandler:");
        public static readonly nint SelGPUStartTime = MetalBindings.sel_registerName("GPUStartTime");
        public static readonly nint SelGPUEndTime = MetalBindings.sel_registerName("GPUEndTime");
        public static readonly nint SelError = MetalBindings.sel_registerName("error");

        // ---- MTLSharedEvent block-free CPU/GPU sync ----
        public static readonly nint SelNewSharedEvent = MetalBindings.sel_registerName("newSharedEvent");
        public static readonly nint SelSignalEventValue = MetalBindings.sel_registerName("signalEvent:value:");
        public static readonly nint SelWaitUntilSignaledValueTimeoutMS = MetalBindings.sel_registerName("waitUntilSignaledValue:timeoutMS:");

        // ---- MTL4 render pass descriptor / attachment ----
        public static readonly nint SelColorAttachments = MetalBindings.sel_registerName("colorAttachments");
        public static readonly nint SelSetBlendingState = MetalBindings.sel_registerName("setBlendingState:");
        public static readonly nint SelObjectAtIndexedSubscript = MetalBindings.sel_registerName("objectAtIndexedSubscript:");
        public static readonly nint SelSetPixelFormat = MetalBindings.sel_registerName("setPixelFormat:");
        public static readonly nint SelSetTexture = MetalBindings.sel_registerName("setTexture:");
        public static readonly nint SelSetLoadAction = MetalBindings.sel_registerName("setLoadAction:");
        public static readonly nint SelSetStoreAction = MetalBindings.sel_registerName("setStoreAction:");
        public static readonly nint SelSetClearColor = MetalBindings.sel_registerName("setClearColor:");
        public static readonly nint SelEndEncoding = MetalBindings.sel_registerName("endEncoding");

        // =====================================================================
        // Constants (verified against MacOSX26.sdk Metal4 headers)
        // =====================================================================

        // MTLLanguageVersion
        public const ulong MTLLanguageVersion4_0 = (4UL << 16);

        /// <summary>
        /// Convenience: creates a class-allocated instance of a Metal 4 descriptor
        /// (e.g. MTL4LibraryDescriptor) via <c>[[Cls new]]</c>.
        /// </summary>
        public static nint Metal4New(string className)
        {
            return MetalBindings.objc_msgSend(MetalBindings.objc_getClass(className), MetalBindings.SelNew);
        }

        // MTL4PipelineDataSetSerializerConfiguration
        public const ulong M4CaptureDescriptors = 1UL << 0;
        public const ulong M4CaptureBinaries   = 1UL << 1;

        // MTLRenderStages (setArgumentTable:atStages:)
        public const ulong MTLRenderStageVertex   = 1UL << 0;
        public const ulong MTLRenderStageFragment = 1UL << 1;

        // MTL4RenderEncoderOptions
        public const ulong M4RenderEncoderOptionNone       = 0;
        public const ulong M4RenderEncoderOptionSuspending = 1UL << 0;
        public const ulong M4RenderEncoderOptionResuming   = 1UL << 1;

        // MTL4BlendState
        public const ulong MTL4BlendStateDisabled = 0;
        public const ulong MTL4BlendStateEnabled = 1;

        // MTLLoadAction / MTLStoreAction
        public const ulong MTLLoadActionClear = 2;
        public const ulong MTLStoreActionStore = 1;

        // MTLPrimitiveType
        public const ulong MTLPrimitiveTypeTriangle = 3;

        // MTLResourceID accessors
        public static readonly nint SelGpuResourceID = MetalBindings.sel_registerName("gpuResourceID");
        public static readonly nint SelGpuAddress = MetalBindings.sel_registerName("gpuAddress");
    }

    /// <summary>
    /// Opaque pointer type used when passing a C array of MTL4CommandBuffer objects
    /// to commit:count:.
    /// </summary>
    [SupportedOSPlatform("macos26.0")]
    public unsafe struct MTL4CommandBufferPtr
    {
        public nint Handle;
    }

    /// <summary>
    /// MTLResourceID — 64-bit opaque struct returned from gpuResourceID by reference.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MTLResourceID
    {
        public ulong _impl;
    }
}