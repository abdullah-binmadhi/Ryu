# Ryu User Guide: High-Performance Nintendo Switch Emulation

---

## Table of Contents
1. [Introduction and Core Design Philosophy](#introduction-and-core-design-philosophy)
2. [Quick Start Guide](#quick-start-guide)
   * [macOS Installation and Setup](#macos-installation-and-setup)
   * [Windows Installation and Setup](#windows-installation-and-setup)
3. [Provisioning Keys and System Firmware](#provisioning-keys-and-system-firmware)
4. [Launching Games and Graphics Configuration](#launching-games-and-graphics-configuration)
   * [Basic Execution](#basic-execution)
   * [Target Framerate and Presentation Cadence (30 / 60 / 120 FPS)](#target-framerate-and-presentation-cadence-30--60--120-fps)
   * [Command-Line Parameters vs Persistent Defaults](#command-line-parameters-vs-persistent-defaults)
   * [Live In-Game Quick Settings Menu and Hotkeys](#live-in-game-quick-settings-menu-and-hotkeys)
   * [High-Resolution Scaling and Post-Processing](#high-resolution-scaling-and-post-processing)
5. [Input Subsystem and Controller Configuration](#input-subsystem-and-controller-configuration)
   * [Gamepad Support](#gamepad-support)
   * [Keyboard Layout and Bindings](#keyboard-layout-and-bindings)
   * [Mouse and Pointer Input](#mouse-and-pointer-input)
6. [Mods, 60 FPS Patches, and Save Data Management](#mods-60-fps-patches-and-save-data-management)
7. [Comprehensive Command Line Reference](#comprehensive-command-line-reference)
8. [Troubleshooting and Diagnostic Procedures](#troubleshooting-and-diagnostic-procedures)

---

## Introduction and Core Design Philosophy

Ryu is an optimized Nintendo Switch emulator designed for low-latency, high-performance gameplay on both macOS and Windows. 

Unlike traditional emulators that bundle graphical UI shells (such as Avalonia or Qt), Ryu operates as a bare-metal executable that interfaces directly with native operating system graphics APIs:
* **macOS:** Renders directly to `CAMetalLayer` via MoltenVK (Metal 3), executing guest code through Apple Silicon hypervisor virtualization (`AppleHv`).
* **Windows:** Renders directly to Vulkan 1.3 swapchain surfaces with multi-threaded command queues.

This decoupled architecture minimizes RAM usage (saving ~250 MB), eliminates desktop compositing overhead, and delivers deterministic frame pacing.

---

## Quick Start Guide

### macOS Installation and Setup

1. **Clone the Repository:**
   ```bash
   git clone https://github.com/abdullah-binmadhi/Ryu.git
   cd Ryu
   ```

2. **Compile the Native Binary:**
   Run the build script:
   ```bash
   ./distribution/build_release.sh
   ```
   *The binary will be compiled and packaged into `distribution/publish/osx-arm64/Ryu`.*

---

### Windows Installation and Setup

1. **Clone the Repository:**
   ```powershell
   git clone https://github.com/abdullah-binmadhi/Ryu.git
   cd Ryu
   ```

2. **Compile the Binary:**
   Execute via PowerShell:
   ```powershell
   dotnet publish src/Ryujinx.Headless/Ryujinx.Headless.csproj -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -p:TieredPGO=true -o distribution/publish/win-x64
   ```
   *The compiled executable will be located at `distribution\publish\win-x64\Ryu.exe`.*

---

## Provisioning Keys and System Firmware

Ryu utilizes a self-contained portable structure located in the `portable/` directory:

```text
portable/
├── system/
│   ├── prod.keys
│   └── title.keys
└── bis/
    ├── system/Contents/registered/  (Installed Firmware)
    └── user/save/                   (Game Saves)
```

### 1. Adding Cryptographic Keys
Place dumped `prod.keys` and `title.keys` inside `portable/system/`:
* **macOS:** `distribution/publish/osx-arm64/portable/system/`
* **Windows:** `distribution\publish\win-x64\portable\system\`

### 2. Installing Firmware
Install official system firmware directly using the `--install-firmware` flag:

* **macOS:**
  ```bash
  ./distribution/publish/osx-arm64/Ryu --install-firmware "/path/to/Firmware_Directory_or_Zip"
  ```
* **Windows:**
  ```powershell
  .\distribution\publish\win-x64\Ryu.exe --install-firmware "C:\path\to\Firmware_Directory_or_Zip"
  ```

---

## Launching Games and Graphics Configuration

### Basic Execution
Pass the path of the game image (`.xci`, `.nsp`, `.nca`, or `.nro`):

* **macOS:**
  ```bash
  ./distribution/publish/osx-arm64/Ryu "/path/to/Game.xci"
  ```
* **Windows:**
  ```powershell
  .\distribution\publish\win-x64\Ryu.exe "C:\Games\Game.xci"
  ```
  *(Tip: On Windows, you can drag and drop `.xci` or `.nsp` files onto `Ryu.exe` directly).*

---

### Target Framerate and Presentation Cadence (30 / 60 / 120 FPS)
Ryu features dynamic display synchronization that can lock presentation cadence to your desired frame rate:

* **60 FPS Target (Standard Smooth Cadence):**
  ```bash
  Ryu "Game.xci" --target-fps 60
  ```
* **30 FPS Target (Default Switch Timing):**
  ```bash
  Ryu "Game.xci" --target-fps 30
  ```
* **120 FPS Target (Apple ProMotion & High-Refresh Displays):**
  ```bash
  Ryu "Game.xci" --target-fps 120
  ```

---

### Command-Line Parameters vs Persistent Defaults

Ryu supports two complementary workflows for configuring options:

#### 1. Per-Launch Command-Line Arguments (Dynamic)
Arguments passed to the executable apply to that specific game session. If you want to modify settings (such as enabling FSR or changing resolution):
1. Exit the running game (`Command + Q` on macOS; `Alt + F4` on Windows).
2. Relaunch with your updated flags:
   ```bash
   ./distribution/publish/osx-arm64/Ryu "Game.xci" --target-fps 60 --resolution-scale 2 --scaling-filter Fsr --scaling-filter-level 85
   ```
*(You do not need to open a second terminal tab while the game is running, as hardware pipelines initialize during process startup).*

#### 2. Persistent Defaults via `Config.json` (Static)
To set permanent defaults so that you don't need to specify command-line flags every time, edit `portable/Config.json`:
* `"res_scale": 2.0` (Defaults to 2x resolution scaling).
* `"scaling_filter": "Fsr"` (Defaults to AMD FSR upscaling).
* `"scaling_filter_level": 80` (Sets FSR sharpening intensity).
* `"enable_docked_mode": true` (Defaults to Docked operational profile).

Once saved, executing `Ryu "Game.xci"` without flags will automatically inherit these settings.

---

### Live In-Game Quick Settings Menu and Hotkeys

Ryu features live in-session controls, an interactive Quick Settings dialog, and real-time on-screen telemetry that can be toggled without restarting the game:

| Shortcut (macOS / Windows) | Action | Description |
| :--- | :--- | :--- |
| **`F1`** / **`Command + ,`** / **`Command + 1`** | **Quick Settings Menu** | Displays interactive on-screen menu with current settings and hotkey guide. |
| **`F2`** / **`Command + 2`** | **Cycle Target FPS** | Cycles target framerate on the fly (`30 FPS` $\leftrightarrow$ `60 FPS` $\leftrightarrow$ `120 FPS`). |
| **`F3`** / **`Command + 3`** | **Cycle Scaling Filter** | Switches active post-processing filter (`Bilinear` $\to$ `AMD FSR` $\to$ `Nearest`). |
| **`F4`** / **`Command + 4`** | **Cycle FSR Sharpening** | Cycles AMD FSR sharpening intensity (`80%` $\to$ `100%` $\to$ `50%` $\to$ `20%`). |
| **`F5`** / **`Command + 5`** | **Toggle Anti-Aliasing** | Toggles hardware anti-aliasing (`None` $\leftrightarrow$ `SMAA Ultra`). |
| **`F6`** / **`Command + 6`** | **Toggle Operation Mode** | Switches emulation state between `Docked` and `Handheld` modes. |
| **`F7`** / **`Command + 7`** | **Toggle On-Screen OSD** | Enables or disables real-time titlebar/OSD performance telemetry. |
| **`Command + F`** / **`F11`** | **Toggle Fullscreen** | Toggles borderless fullscreen display mode. |
| **`Command + Q`** / **`Alt + F4`** | **Instant Exit** | Immediately and safely terminates the emulator process. |

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

## Mods, 60 FPS Patches, and Save Data Management

### Save Data Location
All user saves are stored under `portable/bis/user/save/`. You can back up or restore save files by copying the contents of this folder.

### Installing 60 FPS, Cheats, and Visual Patches
Ryu includes native support for Atmosphere cheats (`cheats/`), IPS binary patches (`.ips`), and IPSwitch text patches (`.pchtxt`), including all community patches distributed in standard mod repositories (such as the Yuzu Mod Archive):

1. Identify the **Title ID** of the game (for example, `010051F0207B2000`).
2. Create the target path in the portable directory:
   * For Atmosphere Cheat Patches: `portable/mods/contents/<TitleID>/cheats/<BuildID>.txt`
   * For ExeFS / IPSwitch Mods: `portable/mods/contents/<TitleID>/exefs/60fps.pchtxt`
3. Launch the title:
   ```bash
   Ryu "Game.xci" --target-fps 60
   ```
Ryu automatically detects and enables installed cheats and patches during startup, unlocks 60 FPS presentation pacing, and corrects game engine delta physics without causing fast-forward distortion.

---

## Comprehensive Command Line Reference

| Option | Type / Default | Description | Example Usage |
| :--- | :--- | :--- | :--- |
| `<input>` | `String` *(Required)* | File system path to the application image (`.xci`, `.nsp`, `.nca`, `.nro`). | `Ryu "game.xci"` |
| `--target-fps` | `Integer` (`0` = Default) | Configures presentation cadence and refresh target (`30`, `60`, `120`). | `--target-fps 60` |
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

## Troubleshooting and Diagnostic Procedures

### 1. Missing System Font Error
* **Cause:** The title requests system fonts provided in the Switch firmware.
* **Resolution:** Install an official firmware package:
  ```bash
  Ryu --install-firmware "/path/to/firmware"
  ```

### 2. Cryptographic Key Derivation Failure
* **Cause:** Missing or outdated `prod.keys` file.
* **Resolution:** Place your valid `prod.keys` in `portable/system/`.

### 3. Real-Time Telemetry Inspection
Ryu outputs real-time performance telemetry in the terminal and on-screen window titlebar:
```text
[Ryu] FPS:  60.0 (16.6ms) | 1% Low:  58.2 | RAM: 2950 MB | Thermal: Nominal | Uptime: 00:15
```
* **FPS / Frame Time:** Current framerate and millisecond frame timing.
* **1% Low:** Measures consistency of frame delivery.
* **RAM:** Tracks active process memory usage.
* **Thermal:** Reports host thermal pressure (`Nominal`, `Fair`, `Heavy`, `Critical`).

### 4. Process Termination
* **macOS:** Press `Command + Q`, click the window close button, or press `Ctrl + C` in the controlling terminal.
* **Windows:** Press `Alt + F4`, click the window close icon, or press `Ctrl + C` in the PowerShell/Command Prompt window.
