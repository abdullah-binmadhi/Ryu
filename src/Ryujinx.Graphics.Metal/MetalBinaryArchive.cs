using Ryujinx.Graphics.Metal.Interop;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Metal
{
    /// <summary>
    /// M3b: MTLBinaryArchive — a persistent Metal pipeline cache.
    ///
    /// Pipelines are compiled once and stored in the archive; the archive can be
    /// serialized to bytes and persisted to disk, then reloaded on the next launch.
    /// When creating a pipeline state from a descriptor with a loaded archive attached
    /// and <see cref="MetalBindings.MTLPipelineOptionFailOnBinaryArchiveMiss"/>, Metal
    /// reuses the precompiled binaries instead of recompiling — eliminating the live
    /// shader-compile stutter when entering a new scene.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public sealed class MetalBinaryArchive : IDisposable
    {
        private nint _archive;
        private bool _disposed;

        /// <summary>The underlying MTLBinaryArchive object.</summary>
        public nint ArchiveHandle => _archive;

        private MetalBinaryArchive(nint archive)
        {
            _archive = MetalBindings.Retain(archive);
        }

        /// <summary>Creates a new (empty) binary archive.</summary>
        public static MetalBinaryArchive Create(nint device)
        {
            nint descriptor = MetalBindings.objc_msgSend(
                MetalBindings.objc_getClass("MTLBinaryArchiveDescriptor"),
                MetalBindings.SelNew);

            try
            {
                nint archive = MetalBindings.objc_msgSend(
                    device,
                    MetalBindings.SelNewBinaryArchiveWithDescriptorError,
                    descriptor,
                    nint.Zero);

                return archive != nint.Zero ? new MetalBinaryArchive(archive) : null;
            }
            finally
            {
                MetalBindings.Release(descriptor);
            }
        }

        /// <summary>
        /// Loads a binary archive from previously-serialized bytes (i.e. read from disk).
        /// Returns null if the data is empty or invalid.
        /// Uses the URL-based descriptor path (setURL:), since the AGX implementation on
        /// recent macOS no longer exposes the in-memory setData: variant.
        /// </summary>
        public static MetalBinaryArchive Load(nint device, ReadOnlySpan<byte> data)
        {
            if (data.Length == 0)
            {
                return null;
            }

            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ryu-archive-{Guid.NewGuid():N}.metallib");

            try
            {
                System.IO.File.WriteAllBytes(path, data.ToArray());

                nint descriptor = MetalBindings.objc_msgSend(
                    MetalBindings.objc_getClass("MTLBinaryArchiveDescriptor"),
                    MetalBindings.SelNew);
                nint nsPath = MetalBindings.CreateNSString(path);

                try
                {
                    nint url = MetalBindings.objc_msgSend(MetalBindings.objc_getClass("NSURL"), MetalBindings.SelFileURLWithPath, nsPath);

                    if (url != nint.Zero)
                    {
                        try
                        {
                            MetalBindings.objc_msgSend_void(descriptor, MetalBindings.SelSetUrl, url);
                        }
                        finally
                        {
                            MetalBindings.Release(url);
                        }
                    }

                    nint archive = MetalBindings.objc_msgSend(
                        device,
                        MetalBindings.SelNewBinaryArchiveWithDescriptorError,
                        descriptor,
                        nint.Zero);

                    return archive != nint.Zero ? new MetalBinaryArchive(archive) : null;
                }
                finally
                {
                    MetalBindings.Release(descriptor);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                try
                {
                    if (System.IO.File.Exists(path))
                    {
                        System.IO.File.Delete(path);
                    }
                }
                catch
                {
                    // Best-effort cleanup.
                }
            }
        }

        /// <summary>
        /// Explicitly compiles and adds the pipeline described by the render pipeline
        /// descriptor into this archive (addRenderPipelineFunctionsWithDescriptor:error:).
        /// This is the documented way to populate an archive; the compiled functions
        /// become serializable via <see cref="Serialize"/>.
        /// </summary>
        public bool AddRenderPipeline(nint renderPipelineDescriptor)
        {
            if (_archive == nint.Zero || renderPipelineDescriptor == nint.Zero)
            {
                return false;
            }

            nint result = MetalBindings.objc_msgSend(
                _archive,
                MetalBindings.SelAddRenderPipelineFunctionsWithDescriptorError,
                renderPipelineDescriptor,
                nint.Zero);

            return result != nint.Zero;
        }

        /// <summary>
        /// Attaches this archive to a render pipeline descriptor's binaryArchives array.
        /// After this, creating a pipeline state from the descriptor also adds the
        /// pipeline's functions to this archive (so they can be serialized later).
        /// </summary>
        public void AttachTo(nint renderPipelineDescriptor)
        {
            if (_archive == nint.Zero || renderPipelineDescriptor == nint.Zero)
            {
                return;
            }

            nint array = MetalBindings.objc_msgSend(
                MetalBindings.objc_getClass("NSArray"),
                MetalBindings.SelArrayWithObject,
                _archive);

            try
            {
                MetalBindings.objc_msgSend_void(renderPipelineDescriptor, MetalBindings.SelSetBinaryArchives, array);
            }
            finally
            {
                MetalBindings.Release(array);
            }
        }

        /// <summary>
        /// Creates a render pipeline state from the descriptor with this archive attached.
        /// When <paramref name="failOnMiss"/> is set and the pipeline is not already in the
        /// archive, creation fails (returns 0) — use that to detect a cache miss and fall
        /// back to a compiling creation.
        /// </summary>
        public nint CreatePipelineState(nint device, nint renderPipelineDescriptor, bool failOnMiss)
        {
            if (_archive == nint.Zero)
            {
                return nint.Zero;
            }

            AttachTo(renderPipelineDescriptor);

            ulong options = failOnMiss ? MetalBindings.MTLPipelineOptionFailOnBinaryArchiveMiss : 0;

            return MetalBindings.objc_msgSend(
                device,
                MetalBindings.SelNewRenderPipelineStateWithDescriptorOptionsReflectionError,
                renderPipelineDescriptor,
                options,
                nint.Zero,
                nint.Zero);
        }

        /// <summary>
        /// Serializes the archive contents to bytes for persistence to disk.
        /// Returns null on failure or when the archive is empty.
        /// Prefers serializeToData:error: (in-memory), but falls back to
        /// serializeToURL:error: via a temp file on macOS builds where the AGX
        /// implementation no longer exposes the data variant.
        /// </summary>
        public byte[] Serialize()
        {
            if (_archive == nint.Zero)
            {
                return null;
            }

            bool hasDataVariant = MetalBindings.objc_msgSend_bool(_archive, MetalBindings.SelRespondsToSelector, MetalBindings.SelSerializeToDataError);

            if (hasDataVariant)
            {
                nint data = MetalBindings.objc_msgSend(_archive, MetalBindings.SelSerializeToDataError, nint.Zero);

                if (data == nint.Zero)
                {
                    return null;
                }

                try
                {
                    nuint length = (nuint)MetalBindings.objc_msgSend_ulong_ret(data, MetalBindings.SelLength);
                    nint bytes = MetalBindings.objc_msgSend(data, MetalBindings.SelBytes);

                    if (length == 0 || bytes == nint.Zero)
                    {
                        return null;
                    }

                    byte[] result = new byte[length];
                    Marshal.Copy(bytes, result, 0, (int)length);
                    return result;
                }
                finally
                {
                    MetalBindings.Release(data);
                }
            }
            else
            {
                // serializeToURL:error: — write to a temp file and read it back.
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ryu-archive-{Guid.NewGuid():N}.metallib");
                nint nsPath = MetalBindings.CreateNSString(path);

                try
                {
                    nint url = MetalBindings.objc_msgSend(MetalBindings.objc_getClass("NSURL"), MetalBindings.SelFileURLWithPath, nsPath);

                    if (url == nint.Zero)
                    {
                        return null;
                    }

                    try
                    {
                        nint ok = MetalBindings.objc_msgSend(_archive, MetalBindings.SelSerializeToURLError, url, nint.Zero);

                        if (ok == nint.Zero || !System.IO.File.Exists(path))
                        {
                            return null;
                        }

                        return System.IO.File.ReadAllBytes(path);
                    }
                    finally
                    {
                        MetalBindings.Release(url);
                    }
                }
                finally
                {
                    try
                    {
                        if (System.IO.File.Exists(path))
                        {
                            System.IO.File.Delete(path);
                        }
                    }
                    catch
                    {
                        // Best-effort cleanup.
                    }
                }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;

                if (_archive != nint.Zero)
                {
                    MetalBindings.Release(_archive);
                    _archive = nint.Zero;
                }

                GC.SuppressFinalize(this);
            }
        }
    }
}
