using Silk.NET.Core.Loader;
using Silk.NET.Vulkan;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryujinx.Graphics.Vulkan.MoltenVK
{
    [SupportedOSPlatform("macos")]
    public static partial class MVKInitialization
    {
        private const string VulkanLib = "libvulkan.dylib";

        [LibraryImport("libMoltenVK.dylib")]
        private static partial Result vkGetMoltenVKConfigurationMVK(nint unusedInstance, out MVKConfiguration config, in nint configSize);

        [LibraryImport("libMoltenVK.dylib")]
        private static partial Result vkSetMoltenVKConfigurationMVK(nint unusedInstance, in MVKConfiguration config, in nint configSize);

        public static void Initialize()
        {
            nint configSize = (nint)Marshal.SizeOf<MVKConfiguration>();

            vkGetMoltenVKConfigurationMVK(nint.Zero, out MVKConfiguration config, configSize);

            // 1. Metal Argument Buffers (Tier 2 fast descriptors)
            config.UseMetalArgumentBuffers = true;

            // 2. Prefill Command Buffers (Zero Driver Stutters on CPU)
            config.PrefillMetalCommandBuffers = true;

            // 3. Fast-Math Shader Acceleration (High Arithmetic Throughput on Apple Silicon)
            config.FastMathEnabled = true;

            // 4. Memory Heap Pooling & Descriptor Preallocation (Reduces kernel-level vm_allocate syscalls)
            config.UseMTLHeap = true;
            config.UseCommandPooling = true;
            config.PreallocateDescriptors = true;
            config.MaxActiveMetalCommandBuffersPerQueue = 64;

            // 5. Hardware Metal Events & Multi-Queue Overlapping (Zero CPU Wait on Semaphores)
            config.SemaphoreSupportStyle = MVKVkSemaphoreSupportStyle.MVK_CONFIG_VK_SEMAPHORE_SUPPORT_STYLE_METAL_EVENTS_WHERE_SAFE;
            config.SemaphoreUseMTLFence = true;
            config.SynchronousQueueSubmits = false;

            // 6. Device Lost Recovery Hook
            config.ResumeLostDevice = true;

            // 7. Minimal Logging overhead
            config.LogLevel = MVKConfigLogLevel.Error;

            vkSetMoltenVKConfigurationMVK(nint.Zero, config, configSize);
        }

        private static string[] Resolver(string path)
        {
            if (path.EndsWith(VulkanLib))
            {
                path = path[..^VulkanLib.Length] + "libMoltenVK.dylib";
                return [path];
            }

            return [];
        }

        public static void InitializeResolver()
        {
            ((DefaultPathResolver)PathResolver.Default).Resolvers.Insert(0, Resolver);
        }
    }
}
