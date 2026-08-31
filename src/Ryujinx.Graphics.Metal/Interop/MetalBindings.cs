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
        public static partial void objc_msgSend_void(nint receiver, nint selector, [MarshalAs(UnmanagedType.I1)] bool arg1);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        public static partial void objc_msgSend_void(nint receiver, nint selector, nuint arg1, nuint arg2, nuint arg3);

        [LibraryImport(ObjCLib, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        public static partial bool objc_msgSend_bool(nint receiver, nint selector);

        // Selector Cache
        public static readonly nint SelNewCommandQueue = sel_registerName("newCommandQueue");
        public static readonly nint SelCommandBuffer = sel_registerName("commandBuffer");
        public static readonly nint SelCommit = sel_registerName("commit");
        public static readonly nint SelNewBufferWithLengthOptions = sel_registerName("newBufferWithLength:options:");
        public static readonly nint SelNewBufferWithBytesNoCopyOptions = sel_registerName("newBufferWithBytesNoCopy:length:options:deallocator:");
        public static readonly nint SelContents = sel_registerName("contents");
        public static readonly nint SelLength = sel_registerName("length");
        public static readonly nint SelEndEncoding = sel_registerName("endEncoding");
        public static readonly nint SelName = sel_registerName("name");
        public static readonly nint SelRelease = sel_registerName("release");
        public static readonly nint SelRetain = sel_registerName("retain");

        // Metal Constants
        public const ulong MTLResourceStorageModeShared = 0;
        public const ulong MTLResourceCPUCacheModeDefaultCache = 0;
    }
}
