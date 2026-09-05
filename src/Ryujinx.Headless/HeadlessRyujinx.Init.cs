using LibHac.Tools.FsSystem;
using Ryujinx.Audio.Backends.SDL3;
using Ryujinx.Ava.Systems.Configuration;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Configuration.Hid;
using Ryujinx.Common.Configuration.Hid.Controller;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Utilities;
using Ryujinx.Cpu;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.OpenGL;
using Ryujinx.Graphics.Vulkan;
using Ryujinx.HLE;
using Ryujinx.Input;
using Ryujinx.Input.SDL3;
using Silk.NET.Vulkan;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Ryujinx.Headless
{
    public partial class HeadlessRyujinx
    {
        public static void Initialize()
        {
            // Hook unhandled exception and process exit events.
            AppDomain.CurrentDomain.UnhandledException += (sender, e)
                => Program.ProcessUnhandledException(sender, e.ExceptionObject as Exception, e.IsTerminating);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Program.Exit();

            // Initialize the configuration.
            ConfigurationState.Initialize();
        }

        private static InputConfig HandlePlayerConfiguration(string inputProfileName, string inputId, PlayerIndex index)
        {
            if (inputId == null)
            {
                if (index == PlayerIndex.Player1)
                {
                    // Check if a physical gamepad is connected (e.g. Xbox, PlayStation, Switch Pro Controller)
                    string firstGamepadId = null;
                    foreach (string id in _inputManager.GamepadDriver.GamepadsIds)
                    {
                        firstGamepadId = id;
                        break;
                    }

                    if (!string.IsNullOrEmpty(firstGamepadId))
                    {
                        inputId = firstGamepadId;
                        Logger.Notice.Print(LogClass.Application, $"Auto-detected connected gamepad for {index}: ID \"{inputId}\"");
                    }
                    else
                    {
                        Logger.Info?.Print(LogClass.Application, $"{index} not configured, defaulting to default keyboard.");
                        inputId = "0";
                    }
                }
                else
                {
                    Logger.Info?.Print(LogClass.Application, $"{index} not configured");
                    return null;
                }
            }

            IGamepad gamepad = null;
            bool isKeyboard = false;

            if (inputId != "0")
            {
                gamepad = _inputManager.GamepadDriver.GetGamepad(inputId);
            }

            if (gamepad == null)
            {
                gamepad = _inputManager.KeyboardDriver.GetGamepad(inputId);
                if (gamepad != null)
                {
                    isKeyboard = true;
                }
                else
                {
                    gamepad = _inputManager.GamepadDriver.GetGamepad(inputId);
                    if (gamepad == null)
                    {
                        Logger.Error?.Print(LogClass.Application, $"{index} gamepad not found (\"{inputId}\")");
                        return null;
                    }
                }
            }

            string gamepadName = gamepad.Name;
            bool isNintendoStyle = false;

            if (gamepad is SDL3Gamepad sdlGp)
            {
                // Nintendo vendor ID is 0x057E
                isNintendoStyle = sdlGp.VendorId == 0x057E;
            }
            else
            {
                isNintendoStyle = gamepadName.Contains("Nintendo", StringComparison.OrdinalIgnoreCase);
            }

            gamepad.Dispose();

            InputConfig config;

            if (inputProfileName == null || inputProfileName.Equals("default"))
            {
                if (isKeyboard)
                {
                    config = InputConfigDefaults.CreateDefaultKeyboardConfiguration(
                        null,
                        null,
                        ControllerType.JoyconPair,
                        index);
                }
                else
                {
                    config = InputConfigDefaults.CreateDefaultControllerConfiguration(
                        null,
                        null,
                        ControllerType.ProController,
                        index,
                        isNintendoStyle);
                }
            }
            else
            {
                string profileBasePath = isKeyboard 
                    ? Path.Combine(AppDataManager.ProfilesDirPath, "keyboard") 
                    : Path.Combine(AppDataManager.ProfilesDirPath, "controller");

                string path = Path.Combine(profileBasePath, inputProfileName + ".json");

                if (!File.Exists(path))
                {
                    Logger.Error?.Print(LogClass.Application, $"Input profile \"{inputProfileName}\" not found for \"{inputId}\"");
                    return null;
                }

                try
                {
                    config = JsonHelper.DeserializeFromFile(path, _serializerContext.InputConfig);
                }
                catch (JsonException)
                {
                    Logger.Error?.Print(LogClass.Application, $"Input profile \"{inputProfileName}\" parsing failed for \"{inputId}\"");
                    return null;
                }
            }

            config.Id = inputId;
            config.PlayerIndex = index;

            string inputTypeName = isKeyboard ? "Keyboard" : "Gamepad";
            Logger.Info?.Print(LogClass.Application, $"{config.PlayerIndex} configured with {inputTypeName} \"{config.Id}\"");

            if (config is StandardControllerInputConfig controllerConfig)
            {
                if (controllerConfig.RangeLeft <= 0.0f && controllerConfig.RangeRight <= 0.0f)
                {
                    controllerConfig.RangeLeft = 1.0f;
                    controllerConfig.RangeRight = 1.0f;
                }
            }

            return config;
        }

        private static IRenderer CreateRenderer(Options options, WindowBase window)
        {
            if (OperatingSystem.IsMacOS() && options.GraphicsBackend == GraphicsBackend.Metal && window is MetalWindow)
            {
                return new Ryujinx.Graphics.Metal.MetalRenderer();
            }

            if (options.GraphicsBackend == GraphicsBackend.Vulkan && window is VulkanWindow vulkanWindow)
            {
                string preferredGpuId = string.Empty;
                Vk api = Vk.GetApi();

                if (!string.IsNullOrEmpty(options.PreferredGPUVendor))
                {
                    string preferredGpuVendor = options.PreferredGPUVendor.ToLowerInvariant();
                    DeviceInfo[] devices = VulkanRenderer.GetPhysicalDevices(api);

                    foreach (DeviceInfo device in devices)
                    {
                        if (device.Vendor.Equals(preferredGpuVendor, StringComparison.OrdinalIgnoreCase))
                        {
                            preferredGpuId = device.Id;
                            break;
                        }
                    }
                }

                return new VulkanRenderer(
                    api,
                    (instance, vk) => new SurfaceKHR((ulong)vulkanWindow.CreateWindowSurface(instance.Handle)),
                    VulkanWindow.GetRequiredInstanceExtensions,
                    preferredGpuId);
            }

            return new OpenGLRenderer();
        }

        private static Switch InitializeEmulationContext(WindowBase window, IRenderer renderer, Options options)
        {
            if (options.TargetFps > 0 && options.TargetFps > 30)
            {
                // Only force a custom emulated refresh when targeting above 30 FPS.
                // Switch games are authored for a 60 Hz display; a 30 FPS game paces
                // itself to every second vsync. Emulating a 30 Hz display would halve
                // its speed (observed: rock-steady 15.0 FPS instead of 30.0).
                options.VSyncMode = VSyncMode.Custom;
                options.CustomVSyncInterval = options.TargetFps;
            }

            return new(
                new HleConfiguration(
                        options.DramSize,
                        options.SystemLanguage,
                        options.SystemRegion,
                        options.VSyncMode,
                        !options.DisableDockedMode,
                        !options.DisablePTC,
                        ITickSource.RealityTickScalar,
                        options.EnableInternetAccess,
                        !options.DisableFsIntegrityChecks ? IntegrityCheckLevel.ErrorOnInvalid : IntegrityCheckLevel.None,
                        options.FsGlobalAccessLogMode,
                        options.SystemTimeOffset,
                        options.SystemTimeZone,
                        options.MemoryManagerMode,
                        options.IgnoreMissingServices,
                        options.AspectRatio,
                        options.AudioVolume,
                        options.EffectiveUseHypervisor,
                        options.MultiplayerLanInterfaceId,
                        Common.Configuration.Multiplayer.MultiplayerMode.Disabled,
                        false,
                        string.Empty,
                        string.Empty,
                        options.EnableGdbStub,
                        options.GdbStubPort,
                        options.DebuggerSuspendOnStart,
                        options.CustomVSyncInterval
                    )
                    .Configure(
                        _virtualFileSystem,
                        _libHacHorizonManager,
                        _contentManager,
                        _accountManager,
                        _userChannelPersistence,
                        renderer.TryMakeThreaded(options.BackendThreading),
                        new SDL3HardwareDeviceDriver(),
                        window
                    )
            );
        }
    }
}
