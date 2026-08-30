# Ryu: High-Performance Bare-Metal Nintendo Switch Emulation Core

Ryu is an optimized, decoupled Nintendo Switch execution environment engineered for minimal latency, reduced memory overhead, and native hardware execution across macOS and Windows platforms. 

By eliminating desktop user interface abstractions and integrating directly with native operating system subsystems, Ryu achieves deterministic execution timing and efficient resource utilization on modern hardware architectures.

---

## Table of Contents
1. [Architectural Overview](#architectural-overview)
2. [Compilation and Setup](#compilation-and-setup)
   * [macOS (Apple Silicon & Intel)](#macos-compilation-and-setup)
   * [Windows (x64 & ARM64)](#windows-compilation-and-setup)
3. [System Keys and Firmware Installation](#system-keys-and-firmware-installation)
4. [Execution and Configuration Workflows](#execution-and-configuration-workflows)
   * [Basic Launch](#basic-launch)
   * [Command-Line Parameters vs Persistent Defaults](#command-line-parameters-vs-persistent-defaults)
   * [Live In-Session Controls](#live-in-session-controls)
   * [High-Resolution Scaling and Post-Processing](#high-resolution-scaling-and-post-processing)
5. [Input Subsystem and Controller Configuration](#input-subsystem-and-controller-configuration)
   * [Gamepad Support](#gamepad-support)
   * [Keyboard Layout and Bindings](#keyboard-layout-and-bindings)
   * [Mouse and Pointer Input](#mouse-and-pointer-input)
6. [Graphics Pipeline and Display Synchronization](#graphics-pipeline-and-display-synchronization)
7. [Data Hierarchy, Saves, and Modifications](#data-hierarchy-saves-and-modifications)
8. [Command Line Reference](#command-line-reference)
9. [Diagnostic Procedures and Troubleshooting](#diagnostic-procedures-and-troubleshooting)

---

## Architectural Overview

### Decoupled Headless Engine
Traditional desktop emulators allocate significant memory and CPU cycles to window managers, UI compositors, and graphical framework runtimes (such as Avalonia or Qt). Ryu removes these dependencies, utilizing a lightweight execution harness that renders directly to the native windowing surface (`CAMetalLayer` on macOS; Vulkan swapchain surfaces on Windows). This architecture saves approximately 250 MB of memory and eliminates UI-induced CPU thread contention.

### Native Apple Silicon Virtualization (`AppleHv`)
On macOS ARM64 systems, Ryu routes guest EL0 code through Apple's `Hypervisor.framework`, executing instructions directly on physical CPU registers rather than compiling them through intermediate software translation layers.

### Darwin Mach Thread Scheduling
Critical emulator subsystems (guest CPU threads, GPU command dispatch, and DSP audio processing) are assigned Mach `QOS_CLASS_USER_INTERACTIVE` priorities to guarantee execution on Apple Silicon Performance Cores (P-Cores), while asynchronous background tasks such as disk compilation caches are delegated to Efficiency Cores (E-Cores).

### Precision Display Synchronization
Ryu interfaces directly with platform display synchronization APIs:
* **macOS:** Utilizes `CoreVideo` (`CVDisplayLink`) callbacks to synchronize frame pacing with hardware refresh intervals. On 120Hz ProMotion displays, Ryu implements integer cadence division (60 FPS at 2 ticks; 30 FPS at 4 ticks), preventing simulation clock speedups while eliminating CPU spin-waiting loops.
* **Windows:** Employs Vulkan presentation queues with adaptive mailbox pacing, supporting high-refresh displays (120Hz, 144Hz, 165Hz, 240Hz) and variable refresh rate (VRR) technologies including NVIDIA G-Sync and AMD FreeSync.

### Sub-Millisecond Input Polling
Input event loops are configured for 1ms polling intervals with dedicated driver worker threads, providing sub-millisecond latency for competitive gameplay.

---

## Compilation and Setup

### Prerequisites
* [.NET 10.0 SDK](https://dotnet.microsoft.com/download) (or latest supported release)
* Native C/C++ compiler toolchain (Xcode Command Line Tools on macOS; Visual Studio C++ Build Tools on Windows)
* Git

---

### macOS Compilation and Setup

1. **Clone the Repository:**
   ```bash
   git clone https://github.com/abdullah-binmadhi/Ryu.git
   cd Ryu
   ```

2. **Compile the Native Release Binary:**
   Execute the automated packaging script:
   ```bash
   ./distribution/build_release.sh
   ```
   *The compiled binary and runtime dependencies are placed in `distribution/publish/osx-arm64/Ryu`.*

3. **Manual Compilation Command (Alternative):**
   ```bash
   dotnet publish src/Ryujinx.Headless/Ryujinx.Headless.csproj \
       -c Release \
       -r osx-arm64 \
       --self-contained \
       -p:PublishReadyToRun=true \
       -p:TieredPGO=true \
       -o distribution/publish/osx-arm64

   codesign --entitlements distribution/macos/entitlements.xml -f -s - distribution/publish/osx-arm64/Ryu
   ```

---

### Windows Compilation and Setup

1. **Clone the Repository:**
   ```powershell
   git clone https://github.com/abdullah-binmadhi/Ryu.git
   cd Ryu
   ```

2. **Compile the Release Binary:**
   Run from PowerShell or Command Prompt:
   ```powershell
   dotnet publish src/Ryujinx.Headless/Ryujinx.Headless.csproj -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -p:TieredPGO=true -o distribution/publish/win-x64
   ```
   *The compiled executable is located at `distribution\publish\win-x64\Ryu.exe`.*

---

## System Keys and Firmware Installation

Ryu maintains a portable configuration environment located in the `portable/` subdirectory adjacent to the executable.

```text
portable/
├── system/
│   ├── prod.keys
│   └── title.keys
└── bis/
    ├── system/Contents/registered/  (Installed Nintendo Firmware)
    └── user/save/                   (Game Save Data)
```

### 1. Provisioning Cryptographic Keys
Place your dumped `prod.keys` and `title.keys` files into the `portable/system/` directory:
* **macOS:** `distribution/publish/osx-arm64/portable/system/`
* **Windows:** `distribution\publish\win-x64\portable\system\`

### 2. Installing System Firmware
Install official system firmware (from a `.zip` archive or extracted directory) using the built-in installation argument:

* **macOS:**
  ```bash
  ./distribution/publish/osx-arm64/Ryu --install-firmware "/path/to/Firmware_Archive_or_Directory"
  ```
* **Windows:**
  ```powershell
  .\distribution\publish\win-x64\Ryu.exe --install-firmware "C:\path\to\Firmware_Archive_or_Directory"
  ```

Upon completion, the system firmware is automatically verified and unpacked into the registered contents partition.

---

## Execution and Configuration Workflows

### Basic Launch
Provide the path to the game image (`.xci`, `.nsp`, `.nca`, or `.nro`):

* **macOS:**
  ```bash
  ./distribution/publish/osx-arm64/Ryu "/path/to/Game.xci"
  ```
* **Windows:**
  ```powershell
  .\distribution\publish\win-x64\Ryu.exe "C:\Games\Game.xci"
  ```
  *(Note: On Windows, game files can also be dragged and dropped directly onto `Ryu.exe`.)*

---

### Command-Line Parameters vs Persistent Defaults

Ryu provides two complementary methods for configuring runtime behavior:

#### 1. Per-Launch Command-Line Arguments (Dynamic)
Command-line arguments configure the graphics pipeline and emulator subsystems for a single session. To change parameters:
1. Terminate the active game session (`Command + Q` on macOS; `Alt + F4` on Windows).
2. Execute the launch command with the updated arguments:
   ```bash
   ./distribution/publish/osx-arm64/Ryu "Game.xci" --resolution-scale 2 --scaling-filter Fsr --scaling-filter-level 85
   ```
*(Opening a secondary terminal tab while a game is running is unnecessary, as runtime hardware pipelines are initialized upon process creation).*

#### 2. Persistent Defaults via `Config.json` (Static)
To set permanent global defaults without passing arguments on every launch, edit the configuration file located at `portable/Config.json`:
* `"res_scale": 2.0` (Permanently defaults to 2x resolution scaling).
* `"scaling_filter": "Fsr"` (Permanently activates AMD FSR upscaling).
* `"scaling_filter_level": 80` (Configures default sharpening strength).
* `"enable_docked_mode": true` (Defaults to Switch Docked operational mode).

Once saved, executing `Ryu "Game.xci"` without flags will automatically inherit these settings.

---

### Live In-Session Controls
The following actions can be performed directly during active gameplay without restarting the application:
* **Toggle Fullscreen Mode:** Press `Command + F` (macOS), `F11`, or `Alt + Enter` (Windows).
* **Instant Process Termination:** Press `Command + Q` (macOS) or `Alt + F4` (Windows).

---

### High-Resolution Scaling and Post-Processing

* **2x Native Resolution Scaling (1440p / 4K Target):**
  ```bash
  Ryu "Game.xci" --resolution-scale 2
  ```
* **3x Native Resolution Scaling (High-End Discrete GPUs):**
  ```bash
  Ryu "Game.xci" --resolution-scale 3
  ```
* **AMD FidelityFX Super Resolution (FSR) and Anti-Aliasing:**
  ```bash
  Ryu "Game.xci" --scaling-filter Fsr --scaling-filter-level 80 --anti-aliasing SmaaUltra
  ```

---

## Input Subsystem and Controller Configuration

### Gamepad Support
Ryu integrates with SDL3 and native platform driver interfaces (Apple GameController on macOS; XInput/DirectInput/HIDAPI on Windows). Connected controllers are identified and mapped automatically upon initialization:

* **Nintendo Switch Pro Controller and Joy-Cons:** Mapped 1:1 with native hardware button assignments and six-axis motion sensor (gyroscope/accelerometer) support.
* **Sony DualSense (PS5) and DualShock 4 (PS4):**
  * Cross -> B
  * Circle -> A
  * Square -> Y
  * Triangle -> X
  * Touchpad Click -> Minus (-)
  * Options -> Plus (+)
* **Microsoft Xbox Wireless / Elite Controllers:**
  * Automatically mapped to standard Nintendo diamond geometry ($A \leftrightarrow B$, $X \leftrightarrow Y$).
* **DirectInput and Third-Party Controllers:** Full compatibility through SDL3 GameControllerDB definitions.

---

### Keyboard Layout and Bindings
When running without a physical gamepad, Ryu maps keyboard inputs according to the following layout:

```text
         [ Q ] ZL                    ZR [ O ]
         [ E ] L                      R [ U ]

         [ - ] Minus              Plus  [ + ]

         [ W ]                          [ C ] (X)
     [ A ] + [ D ] (Movement)       [ V ] (Y) + [ Z ] (A)
         [ S ]                          [ X ] (B)
        ( L3: [F] )

       [ ^ ]                            [ I ]
   [ < ] + [ > ] (D-Pad)            [ J ] + [ L ] (Camera Stick)
       [ v ]                            [ K ]
                                       ( R3: [H] )
```

| Switch Button | Keyboard Binding | Function |
| :--- | :--- | :--- |
| **Left Stick (Up/Left/Down/Right)** | `W` / `A` / `S` / `D` | Character / Vehicle Movement |
| **Left Stick Button (L3)** | `F` | Crouch / Sprint / Secondary Action |
| **Right Stick (Up/Left/Down/Right)** | `I` / `J` / `K` / `L` | Camera Control / Aiming |
| **Right Stick Button (R3)** | `H` | Reset Camera / Lock-On |
| **D-Pad (Up/Down/Left/Right)** | `Arrow Keys` | Item Selection / Menus |
| **A Button** | `Z` | Confirm / Primary Action |
| **B Button** | `X` | Cancel / Jump |
| **X Button** | `C` | Context Action / Menu |
| **Y Button** | `V` | Attack / Secondary Action |
| **L Shoulder / ZL Trigger** | `E` / `Q` | Left Shoulder / Left Trigger |
| **R Shoulder / ZR Trigger** | `U` / `O` | Right Shoulder / Right Trigger |
| **Minus (-) / Plus (+)** | `-` / `+` (or `=`) | System Select / Start |

---

### Mouse and Pointer Input

1. **Touchscreen Mode (Default):**
   * Primary mouse click and drag operations map directly to single-point capacitive touch events on the virtual Switch display.
   * Ideal for software titles with touch navigation interfaces.

2. **First-Person / Free-Look Camera Mode:**
   * Enable through the command line:
     ```bash
     Ryu "Game.xci" --enable-mouse
     ```
   * **Left Mouse Button:** Mapped to ZR (Primary Fire / Action).
   * **Right Mouse Button:** Mapped to ZL (Aim / Focus).
   * **Mouse Delta:** Directly translates cursor motion to the right analog stick.

---

## Graphics Pipeline and Display Synchronization

### Backend Configurations
* **macOS:** Metal 3 via MoltenVK. Graphics features include asynchronous pipeline compilation, prefilled Metal command buffers, MSL fast-math optimizations, and sub-allocated `MTLHeap` memory pools.
* **Windows:** Direct Vulkan 1.3 driver interface supporting descriptor indexing, unified memory architectures, and multithreaded command recording.

### Handheld versus Docked Modes
* **Docked Mode (Default):** Higher target resolution limits and expanded graphical profile settings.
* **Handheld Mode:** Reduces internal rendering workloads for lower power consumption and improved battery efficiency on portable devices:
  ```bash
  Ryu "Game.xci" --docked-mode=false
  ```

---

## Data Hierarchy, Saves, and Modifications

### Directory Structure
```text
portable/
├── system/                  (System keys: prod.keys, title.keys)
├── bis/
│   ├── system/Contents/     (Installed firmware NCAs)
│   └── user/save/           (Account save data directory)
└── mods/
    └── contents/
        └── <TitleID>/       (Target Game Title ID, e.g. 010051F0207B2000)
            ├── romfs/       (RomFS asset replacement files)
            └── exefs/       (ExeFS binary patches and IPS code mods)
```

### Save Data Management
Game saves are maintained under `portable/bis/user/save/`. Backups can be performed by copying this folder to an external location.

### Applying Game Modifications and 60 FPS Patches
1. Identify the Title ID of the target title.
2. Create the directory `portable/mods/contents/<TitleID>/`.
3. Place the modification files within the corresponding `romfs/` or `exefs/` subdirectories.
4. Launch the game normally; Ryu loads and patches the data structures during initialization.

---

## Command Line Reference

| Option | Type / Default | Description | Example Usage |
| :--- | :--- | :--- | :--- |
| `<input>` | `String` *(Required)* | File system path to the application image (`.xci`, `.nsp`, `.nca`, `.nro`). | `Ryu "game.xci"` |
| `--fullscreen` | `Boolean` (`false`) | Initializes the render viewport in borderless fullscreen mode. | `--fullscreen` |
| `--resolution-scale` | `Float` (`1.0`) | Render target resolution scaling factor (`1` = 720p/1080p, `2` = 1440p/4K). | `--resolution-scale 2` |
| `--anti-aliasing` | `Enum` (`None`) | Anti-aliasing method (`None`, `Fxaa`, `SmaaLow`, `SmaaMedium`, `SmaaHigh`, `SmaaUltra`). | `--anti-aliasing SmaaUltra` |
| `--scaling-filter` | `Enum` (`Bilinear`) | Upscaling filter (`Bilinear`, `Nearest`, `Fsr`, `Area`). | `--scaling-filter Fsr` |
| `--scaling-filter-level` | `Integer` (`80`) | Sharpening intensity parameter for FSR filtering (Range: 0 to 100). | `--scaling-filter-level 85` |
| `--docked-mode` | `Boolean` (`true`) | Selects between Docked mode (`true`) and Handheld mode (`false`). | `--docked-mode=false` |
| `--enable-mouse` | `Boolean` (`false`) | Routes pointer movement and mouse clicks to controller inputs. | `--enable-mouse` |
| `--install-firmware` | `String` (`None`) | Unpacks and installs system firmware files into the internal partition. | `--install-firmware "fw.zip"` |
| `--audio-backend` | `Enum` (`SDL3`) | Audio output driver backend (`SDL3`, `OpenAL`, `SoundIO`). | `--audio-backend SDL3` |
| `--audio-volume` | `Float` (`1.0`) | Master audio attenuation scalar (Range: 0.0 to 1.0). | `--audio-volume 0.8` |
| `--help` | `Flag` | Displays the complete parameter specification list. | `Ryu --help` |

---

## Diagnostic Procedures and Troubleshooting

### 1. Missing System Font Title (`FontStandard / 100000000000811`)
* **Cause:** The title requests shared system font resources stored in firmware archives.
* **Resolution:** Install an official firmware package:
  ```bash
  Ryu --install-firmware "/path/to/firmware"
  ```

### 2. Cryptographic Key Derivation Failure
* **Cause:** Missing or outdated `prod.keys` file.
* **Resolution:** Ensure valid, current `prod.keys` and `title.keys` are present in `portable/system/`.

### 3. Real-Time Telemetry Inspection
Ryu provides a continuous ANSI terminal HUD reporting engine state:
```text
[Ryu] FPS:  30.0 (33.3ms) | 1% Low:  28.8 | RAM: 2950 MB | Thermal: Nominal | Uptime: 00:15
```
* **FPS / Frame Time:** Indicates instantaneous render cadence and frame time variance.
* **1% Low:** Measures consistency of frame pacing.
* **RAM:** Tracks Resident Set Size (RSS) process memory consumption.
* **Thermal:** Reports operating system thermal pressure states (`Nominal`, `Fair`, `Serious`, `Critical`).

### 4. Process Termination
* **macOS:** Press `Command + Q`, click the window close button, or send `SIGINT` via `Ctrl + C` in the controlling terminal.
* **Windows:** Press `Alt + F4`, click the window close icon, or press `Ctrl + C` in the PowerShell/Command Prompt terminal.
