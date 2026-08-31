# Ryu User Guide: Pure Native Apple Silicon Nintendo Switch Emulation

---

## Table of Contents
1. [Core Design and Architecture](#core-design-and-architecture)
2. [Quick Start Guide](#quick-start-guide)
3. [Provisioning Keys and System Firmware](#provisioning-keys-and-system-firmware)
4. [Launching Games and Graphics Configuration](#launching-games-and-graphics-configuration)
   * [Basic Execution](#basic-execution)
   * [Optimized Gameplay Execution Profiles](#optimized-gameplay-execution-profiles)
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

## Core Design and Architecture

Ryu is an emulator engineered exclusively for **Apple Silicon (M1/M2/M3/M4/M5)** running macOS.

Unlike cross-platform emulators that rely on heavy graphical UI frameworks (such as Avalonia or Qt) and intermediate x86 translation layers, Ryu operates as a bare-metal executable that interfaces directly with Apple's native APIs:
* **Apple Hypervisor (`Hypervisor.framework`):** Guest ARM64 code runs directly on physical Apple Silicon CPU registers at native hardware speeds.
* **Mach VM Zero-Copy Unified Memory:** CPU and GPU share the same physical memory space with zero PCIe bus emulation.
* **Darwin QoS P-Core Pinning:** Emulation worker threads are pinned to Apple Performance Cores via `QOS_CLASS_USER_INTERACTIVE`.
* **Direct `CAMetalLayer` Presentation:** Renders directly to macOS native surfaces with sub-millisecond frame pacing.

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
   *The binary is compiled and packaged into `distribution/publish/osx-arm64/Ryu`.*

3. **Run Self-Test Diagnostics:**
   ```bash
   ./distribution/publish/osx-arm64/Ryu --test
   ```

---

## Provisioning Keys and System Firmware

Ryu utilizes a self-contained portable directory located at `distribution/publish/osx-arm64/portable/`:

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
Place dumped `prod.keys` and `title.keys` inside:
`distribution/publish/osx-arm64/portable/system/`

### 2. Installing Firmware
Install official system firmware with one command:
```bash
./distribution/publish/osx-arm64/Ryu --install-firmware "/path/to/Firmware_Directory_or_Zip"
```

---

## Launching Games and Graphics Configuration

### Basic Execution
Provide the path to the game image (`.xci`, `.nsp`, `.nca`, or `.nro`):
```bash
./distribution/publish/osx-arm64/Ryu "/path/to/Game.xci"
```

---

### Optimized Gameplay Execution Profiles

#### 1. Maximum Stability Profile (Heavy 30 FPS Games & Zero Stutter)
```bash
./distribution/publish/osx-arm64/Ryu "Game.xci" \
  --target-fps 30 \
  --backend-threading on \
  --scaling-filter Bilinear
```

#### 2. Maximum Quality Profile (Retina Visuals & 60 FPS)
```bash
./distribution/publish/osx-arm64/Ryu "Game.xci" \
  --target-fps 60 \
  --backend-threading on \
  --scaling-filter Fsr \
  --scaling-filter-level 80
```

#### 3. Balanced Profile (MacBook Air Thermal Optimization)
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

## Input Subsystem and Controller Configuration

### Gamepad Support
Ryu natively supports Apple's **GameController.framework** and SDL3:

* **Nintendo Switch Pro Controller / Joy-Cons:** 1:1 button mapping and native motion control gyro.
* **Sony DualSense (PS5) / DualShock 4 (PS4):** Native Bluetooth connection and gyro aiming.
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

## Mods, 60 FPS Patches, and Save Data Management

### Installing Atmosphere LayeredFS Mods & 60 FPS Patches
Place mod directories into:
`distribution/publish/osx-arm64/portable/sdcard/atmosphere/contents/<Title_ID>/`

---

## Comprehensive Command Line Reference

```bash
./distribution/publish/osx-arm64/Ryu <Path_To_Game> [Options]
```

### Essential Flags:
* `--target-fps <30|60|120>`: Sets hardware display cadence and timing.
* `--resolution-scale <1|2|3|4>`: Configures native render resolution multiplier.
* `--scaling-filter <Bilinear|Fsr|Nearest>`: Selects post-processing upscaler.
* `--scaling-filter-level <0-100>`: Configures FSR sharpening intensity.
* `--anti-aliasing <None|Fxaa|SmaaLow|SmaaMedium|SmaaHigh|SmaaUltra>`: Enables anti-aliasing.
* `--disable-docked-mode`: Forces Handheld mode (reduces GPU fill rate by 50%).
* `--dram-size <MemoryConfiguration4GiB|MemoryConfiguration6GiB|MemoryConfiguration8GiB>`: Expands guest DRAM.
* `--install-firmware <Path>`: Installs Nintendo system firmware.
* `--test`: Runs full 6/6 subsystem hardware diagnostic suite.

---

## Troubleshooting and Diagnostic Procedures

### 1. Run Complete Subsystem Diagnostic
```bash
./distribution/publish/osx-arm64/Ryu --test
```

### 2. Verify Gatekeeper and Hardened Runtime
If macOS blocks execution:
```bash
xattr -d com.apple.quarantine distribution/publish/osx-arm64/Ryu
```
