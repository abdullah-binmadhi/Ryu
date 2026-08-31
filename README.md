# Ryu: Pure Native Apple Silicon Nintendo Switch Emulation Core

Ryu is the **World's First Pure Native Apple Silicon Nintendo Switch Emulator**, built from the ground up specifically for macOS and Apple M-Series hardware (M1/M2/M3/M4/M5).

By discarding legacy x86/Windows cross-platform abstractions, Ryu interfaces directly with Apple's bare-metal hardware subsystems: **Apple Hypervisor (`Hypervisor.framework`)**, **Mach VM Zero-Copy Unified Memory**, **Darwin `QOS_CLASS_USER_INTERACTIVE` CPU Scheduling**, and **Apple Metal 3 (`CAMetalLayer`)**.

---

## Table of Contents
1. [Core Apple Silicon Architecture](#core-apple-silicon-architecture)
2. [Compilation and Setup](#compilation-and-setup)
3. [System Keys and Firmware Installation](#system-keys-and-firmware-installation)
4. [Execution and Configuration Workflows](#execution-and-configuration-workflows)
   * [Basic Launch](#basic-launch)
   * [Optimized Gameplay Execution Profiles](#optimized-gameplay-execution-profiles)
   * [Target Framerate and Presentation Cadence (30 / 60 / 120 FPS)](#target-framerate-and-presentation-cadence-30--60--120-fps)
   * [Command-Line Parameters vs Persistent Defaults](#command-line-parameters-vs-persistent-defaults)
   * [Live In-Game Quick Settings Menu and Hotkeys](#live-in-game-quick-settings-menu-and-hotkeys)
   * [High-Resolution Scaling and Post-Processing](#high-resolution-scaling-and-post-processing)
5. [Input Subsystem and Controller Configuration](#input-subsystem-and-controller-configuration)
   * [Gamepad Support](#gamepad-support)
   * [Keyboard Layout and Bindings](#keyboard-layout-and-bindings)
   * [Mouse and Pointer Input](#mouse-and-pointer-input)
6. [Graphics Pipeline and ProMotion Display Synchronization](#graphics-pipeline-and-promotion-display-synchronization)
7. [Data Hierarchy, Saves, and Modifications](#data-hierarchy-saves-and-modifications)
   * [Directory Structure](#directory-structure)
   * [Installing 60 FPS and Visual Patches](#installing-60-fps-and-visual-patches)
8. [Command Line Reference](#command-line-reference)
9. [Diagnostic Procedures and Troubleshooting](#diagnostic-procedures-and-troubleshooting)

---

## Core Apple Silicon Architecture

### 1. Bare-Metal Apple Hypervisor (`AppleHv`)
Ryu executes Nintendo Switch ARM64 guest instructions directly on physical Apple Silicon CPU registers using Apple's `Hypervisor.framework`. There is zero intermediate x86 re-compilation or software translation overhead.

### 2. Zero-Copy Mach VM Unified Memory (UMA)
On Apple Silicon, CPU and GPU share a single, ultra-fast Unified Memory bus. Ryu utilizes `HostMappedUnsafe` zero-copy memory mapping: when the guest CPU writes to memory, the Apple GPU reads from that exact same physical address with **zero PCIe bus emulation and zero memory copying latency**.

### 3. Darwin Mach QoS Thread Scheduling
All critical emulation threads (guest ARM64 CPU cores, GPU dispatch queues, DSP audio rendering) are locked to Mach `QOS_CLASS_USER_INTERACTIVE` priorities. macOS automatically pins these threads to Apple Silicon **Performance Cores (P-Cores)**, while background tasks (shader disk caching) run on **Efficiency Cores (E-Cores)**.

### 4. Direct `CAMetalLayer` Presentation & ProMotion Sync
Ryu bypasses heavy UI frameworks (Qt/Avalonia) and renders directly to native `CAMetalLayer` surfaces with sub-millisecond frame pacing. On 120Hz Apple ProMotion Liquid Retina displays, Ryu implements integer cadence division (60 FPS at 2 ticks; 30 FPS at 4 ticks), eliminating stutter, frame pacing judder, and CPU spin-waiting loops.

---

## Compilation and Setup

### Prerequisites
* **macOS 14.0 Sonoma / macOS 15.0 Sequoia / macOS 26.0+ (ARM64)**
* Apple Silicon Mac (M1, M2, M3, M4, M5 — Pro/Max/Ultra/Base)
* [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
* Xcode Command Line Tools (`xcode-select --install`)
* Git

---

### Step-by-Step macOS Compilation

1. **Clone the Repository:**
   ```bash
   git clone https://github.com/abdullah-binmadhi/Ryu.git
   cd Ryu
   ```

2. **Compile the Native ReadyToRun (R2R) Release Binary:**
   Run the automated build script:
   ```bash
   ./distribution/build_release.sh
   ```
   *The compiled, hardened, and entitlement-signed binary is generated at `distribution/publish/osx-arm64/Ryu`.*

3. **Verify Subsystems via Self-Test:**
   ```bash
   ./distribution/publish/osx-arm64/Ryu --test
   ```

---

## System Keys and Firmware Installation

Ryu uses a completely self-contained, portable environment located at `distribution/publish/osx-arm64/portable/`.

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
Place your dumped `prod.keys` and `title.keys` files into:
`distribution/publish/osx-arm64/portable/system/`

### 2. Installing System Firmware
Install official system firmware (from a `.zip` archive or directory) with a single command:
```bash
./distribution/publish/osx-arm64/Ryu --install-firmware "/path/to/Firmware_Directory_or_Zip"
```

---

## Execution and Configuration Workflows

### Basic Launch
Provide the path to your Nintendo Switch game image (`.xci`, `.nsp`, `.nca`, or `.nro`):
```bash
./distribution/publish/osx-arm64/Ryu "/path/to/Game.xci"
```

---

### Optimized Gameplay Execution Profiles

Ryu supports fine-tuned runtime presets for Apple Silicon hardware:

#### 1. Maximum Stability Profile (Heavy / 30 FPS Native Games & Zero Stutter)
```bash
./distribution/publish/osx-arm64/Ryu "Game.xci" \
  --target-fps 30 \
  --backend-threading on \
  --scaling-filter Bilinear
```

#### 2. Maximum Quality Profile (Peak Visuals & 60 FPS)
```bash
./distribution/publish/osx-arm64/Ryu "Game.xci" \
  --target-fps 60 \
  --backend-threading on \
  --scaling-filter Fsr \
  --scaling-filter-level 80
```

#### 3. Balanced Profile (Optimal Thermals on MacBook Air)
```bash
./distribution/publish/osx-arm64/Ryu "Game.xci" \
  --target-fps 60 \
  --scaling-filter Bilinear
```

#### 4. High-Refresh ProMotion Profile (120Hz Liquid Retina Displays)
```bash
./distribution/publish/osx-arm64/Ryu "Game.xci" \
  --target-fps 120 \
  --disable-docked-mode \
  --backend-threading on \
  --scaling-filter Nearest
```

---

### Target Framerate and Presentation Cadence (30 / 60 / 120 FPS)
* **60 FPS Target:**
  ```bash
  ./distribution/publish/osx-arm64/Ryu "Game.xci" --target-fps 60
  ```
* **30 FPS Target (Standard Console Timing):**
  ```bash
  ./distribution/publish/osx-arm64/Ryu "Game.xci" --target-fps 30
  ```
* **120 FPS Target (Apple ProMotion Displays):**
  ```bash
  ./distribution/publish/osx-arm64/Ryu "Game.xci" --target-fps 120
  ```

---

### Command-Line Parameters vs Persistent Defaults

#### 1. Per-Launch Command-Line Arguments (Dynamic)
To launch with specific settings:
```bash
./distribution/publish/osx-arm64/Ryu "Game.xci" --target-fps 60 --resolution-scale 2 --scaling-filter Fsr --scaling-filter-level 85
```

#### 2. Persistent Defaults via `Config.json` (Static)
Edit `distribution/publish/osx-arm64/portable/Config.json` to make settings permanent:
* `"res_scale": 2.0` (Defaults to 2x resolution scaling).
* `"scaling_filter": "Fsr"` (Activates AMD FSR upscaling).
* `"scaling_filter_level": 80` (FSR sharpening strength).
* `"enable_docked_mode": true` (Defaults to Switch Docked mode).

---

### Live In-Game Quick Settings Menu and Hotkeys

| Shortcut (macOS) | Action | Description |
| :--- | :--- | :--- |
| **`F1`** / **`Command + ,`** / **`Command + 1`** | **Quick Settings Menu** | Displays interactive on-screen menu with current settings and hotkey guide. |
| **`F2`** / **`Command + 2`** | **Cycle Target FPS** | Cycles target framerate on the fly (`30 FPS` $\leftrightarrow$ `60 FPS` $\leftrightarrow$ `120 FPS`). |
| **`F3`** / **`Command + 3`** | **Cycle Scaling Filter** | Switches active post-processing filter (`Bilinear` $\to$ `AMD FSR` $\to$ `Nearest`). |
| **`F4`** / **`Command + 4`** | **Cycle FSR Sharpening** | Cycles AMD FSR sharpening intensity (`80%` $\to$ `100%` $\to$ `50%` $\to$ `20%`). |
| **`F5`** / **`Command + 5`** | **Toggle Anti-Aliasing** | Toggles hardware anti-aliasing (`None` $\leftrightarrow$ `SMAA Ultra`). |
| **`F6`** / **`Command + 6`** | **Toggle Operation Mode** | Switches emulation state between `Docked` and `Handheld` modes. |
| **`F7`** / **`Command + 7`** | **Toggle On-Screen OSD** | Enables or disables real-time titlebar/OSD performance telemetry. |
| **`Command + F`** / **`F11`** | **Toggle Fullscreen** | Toggles native borderless macOS fullscreen mode. |
| **`Command + Q`** | **Instant Exit** | Immediately and safely terminates the emulator process. |

---

### High-Resolution Scaling and Post-Processing

* **2x Retina Resolution Scaling (1440p / 4K Target on Apple Silicon):**
  ```bash
  ./distribution/publish/osx-arm64/Ryu "Game.xci" --resolution-scale 2
  ```
* **AMD FidelityFX Super Resolution (FSR) with SMAA Ultra:**
  ```bash
  ./distribution/publish/osx-arm64/Ryu "Game.xci" --scaling-filter Fsr --scaling-filter-level 80 --anti-aliasing SmaaUltra
  ```

---

## Input Subsystem and Controller Configuration

### Gamepad Support
Ryu integrates with Apple's native **GameController.framework** and SDL3:

* **Nintendo Switch Pro Controller and Joy-Cons:** Mapped 1:1 with native hardware button assignments and motion sensor (gyroscope/accelerometer) support.
* **Sony DualSense (PS5) / DualShock 4 (PS4):** Native Bluetooth connection, touchpad mapping, and gyro aiming.
* **Xbox Wireless / Elite Controllers:** Automatic Nintendo diamond geometry mapping ($A \leftrightarrow B$, $X \leftrightarrow Y$).

---

### Keyboard Layout and Bindings

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

---

## Data Hierarchy, Saves, and Modifications

### Directory Structure
```text
distribution/publish/osx-arm64/portable/
├── Config.json
├── system/
│   ├── prod.keys
│   └── title.keys
├── bis/
│   ├── system/Contents/registered/  (Installed Firmware)
│   └── user/save/                   (Game Save Files)
└── sdcard/
    └── atmosphere/
        └── contents/
            └── <Title_ID>/          (Game Mods, 60 FPS Patches, Cheats)
```

---

## Diagnostic Procedures and Troubleshooting

### 1. Run Complete Subsystem Diagnostic
```bash
./distribution/publish/osx-arm64/Ryu --test
```
Verifies CPU Hypervisor, 4GB DRAM mapping, Darwin QoS P-core lock, Metal driver, SDL3 audio, and OSD overlay.

### 2. Verify Cryptographic Keys & Firmware
If games fail to boot or show a black screen:
```bash
ls -l distribution/publish/osx-arm64/portable/system/prod.keys
./distribution/publish/osx-arm64/Ryu --install-firmware "/path/to/firmware"
```
