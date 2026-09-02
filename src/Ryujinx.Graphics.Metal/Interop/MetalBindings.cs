using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal.Interop
{
    [SupportedOSPlatform("macos")]
    public static partial class MetalBindings
    {
        private const string MetalLib = "/System/Library/Frameworks/Metal.framework/Metal";
        private const string ObjCLib = "/usr/lib/libobjc.A.dylib";

        [LibraryImport("libSystem.dylib", StringMarshalling = StringMarshalling.Utf8)]
        public static partial nint dlopen(string path, int mode);

        [LibraryImport(MetalLib)]
        public static partial nint MTLCreateSystemDefaultDevice();

        [LibraryImport(ObjCLib, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
        public static partial nint sel_registerName(string name);

        [LibraryImport(ObjCLib, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
        public static partial nint objc_getClass(string name);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial nint objc_msgSend(nint receiver, nint selector);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial nint objc_msgSend(nint receiver, nint selector, nint arg1);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial nint objc_msgSend(nint receiver, nint selector, nint arg1, nint arg2);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial nint objc_msgSend(nint receiver, nint selector, ulong arg1);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial nint objc_msgSend(nint receiver, nint selector, nuint arg1, nuint arg2);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial nint objc_msgSend(nint receiver, nint selector, nint arg1, nuint arg2, nuint arg3, nint arg4);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void objc_msgSend_void(nint receiver, nint selector);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void objc_msgSend_void(nint receiver, nint selector, nint arg1);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void objc_msgSend_void(nint receiver, nint selector, nint arg1, nint arg2, nint arg3);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void objc_msgSend_void(nint receiver, nint selector, nint arg1, nuint arg2);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void objc_msgSend_void(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool arg1);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void objc_msgSend_void(nint receiver, nint selector, nuint arg1, nuint arg2, nuint arg3);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void objc_msgSend_void(nint receiver, nint selector, nint arg1, nuint arg2, nuint arg3, byte arg4);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool objc_msgSend_bool(nint receiver, nint selector);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool objc_msgSend_bool(nint receiver, nint selector, nint arg1);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial ulong objc_msgSend_ulong_ret(nint receiver, nint selector);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial nint objc_msgSend(nint receiver, nint selector, nuint arg1);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
        public static partial nint objc_msgSend(nint receiver, nint selector, string arg1);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial nint objc_msgSend(nint receiver, nint selector, nint arg1, nint arg2, nint arg3);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial nint objc_msgSend(nint receiver, nint selector, nint arg1, nint arg2, nint arg3, nint arg4);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial nint objc_msgSend(nint receiver, nint selector, nuint arg1, nuint arg2, nuint arg3, byte arg4);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial nint objc_msgSend(nint receiver, nint selector, nuint arg1, nuint arg2, byte arg3);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static unsafe partial void objc_msgSend_void(nint receiver, nint selector, MTLRegion* region, nuint level, void* bytes, nuint bytesPerRow);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static unsafe partial void objc_msgSend_void(nint receiver, nint selector, void* bytes, nuint bytesPerRow, nuint bytesPerImage, MTLRegion* region, nuint level);

        // 2D texture readback: getBytes:bytesPerRow:fromRegion:mipmapLevel:
        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static unsafe partial void objc_msgSend_void(nint receiver, nint selector, void* bytes, nuint bytesPerRow, MTLRegion* region, nuint level);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static unsafe partial void objc_msgSend_void(nint receiver, nint selector, nint source, nuint srcSlice, nuint srcLevel, MTLOrigin* srcOrigin, MTLSize* srcSize, nint dest, nuint dstSlice, nuint dstLevel, MTLOrigin* dstOrigin);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void objc_msgSend_void(nint receiver, nint selector, nuint arg1);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void objc_msgSend_void(nint receiver, nint selector, nint arg1, double arg2, double arg3);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void objc_msgSend_void(nint receiver, nint selector, double arg1, double arg2);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void objc_msgSend_void(nint receiver, nint selector, double arg1);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void objc_msgSend_void(nint receiver, nint selector, nint arg1, nuint arg2, nuint arg3);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static unsafe partial void objc_msgSend_void(nint receiver, nint selector, MTLColor* color);

        // M3b: MTLBinaryArchive — newRenderPipelineStateWithDescriptor:options:reflection:error:
        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial nint objc_msgSend(nint receiver, nint selector, nint arg1, ulong arg2, nint arg3, nint arg4);

        // M6: encodeSignalEvent:value: / encodeWaitForEvent:value: / signalEvent:value:
        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void objc_msgSend_void(nint receiver, nint selector, nint arg1, ulong arg2);

        // NSData construction: dataWithBytes:length:
        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static unsafe partial nint objc_msgSend(nint receiver, nint selector, void* arg1, nuint arg2);

        // M4: render encoder state
        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static unsafe partial void objc_msgSend_void(nint receiver, nint selector, MTLViewport* viewport);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static unsafe partial void objc_msgSend_void(nint receiver, nint selector, MTLScissorRect* scissor);

        // M6: array/cube texture upload — replaceRegion:mipmapLevel:slice:withBytes:bytesPerRow:bytesPerImage:
        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static unsafe partial void objc_msgSend_void(nint receiver, nint selector, MTLRegion* region, nuint level, nuint slice, void* bytes, nuint bytesPerRow, nuint bytesPerImage);

        // M6: array/cube texture readback — getBytes:bytesPerRow:bytesPerImage:fromRegion:mipmapLevel:slice:
        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static unsafe partial void objc_msgSend_void(nint receiver, nint selector, void* bytes, nuint bytesPerRow, nuint bytesPerImage, MTLRegion* region, nuint level, nuint slice);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static unsafe partial void objc_msgSend_void(nint receiver, nint selector, void* bytes, nuint length, nuint index);

        // Blit encoder: copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toTexture:destinationSlice:destinationLevel:destinationOrigin:
        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static unsafe partial void objc_msgSend_void_blitCopy(nint receiver, nint selector,
            nint srcTexture, nuint srcSlice, nuint srcLevel, MTLOrigin srcOrigin, MTLSize srcSize,
            nint dstTexture, nuint dstSlice, nuint dstLevel, MTLOrigin dstOrigin);


        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void objc_msgSend_void(nint receiver, nint selector, nuint arg1, nuint arg2, nuint arg3, nuint arg4);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void objc_msgSend_void(nint receiver, nint selector, nuint indexCount, nuint indexType, nint indexBuffer, nuint indexBufferOffset, nuint instanceCount);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void objc_msgSend_void(nint receiver, nint selector, nuint primitiveType, nuint indexCount, nuint indexType, nint indexBuffer, nuint indexBufferOffset, nuint instanceCount);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static unsafe partial void objc_msgSend_void(nint receiver, nint selector, MTLSize* threadgroups, MTLSize* threadsPerThreadgroup);

        // Selector Cache
        public static readonly nint SelNewCommandQueue = sel_registerName("newCommandQueue");
        public static readonly nint SelCommandBuffer = sel_registerName("commandBuffer");
        public static readonly nint SelCommandBufferWithUnretainedReferences = sel_registerName("commandBufferWithUnretainedReferences");
        public static readonly nint SelBlitCommandEncoder = sel_registerName("blitCommandEncoder");
        public static readonly nint SelFillBufferRangeValue = sel_registerName("fillBuffer:range:value:");
        public static readonly nint SelCopyFromTextureToTexture = sel_registerName("copyFromTexture:toTexture:");
        public static readonly nint SelCopyFromTextureSourceSliceSourceLevelSourceOriginSourceSizeToTextureDestinationSliceDestinationLevelDestinationOrigin = sel_registerName("copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:toTexture:destinationSlice:destinationLevel:destinationOrigin:");
        public static readonly nint SelCommit = sel_registerName("commit");
        public static readonly nint SelWaitUntilCompleted = sel_registerName("waitUntilCompleted");
        public static readonly nint SelWaitUntilScheduled = sel_registerName("waitUntilScheduled");
        public static readonly nint SelStatus = sel_registerName("status");
        public static readonly nint SelNewBufferWithLengthOptions = sel_registerName("newBufferWithLength:options:");
        public static readonly nint SelNewBufferWithBytesNoCopyOptions = sel_registerName("newBufferWithBytesNoCopy:length:options:deallocator:");
        public static readonly nint SelContents = sel_registerName("contents");
        public static readonly nint SelLength = sel_registerName("length");
        public static readonly nint SelEndEncoding = sel_registerName("endEncoding");
        public static readonly nint SelName = sel_registerName("name");
        public static readonly nint SelRelease = sel_registerName("release");
        public static readonly nint SelRetain = sel_registerName("retain");

        // CAMetalLayer / presentation surface (M2)
        public static readonly nint SelNew = sel_registerName("new");
        public static readonly nint SelSetDevice = sel_registerName("setDevice:");        public static readonly nint SelSetPixelFormat = sel_registerName("setPixelFormat:");
        public static readonly nint SelSetDrawableSize = sel_registerName("setDrawableSize:");
        public static readonly nint SelSetFramebufferOnly = sel_registerName("setFramebufferOnly:");
        public static readonly nint SelNextDrawable = sel_registerName("nextDrawable");
        public static readonly nint SelTexture = sel_registerName("texture");
        public static readonly nint SelRenderPassDescriptor = sel_registerName("renderPassDescriptor");
        public static readonly nint SelColorAttachments = sel_registerName("colorAttachments");
        public static readonly nint SelDepthAttachment = sel_registerName("depthAttachment");
        public static readonly nint SelObjectAtIndexedSubscript = sel_registerName("objectAtIndexedSubscript:");
        public static readonly nint SelSetTexture = sel_registerName("setTexture:");
        public static readonly nint SelSetLoadAction = sel_registerName("setLoadAction:");
        public static readonly nint SelSetStoreAction = sel_registerName("setStoreAction:");
        public static readonly nint SelSetClearColor = sel_registerName("setClearColor:");
        public static readonly nint SelSetClearDepth = sel_registerName("setClearDepth:");
        public static readonly nint SelSetClearStencil = sel_registerName("setClearStencil:");
        public static readonly nint SelRenderCommandEncoderWithDescriptor = sel_registerName("renderCommandEncoderWithDescriptor:");
        public static readonly nint SelPresentDrawable = sel_registerName("presentDrawable:");

        // M3: shader library + render pipeline surface
        public static readonly nint SelStringWithUTF8String = sel_registerName("stringWithUTF8String:");
        public static readonly nint SelUTF8String = sel_registerName("UTF8String");
        public static readonly nint SelLocalizedDescription = sel_registerName("localizedDescription");
        public static readonly nint SelNewLibraryWithSourceOptionsError = sel_registerName("newLibraryWithSource:options:error:");
        public static readonly nint SelNewFunctionWithName = sel_registerName("newFunctionWithName:");
        public static readonly nint SelRenderPipelineDescriptor = sel_registerName("renderPipelineDescriptor");
        public static readonly nint SelNewRenderPipelineStateWithDescriptorError = sel_registerName("newRenderPipelineStateWithDescriptor:error:");
        public static readonly nint SelSetVertexFunction = sel_registerName("setVertexFunction:");
        public static readonly nint SelSetFragmentFunction = sel_registerName("setFragmentFunction:");
        public static readonly nint SelSetRenderPipelineState = sel_registerName("setRenderPipelineState:");
        public static readonly nint SelDrawPrimitivesVertexStartVertexCount = sel_registerName("drawPrimitives:vertexStart:vertexCount:");

        // M3b: texture surface
        public static readonly nint SelTexture2DDescriptorWithPixelFormatWidthHeightMipmapped = sel_registerName("texture2DDescriptorWithPixelFormat:width:height:mipmapped:");
        public static readonly nint SelNewTextureWithDescriptor = sel_registerName("newTextureWithDescriptor:");
        public static readonly nint SelNewTextureViewWithPixelFormat = sel_registerName("newTextureViewWithPixelFormat:");
        public static readonly nint SelSetUsage = sel_registerName("setUsage:");
        public static readonly nint SelSetTextureType = sel_registerName("setTextureType:");
        public static readonly nint SelSetArrayLength = sel_registerName("setArrayLength:");
        public static readonly nint SelSetDepth = sel_registerName("setDepth:");
        public static readonly nint SelSetWidth = sel_registerName("setWidth:");
        public static readonly nint SelSetMipmapLevelCount = sel_registerName("setMipmapLevelCount:");
        public static readonly nint SelSetSampleCount = sel_registerName("setSampleCount:");
        public static readonly nint SelSetStorageMode = sel_registerName("setStorageMode:");
        public static readonly nint SelSetHeight = sel_registerName("setHeight:");
        public static readonly nint SelReplaceRegionMipmapLevelWithBytesBytesPerRow = sel_registerName("replaceRegion:mipmapLevel:withBytes:bytesPerRow:");
        public static readonly nint SelReplaceRegionMipmapLevelSliceWithBytesBytesPerRowBytesPerImage = sel_registerName("replaceRegion:mipmapLevel:slice:withBytes:bytesPerRow:bytesPerImage:");
        public static readonly nint SelGetBytesBytesPerRowBytesPerImageFromRegionMipmapLevelSlice = sel_registerName("getBytes:bytesPerRow:bytesPerImage:fromRegion:mipmapLevel:slice:");
        public static readonly nint SelGetBytesBytesPerRowBytesPerImageFromRegionMipmapLevel = sel_registerName("getBytes:bytesPerRow:bytesPerImage:fromRegion:mipmapLevel:");
        public static readonly nint SelGetBytesBytesPerRowFromRegionMipmapLevel = sel_registerName("getBytes:bytesPerRow:fromRegion:mipmapLevel:");


        // M3b: MTLBinaryArchive persistent pipeline cache
        public static readonly nint SelNewBinaryArchiveWithDescriptorError = sel_registerName("newBinaryArchiveWithDescriptor:error:");
        public static readonly nint SelAddRenderPipelineFunctionsWithDescriptorError = sel_registerName("addRenderPipelineFunctionsWithDescriptor:error:");
        public static readonly nint SelSerializeToDataError = sel_registerName("serializeToData:error:");
        public static readonly nint SelSerializeToURLError = sel_registerName("serializeToURL:error:");
        public static readonly nint SelFileURLWithPath = sel_registerName("fileURLWithPath:");
        public static readonly nint SelRespondsToSelector = sel_registerName("respondsToSelector:");
        public static readonly nint SelSetData = sel_registerName("setData:");
        public static readonly nint SelSetUrl = sel_registerName("setUrl:");
        public static readonly nint SelBytes = sel_registerName("bytes");
        public static readonly nint SelNewRenderPipelineStateWithDescriptorOptionsReflectionError = sel_registerName("newRenderPipelineStateWithDescriptor:options:reflection:error:");
        public static readonly nint SelSetBinaryArchives = sel_registerName("setBinaryArchives:");
        public static readonly nint SelArrayWithObject = sel_registerName("arrayWithObject:");
        public static readonly nint SelDataWithBytesLength = sel_registerName("dataWithBytes:length:");

        // M4: render encoder state + draw
        public static readonly nint SelSetViewport = sel_registerName("setViewport:");
        public static readonly nint SelSetScissorRect = sel_registerName("setScissorRect:");
        public static readonly nint SelSetCullMode = sel_registerName("setCullMode:");
        public static readonly nint SelSetFrontFacingWinding = sel_registerName("setFrontFacingWinding:");
        public static readonly nint SelSetTriangleFillMode = sel_registerName("setTriangleFillMode:");
        public static readonly nint SelSetDepthStencilState = sel_registerName("setDepthStencilState:");
        public static readonly nint SelSetVertexBufferOffsetAtIndex = sel_registerName("setVertexBuffer:offset:atIndex:");
        public static readonly nint SelSetFragmentBufferOffsetAtIndex = sel_registerName("setFragmentBuffer:offset:atIndex:");
        public static readonly nint SelSetComputeBufferOffsetAtIndex = sel_registerName("setBuffer:offset:atIndex:");
        public static readonly nint SelSetVertexBytesLengthAtIndex = sel_registerName("setVertexBytes:length:atIndex:");
        public static readonly nint SelSetFragmentBytesLengthAtIndex = sel_registerName("setFragmentBytes:length:atIndex:");

        // MetalFX Selectors
        public static readonly nint SelColorTextureFormat = sel_registerName("setColorTextureFormat:");
        public static readonly nint SelOutputTextureFormat = sel_registerName("setOutputTextureFormat:");
        public static readonly nint SelInputWidth = sel_registerName("setInputWidth:");
        public static readonly nint SelInputHeight = sel_registerName("setInputHeight:");
        public static readonly nint SelOutputWidth = sel_registerName("setOutputWidth:");
        public static readonly nint SelOutputHeight = sel_registerName("setOutputHeight:");
        public static readonly nint SelNewSpatialScalerWithDevice = sel_registerName("newSpatialScalerWithDevice:");
        public static readonly nint SelEncodeToCommandBufferColorTextureOutputTexture = sel_registerName("encodeToCommandBuffer:colorTexture:outputTexture:");
        public static readonly nint SelSetVertexTextureAtIndex = sel_registerName("setVertexTexture:atIndex:");
        public static readonly nint SelSetFragmentTextureAtIndex = sel_registerName("setFragmentTexture:atIndex:");
        public static readonly nint SelSetVertexSamplerStateAtIndex = sel_registerName("setVertexSamplerState:atIndex:");
        public static readonly nint SelSetFragmentSamplerStateAtIndex = sel_registerName("setFragmentSamplerState:atIndex:");
        public static readonly nint SelSetIndexBufferOffsetIndexType = sel_registerName("setIndexBuffer:offset:indexType:");
        public static readonly nint SelDrawPrimitivesVertexStartVertexCountInstanceCount = sel_registerName("drawPrimitives:vertexStart:vertexCount:instanceCount:");
        public static readonly nint SelDrawIndexedPrimitivesIndexCountIndexTypeIndexBufferIndexBufferOffsetInstanceCount = sel_registerName("drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferOffset:instanceCount:");

        // M4: compute
        public static readonly nint SelComputeCommandEncoder = sel_registerName("computeCommandEncoder");
        public static readonly nint SelSetComputePipelineState = sel_registerName("setComputePipelineState:");
        public static readonly nint SelDispatchThreadgroupsThreadsPerThreadgroup = sel_registerName("dispatchThreadgroups:threadsPerThreadgroup:");
        public static readonly nint SelNewComputePipelineStateWithFunctionError = sel_registerName("newComputePipelineStateWithFunction:error:");

        // M4: render pipeline descriptor (vertex layout, depth/stencil, topology)
        public static readonly nint SelSetVertexDescriptor = sel_registerName("setVertexDescriptor:");
        public static readonly nint SelSetDepthAttachmentPixelFormat = sel_registerName("setDepthAttachmentPixelFormat:");
        public static readonly nint SelSetStencilAttachmentPixelFormat = sel_registerName("setStencilAttachmentPixelFormat:");
        public static readonly nint SelSetInputPrimitiveTopology = sel_registerName("setInputPrimitiveTopology:");
        public static readonly nint SelSetLabel = sel_registerName("setLabel:");
        public static readonly nint SelVertexDescriptor = sel_registerName("vertexDescriptor");
        public static readonly nint SelAttributes = sel_registerName("attributes");
        public static readonly nint SelLayouts = sel_registerName("layouts");
        public static readonly nint SelSetVertexFormat = sel_registerName("setFormat:");
        public static readonly nint SelSetOffset = sel_registerName("setOffset:");
        public static readonly nint SelSetBufferIndex = sel_registerName("setBufferIndex:");
        public static readonly nint SelSetStride = sel_registerName("setStride:");
        public static readonly nint SelSetStepFunction = sel_registerName("setStepFunction:");
        public static readonly nint SelSetStepRate = sel_registerName("setStepRate:");

        // M4: color attachment blend state
        public static readonly nint SelSetBlendingEnabled = sel_registerName("setBlendingEnabled:");
        public static readonly nint SelSetRgbBlendOperation = sel_registerName("setRgbBlendOperation:");
        public static readonly nint SelSetAlphaBlendOperation = sel_registerName("setAlphaBlendOperation:");
        public static readonly nint SelSetSourceRGBBlendFactor = sel_registerName("setSourceRGBBlendFactor:");
        public static readonly nint SelSetDestinationRGBBlendFactor = sel_registerName("setDestinationRGBBlendFactor:");
        public static readonly nint SelSetSourceAlphaBlendFactor = sel_registerName("setSourceAlphaBlendFactor:");
        public static readonly nint SelSetDestinationAlphaBlendFactor = sel_registerName("setDestinationAlphaBlendFactor:");
        public static readonly nint SelSetWriteMask = sel_registerName("setWriteMask:");
        public static readonly nint SelSetAlphaToCoverageEnabled = sel_registerName("setAlphaToCoverageEnabled:");

        // M4: depth stencil state
        public static readonly nint SelNewDepthStencilStateWithDescriptor = sel_registerName("newDepthStencilStateWithDescriptor:");
        public static readonly nint SelDepthStencilDescriptor = sel_registerName("depthStencilDescriptor");
        public static readonly nint SelSetDepthCompareFunction = sel_registerName("setDepthCompareFunction:");
        public static readonly nint SelSetDepthWriteEnabled = sel_registerName("setDepthWriteEnabled:");

        // M4: sampler state
        public static readonly nint SelNewSamplerStateWithDescriptor = sel_registerName("newSamplerStateWithDescriptor:");
        public static readonly nint SelSamplerDescriptor = sel_registerName("samplerDescriptor");
        public static readonly nint SelSetMinFilter = sel_registerName("setMinFilter:");
        public static readonly nint SelSetMagFilter = sel_registerName("setMagFilter:");
        public static readonly nint SelSetSAddressMode = sel_registerName("setSAddressMode:");
        public static readonly nint SelSetTAddressMode = sel_registerName("setTAddressMode:");
        public static readonly nint SelSetMipFilter = sel_registerName("setMipFilter:");
        public static readonly nint SelSetMaxAnisotropy = sel_registerName("setMaxAnisotropy:");

        // M5/M6: command pool + sync (MTLEvent / MTLSharedEvent)
        public static readonly nint SelNewCommandQueueWithMaxCommandBufferCount = sel_registerName("newCommandQueueWithMaxCommandBufferCount:");
        public static readonly nint SelNewEvent = sel_registerName("newEvent");
        public static readonly nint SelNewSharedEvent = sel_registerName("newSharedEvent");
        public static readonly nint SelEncodeSignalEventValue = sel_registerName("encodeSignalEvent:value:");
        public static readonly nint SelEncodeWaitForEventValue = sel_registerName("encodeWaitForEvent:value:");
        public static readonly nint SelSignalEventValue = sel_registerName("signalEvent:value:");

        /// <summary>
        /// Retains an Objective-C object. Callers own a reference and must pair it with
        /// <see cref="Release"/>. Returns the object (or 0 when the input is 0).
        /// </summary>
        public static nint Retain(nint obj)
        {
            return obj != 0 ? objc_msgSend(obj, SelRetain) : 0;
        }

        /// <summary>
        /// Releases a previously retained Objective-C object reference.
        /// </summary>
        public static void Release(nint obj)
        {
            if (obj != 0)
            {
                objc_msgSend_void(obj, SelRelease);
            }
        }

        /// <summary>
        /// Creates an autoreleased Objective-C NSString from a managed UTF-8 string.
        /// </summary>
        public static nint CreateNSString(string value)
        {
            return objc_msgSend(objc_getClass("NSString"), SelStringWithUTF8String, value);
        }

        /// <summary>
        /// Converts an NSError's localized description to managed text. This is vital
        /// for Metal compilation diagnostics; a nil library alone gives no actionable
        /// reason for a rejected MSL program.
        /// </summary>
        public static string GetErrorDescription(nint error)
        {
            if (error == nint.Zero)
            {
                return "no NSError was returned";
            }

            nint description = objc_msgSend(error, SelLocalizedDescription);
            nint utf8 = description == nint.Zero ? nint.Zero : objc_msgSend(description, SelUTF8String);

            return utf8 == nint.Zero ? "NSError had no localized description" : Marshal.PtrToStringUTF8(utf8) ?? "NSError description was not UTF-8";
        }

        /// <summary>
        /// Creates an autoreleased Objective-C NSData copy from a managed byte span.
        /// </summary>
        public static unsafe nint CreateNSData(ReadOnlySpan<byte> data)
        {
            fixed (byte* p = data)
            {
                return objc_msgSend(objc_getClass("NSData"), SelDataWithBytesLength, p, (nuint)data.Length);
            }
        }

        // Metal Constants
        public const ulong MTLResourceStorageModeShared = 0;
        public const ulong MTLResourceCPUCacheModeDefaultCache = 0;
        public const ulong MTLCommandBufferStatusCompleted = 4;
        public const ulong MTLPixelFormatRGBA8Unorm = 70;
        public const ulong MTLPixelFormatRGBA8Srgb = 71;
        public const ulong MTLPixelFormatRGBA8Snorm = 72;
        public const ulong MTLPixelFormatRGBA8Uint = 73;
        public const ulong MTLPixelFormatRGBA8Sint = 74;
        public const ulong MTLPixelFormatBGRA8Unorm = 80;
        public const ulong MTLPixelFormatBGRA8Srgb = 81;
        // R8
        public const ulong MTLPixelFormatR8Unorm = 10;
        public const ulong MTLPixelFormatR8Snorm = 12;
        public const ulong MTLPixelFormatR8Uint = 13;
        public const ulong MTLPixelFormatR8Sint = 14;
        // RG8
        public const ulong MTLPixelFormatRG8Unorm = 30;
        public const ulong MTLPixelFormatRG8Snorm = 32;
        public const ulong MTLPixelFormatRG8Uint = 33;
        public const ulong MTLPixelFormatRG8Sint = 34;
        // R16
        public const ulong MTLPixelFormatR16Unorm = 20;
        public const ulong MTLPixelFormatR16Snorm = 22;
        public const ulong MTLPixelFormatR16Uint = 23;
        public const ulong MTLPixelFormatR16Sint = 24;
        public const ulong MTLPixelFormatR16Float = 25;
        // RG16
        public const ulong MTLPixelFormatRG16Unorm = 40;
        public const ulong MTLPixelFormatRG16Snorm = 42;
        public const ulong MTLPixelFormatRG16Uint = 43;
        public const ulong MTLPixelFormatRG16Sint = 44;
        public const ulong MTLPixelFormatRG16Float = 45;
        // RGBA16
        public const ulong MTLPixelFormatRGBA16Unorm = 100;
        public const ulong MTLPixelFormatRGBA16Snorm = 102;
        public const ulong MTLPixelFormatRGBA16Uint = 103;
        public const ulong MTLPixelFormatRGBA16Sint = 104;
        public const ulong MTLPixelFormatRGBA16Float = 110;
        // R32
        public const ulong MTLPixelFormatR32Uint = 53;
        public const ulong MTLPixelFormatR32Sint = 54;
        public const ulong MTLPixelFormatR32Float = 55;
        // RG32
        public const ulong MTLPixelFormatRG32Uint = 73;
        public const ulong MTLPixelFormatRG32Sint = 74;
        public const ulong MTLPixelFormatRG32Float = 75;
        // RGBA32
        public const ulong MTLPixelFormatRGBA32Float = 121;
        public const ulong MTLPixelFormatRGBA32Uint = 123;
        public const ulong MTLPixelFormatRGBA32Sint = 122;
        // Packed
        public const ulong MTLPixelFormatRGB10A2Unorm = 90;
        public const ulong MTLPixelFormatRGB10A2Uint = 91;
        public const ulong MTLPixelFormatRG11B10Float = 92;   // R11G11B10Float
        public const ulong MTLPixelFormatRGB9E5Float = 93;
        public const ulong MTLPixelFormatBGR10A2Unorm = 94;
        public const ulong MTLPixelFormatA1BGR5Unorm = 42;
        public const ulong MTLPixelFormatABGR4Unorm = 43;
        public const ulong MTLPixelFormatB5G6R5Unorm = 40;
        public const ulong MTLPixelFormatB5G5R5A1Unorm = 41;
        // Depth/Stencil
        public const ulong MTLPixelFormatDepth16Unorm = 250;
        public const ulong MTLPixelFormatDepth32Float = 252;
        public const ulong MTLPixelFormatStencil8 = 253;
        public const ulong MTLPixelFormatDepth24UnormStencil8 = 255;
        public const ulong MTLPixelFormatDepth32FloatStencil8 = 260;

        public const ulong MTLPixelFormatBC1_RGBA = 130;
        public const ulong MTLPixelFormatBC1_RGBA_sRGB = 131;
        public const ulong MTLPixelFormatBC2_RGBA = 132;
        public const ulong MTLPixelFormatBC2_RGBA_sRGB = 133;
        public const ulong MTLPixelFormatBC3_RGBA = 134;
        public const ulong MTLPixelFormatBC3_RGBA_sRGB = 135;
        public const ulong MTLPixelFormatBC4_RUnorm = 140;
        public const ulong MTLPixelFormatBC4_RSnorm = 141;
        public const ulong MTLPixelFormatBC5_RGUnorm = 142;
        public const ulong MTLPixelFormatBC5_RGSnorm = 143;
        public const ulong MTLPixelFormatBC6H_RGBFloat = 150;
        public const ulong MTLPixelFormatBC6H_RGBUfloat = 151;
        public const ulong MTLPixelFormatBC7_RGBAUnorm = 152;
        public const ulong MTLPixelFormatBC7_RGBAUnorm_sRGB = 153;

        public const ulong MTLPixelFormatASTC_4x4_sRGB = 186;
        public const ulong MTLPixelFormatASTC_4x4_LDR = 204;
        public const ulong MTLPixelFormatASTC_5x4_sRGB = 187;
        public const ulong MTLPixelFormatASTC_5x4_LDR = 205;
        public const ulong MTLPixelFormatASTC_5x5_sRGB = 188;
        public const ulong MTLPixelFormatASTC_5x5_LDR = 206;
        public const ulong MTLPixelFormatASTC_6x5_sRGB = 189;
        public const ulong MTLPixelFormatASTC_6x5_LDR = 207;
        public const ulong MTLPixelFormatASTC_6x6_sRGB = 190;
        public const ulong MTLPixelFormatASTC_6x6_LDR = 208;
        public const ulong MTLPixelFormatASTC_8x5_sRGB = 192;
        public const ulong MTLPixelFormatASTC_8x5_LDR = 210;
        public const ulong MTLPixelFormatASTC_8x6_sRGB = 193;
        public const ulong MTLPixelFormatASTC_8x6_LDR = 211;
        public const ulong MTLPixelFormatASTC_8x8_sRGB = 194;
        public const ulong MTLPixelFormatASTC_8x8_LDR = 212;
        public const ulong MTLPixelFormatASTC_10x5_sRGB = 195;
        public const ulong MTLPixelFormatASTC_10x5_LDR = 213;
        public const ulong MTLPixelFormatASTC_10x6_sRGB = 196;
        public const ulong MTLPixelFormatASTC_10x6_LDR = 214;
        public const ulong MTLPixelFormatASTC_10x8_sRGB = 197;
        public const ulong MTLPixelFormatASTC_10x8_LDR = 215;
        public const ulong MTLPixelFormatASTC_10x10_sRGB = 198;
        public const ulong MTLPixelFormatASTC_10x10_LDR = 216;
        public const ulong MTLPixelFormatASTC_12x10_sRGB = 199;
        public const ulong MTLPixelFormatASTC_12x10_LDR = 217;
        public const ulong MTLPixelFormatASTC_12x12_sRGB = 200;
        public const ulong MTLPixelFormatASTC_12x12_LDR = 218;

        public const ulong MTLLoadActionClear = 2;
        public const ulong MTLStoreActionStore = 1;
        public const ulong MTLPrimitiveTypePoint = 0;
        public const ulong MTLPrimitiveTypeLine = 1;
        public const ulong MTLPrimitiveTypeLineStrip = 2;
        public const ulong MTLPrimitiveTypeTriangle = 3;
        public const ulong MTLPrimitiveTypeTriangleStrip = 4;
        public const ulong MTLTextureUsageShaderRead = 1;
        public const ulong MTLTextureUsageShaderWrite = 2;
        public const ulong MTLTextureUsageRenderTarget = 4;
        public const ulong MTLPipelineOptionFailOnBinaryArchiveMiss = 4;

        // M6: texture types
        public const ulong MTLTextureType2D = 2;
        public const ulong MTLTextureType2DArray = 3;
        public const ulong MTLTextureType2DMultisample = 4;
        public const ulong MTLTextureTypeCube = 5;
        public const ulong MTLTextureTypeCubeArray = 6;
        public const ulong MTLTextureType3D = 7;

        // M4: fixed-function state constants
        public const ulong MTLCullModeNone = 0;
        public const ulong MTLCullModeFront = 1;
        public const ulong MTLCullModeBack = 2;
        public const ulong MTLWindingClockwise = 0;
        public const ulong MTLWindingCounterClockwise = 1;
        public const ulong MTLTriangleFillModeFill = 0;
        public const ulong MTLTriangleFillModeLines = 1;
        public const ulong MTLCompareFunctionNever = 0;
        public const ulong MTLCompareFunctionLess = 1;
        public const ulong MTLCompareFunctionEqual = 2;
        public const ulong MTLCompareFunctionLessEqual = 3;
        public const ulong MTLCompareFunctionGreater = 4;
        public const ulong MTLCompareFunctionNotEqual = 5;
        public const ulong MTLCompareFunctionGreaterEqual = 6;
        public const ulong MTLCompareFunctionAlways = 7;
        public const ulong MTLIndexTypeUInt16 = 0;
        public const ulong MTLIndexTypeUInt32 = 1;
        public const ulong MTLVertexStepFunctionConstant = 0;
        public const ulong MTLVertexStepFunctionPerVertex = 1;
        public const ulong MTLVertexStepFunctionPerInstance = 2;
        public const ulong MTLBlendOperationAdd = 0;
        public const ulong MTLBlendOperationSubtract = 1;
        public const ulong MTLBlendOperationReverseSubtract = 2;
        public const ulong MTLBlendOperationMin = 3;
        public const ulong MTLBlendOperationMax = 4;
        public const ulong MTLBlendFactorZero = 0;
        public const ulong MTLBlendFactorOne = 1;
        public const ulong MTLBlendFactorSourceColor = 2;
        public const ulong MTLBlendFactorOneMinusSourceColor = 3;
        public const ulong MTLBlendFactorSourceAlpha = 4;
        public const ulong MTLBlendFactorOneMinusSourceAlpha = 5;
        public const ulong MTLBlendFactorDestinationColor = 6;
        public const ulong MTLBlendFactorOneMinusDestinationColor = 7;
        public const ulong MTLBlendFactorDestinationAlpha = 8;
        public const ulong MTLBlendFactorOneMinusDestinationAlpha = 9;
        public const ulong MTLBlendFactorSource1Color = 10;
        public const ulong MTLBlendFactorOneMinusSource1Color = 11;
        public const ulong MTLBlendFactorSource1Alpha = 12;
        public const ulong MTLBlendFactorOneMinusSource1Alpha = 13;
        public const ulong MTLBlendFactorSourceAlphaSaturated = 14;
        public const ulong MTLBlendFactorBlendColor = 15;
        public const ulong MTLBlendFactorOneMinusBlendColor = 16;
        public const ulong MTLBlendFactorBlendAlpha = 17;
        public const ulong MTLBlendFactorOneMinusBlendAlpha = 18;
        public const ulong MTLColorWriteMaskRed = 0x8;
        public const ulong MTLColorWriteMaskGreen = 0x4;
        public const ulong MTLColorWriteMaskBlue = 0x2;
        public const ulong MTLColorWriteMaskAlpha = 0x1;
        public const ulong MTLTextureAddressModeClampToEdge = 0;
        public const ulong MTLTextureAddressModeRepeat = 2;
        public const ulong MTLTextureAddressModeMirrorRepeat = 3;
        public const ulong MTLSamplerMinMagFilterNearest = 0;
        public const ulong MTLSamplerMinMagFilterLinear = 1;
        public const ulong MTLSamplerMipFilterNotMipmapped = 0;
        public const ulong MTLSamplerMipFilterNearest = 1;
        public const ulong MTLSamplerMipFilterLinear = 2;
        public const ulong MTLPrimitiveTopologyClassUnspecified = 0;
        public const ulong MTLPrimitiveTopologyClassPoint = 1;
        public const ulong MTLPrimitiveTopologyClassLine = 2;
        public const ulong MTLPrimitiveTopologyClassTriangle = 3;

        // M4: MTLVertexFormat (values verified against Metal.framework header)
        public const ulong MTLVertexFormatInvalid = 0;
        public const ulong MTLVertexFormatUChar2 = 1;
        public const ulong MTLVertexFormatUChar3 = 2;
        public const ulong MTLVertexFormatUChar4 = 3;
        public const ulong MTLVertexFormatChar2 = 4;
        public const ulong MTLVertexFormatChar3 = 5;
        public const ulong MTLVertexFormatChar4 = 6;
        public const ulong MTLVertexFormatUChar4Normalized = 9;
        public const ulong MTLVertexFormatUShort2 = 13;
        public const ulong MTLVertexFormatUShort2Normalized = 19;
        public const ulong MTLVertexFormatFloat16_2 = 25; // MTLVertexFormatHalf2
        public const ulong MTLVertexFormatFloat16_3 = 26; // MTLVertexFormatHalf3
        public const ulong MTLVertexFormatFloat16_4 = 27; // MTLVertexFormatHalf4
        public const ulong MTLVertexFormatFloat = 28;
        public const ulong MTLVertexFormatFloat2 = 29;
        public const ulong MTLVertexFormatFloat3 = 30;
        public const ulong MTLVertexFormatFloat4 = 31;
        public const ulong MTLVertexFormatInt = 32;
        public const ulong MTLVertexFormatInt2 = 33;
        public const ulong MTLVertexFormatInt3 = 34;
        public const ulong MTLVertexFormatInt4 = 35;
        public const ulong MTLVertexFormatUInt = 36;
        public const ulong MTLVertexFormatUInt2 = 37;
        public const ulong MTLVertexFormatUInt3 = 38;
        public const ulong MTLVertexFormatUInt4 = 39;
    }

    /// <summary>
    /// MTLColor — a 32-byte struct of four doubles, passed by reference in the ObjC ABI.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MTLColor
    {
        public double Red;
        public double Green;
        public double Blue;
        public double Alpha;

        public MTLColor(double red, double green, double blue, double alpha)
        {
            Red = red;
            Green = green;
            Blue = blue;
            Alpha = alpha;
        }
    }

    /// <summary>
    /// MTLOrigin — 3 × NSUInteger (24 bytes on arm64), passed by reference in the ObjC ABI.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MTLOrigin
    {
        public nuint X;
        public nuint Y;
        public nuint Z;

        public MTLOrigin(nuint x, nuint y, nuint z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    /// <summary>
    /// MTLSize — 3 × NSUInteger (24 bytes on arm64), passed by reference in the ObjC ABI.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MTLSize
    {
        public nuint Width;
        public nuint Height;
        public nuint Depth;

        public MTLSize(nuint width, nuint height, nuint depth)
        {
            Width = width;
            Height = height;
            Depth = depth;
        }
    }

    /// <summary>
    /// MTLRegion — MTLOrigin + MTLSize (48 bytes on arm64), passed by reference in the ObjC ABI.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MTLRegion
    {
        public MTLOrigin Origin;
        public MTLSize Size;

        public MTLRegion(nuint x, nuint y, nuint z, nuint width, nuint height, nuint depth)
        {
            Origin = new MTLOrigin(x, y, z);
            Size = new MTLSize(width, height, depth);
        }
    }

    /// <summary>
    /// MTLViewport — 6 doubles (48 bytes on arm64), passed by reference in the ObjC ABI.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MTLViewport
    {
        public double OriginX;
        public double OriginY;
        public double Width;
        public double Height;
        public double ZNear;
        public double ZFar;

        public MTLViewport(double originX, double originY, double width, double height, double zNear, double zFar)
        {
            OriginX = originX;
            OriginY = originY;
            Width = width;
            Height = height;
            ZNear = zNear;
            ZFar = zFar;
        }
    }

    /// <summary>
    /// MTLScissorRect — 4 × NSUInteger (32 bytes on arm64), passed by reference in the ObjC ABI.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MTLScissorRect
    {
        public nuint X;
        public nuint Y;
        public nuint Width;
        public nuint Height;

        public MTLScissorRect(nuint x, nuint y, nuint width, nuint height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }
}
