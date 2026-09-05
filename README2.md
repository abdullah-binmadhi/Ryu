# Architectural Specification: Native Apple Silicon Headless Emulation Engine (Ryu)

> **Documentation Hierarchy & Source of Truth:**
> - For immediate execution phases and live operational gates: see **[Native Metal Execution Roadmap](docs/native-metal-execution-roadmap.md)** (`docs/native-metal-execution-roadmap.md`).
> - For shader compiler IR specifications and non-negotiable architectural rules: see **[Native Metal 4 Engine Plan](docs/native-engine-plan.md)** (`docs/native-engine-plan.md`).
> - For Metal 4 binding and implementation reference: see **[Complete Native Metal Context](docs/full-metal.md)** (`docs/full-metal.md`).

---

## 1. Executive Summary & Architectural Overview
This document defines the engineering architecture for transforming the cross-platform Ryubing / Ryujinx codebase into a terminal-first, hardware-accelerated Nintendo Switch emulation core optimized specifically for Apple Silicon (M-series) SoCs across all generations (M1 through M5/M6+).

Rather than rebuilding the guest OS emulation stack from scratch, this initiative executes an **in-place modular refactoring**. The system preserves high-level guest Horizon OS (HOS) service emulation, ARM64 CPU instruction execution, and title compatibility layers, while systematically replacing generic .NET runtime abstractions, bloated desktop UI frameworks (Avalonia/XAML/Skia), and cross-platform translation middle-layers (Vulkan/MoltenVK) with a lightweight **Terminal/CLI runner (Astris-style)**, bare-metal Darwin kernel primitives (Mach QoS, `CVDisplayLink`), low-latency SDL3 Apple backend hooks, a **native Apple Metal 4 GAL backend** (`MTL4ArgumentTable` zero-copy bindings + multi-threaded P-core command encoding) paired with intentional Metal 3 presentation/format-blit hybrid pipelines, and native Apple frameworks (`MetalFX`, `Accelerate/vDSP`, `AdvSimd` NEON, `AppleHv`).

```
+-----------------------------------------------------------------------------------------+
|                         Guest Layer: Horizon OS & Switch Titles                         |
+-----------------------------------------------------------------------------------------+
                                             │
                                             ▼
+-----------------------------------------------------------------------------------------+
|                              Preserved Emulation Services                               |
|  - Dual CPU Execution: AppleHv (Bare-Metal Hypervisor) + ARMeilleure (ARM64 JIT)        |
|  - Horizon OS IPC / System Services (FS, NVN, Time, Audio Renderer, Account)            |
|  - Title Compatibility & Game-Specific Quirks Layer                                     |
+-----------------------------------------------------------------------------------------+
                                             │
                                             ▼
+-----------------------------------------------------------------------------------------+
|                        Surgically Refactored Hardware Subsystems                        |
|  ┌─────────────────────────────────┐   ┌──────────────────────────────────────────────┐ |
|  │ Unified Memory & Purgeable RAM  │   │ Native Metal 4 & Metal 3 Hybrid Engine       │ |
|  │ (Hybrid 4KB/16KB Tracking,      │   │ (Direct Metal 4 GAL, MTL4ArgumentTable       │ |
|  │ Zero-Copy Texture Aliasing,     │   │  Zero-Copy, Multi-Core P-Core Encoding,      │ |
|  │ Mach VM_FLAGS_PURGABLE Caches)  │   │  MetalFX Spatial/DRS, Hardware ASTC)         │ |
|  └─────────────────────────────────┘   └──────────────────────────────────────────────┘ |
|  ┌─────────────────────────────────┐   ┌──────────────────────────────────────────────┐ |
|  │ Mach QoS & Timing Engine        │   │ Vectorized Audio DSP & Input Engine          │ |
|  │ (CVDisplayLink 60Hz Locking,    │   │ (ARM64 AdvSimd NEON / vDSP Vectorized DSP,   │ |
|  │ P-Core Thread Affinity,         │   │ Low-Latency SDL3 Apple Backend & Rumble,     │ |
|  │ Lockless SPSC Command Queues)   │   │ Native Aligned Memory Allocation)            │ |
|  └─────────────────────────────────┘   └──────────────────────────────────────────────┘ |
+-----------------------------------------------------------------------------------------+
                                             │
                                             ▼
+-----------------------------------------------------------------------------------------+
|                     Host Platform: Apple Silicon Hardware (Darwin)                      |
|       - 16KB Page Unified Memory Subsystem (16GB RAM / 100 GB/s Bandwidth)              |
|       - Apple Silicon TBDR GPU (10 Cores) + 16-Core Apple Neural Engine (ANE)           |
|       - 4x Avalanche P-Cores + 4x Blizzard E-Cores + ARM NEON Vector Units              |
+-----------------------------------------------------------------------------------------+
```

---

## 2. Terminal-First Architecture (Astris-Style CLI Execution)

Rather than running heavy GUI window compositing loops, the emulator operates with a **dual-surface decoupled model**:
1. **Interactive Terminal (CLI / TUI):** Handles game launching, library indexing, configuration, real-time telemetry HUD, and process lifecycle management.
2. **Dedicated Game Surface:** A minimal, borderless `CAMetalLayer` window rendering the game viewport directly via Metal 4/Metal 3/MetalFX with zero desktop UI overhead.

```
┌─────────────────────────────────────────────────────────────────────────────────────────┐
│                          TERMINAL-FIRST EXECUTION MODEL                                 │
├─────────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                         │
│   [ Terminal Window (TUI Dashboard) ]             [ Dedicated Metal Viewport ]          │
│   • Sub-50ms CLI launch (`ryu run game.nsp`)      • Pure `CAMetalLayer` + MetalFX       │
│   • Zero UI Garbage Collection Churn              • Zero XAML/Skia window compositing   │
│   • Real-Time Telemetry & Thermal HUD             • Exclusive fullscreen or borderless  │
│   • Instant library search & scripting            • Direct 60Hz / ProMotion sync        │
│                                                                                         │
└─────────────────────────────────────────────────────────────────────────────────────────┘
```

### Core Benefits of Terminal-First Architecture:
* **Zero UI Compositor Overhead:** Drops desktop UI CPU utilization to **0.0%**, saving 10–15% total SoC power and drastically reducing thermal load on fanless models (e.g. MacBook Air M2).
* **Zero UI Garbage Collection Churn:** Eliminates thousands of short-lived managed objects allocated per second by MVVM bindings and XAML trees, eliminating micro-stutters.
* **Instant Cold Boots (<50 ms):** Direct execution bypassing Avalonia theme compilation and asset loading.
* **Streamlined Automation:** Native integration with Raycast, Alfred, shell aliases, and EmulationStation/Pegasus frontends.

---

## 3. Apple Silicon Generational Scaling Matrix (M1 through M5/M6+)

Because our refactoring interfaces directly with **Darwin Mach kernel primitives, POSIX `mmap`, Metal 4 & Metal 3, and the Accelerate framework**, it is **architecturally forward- and backward-compatible** across the entire Apple Silicon family.

```
┌─────────────────────────────────────────────────────────────────────────────────────────┐
│                       APPLE SILICON GENERATIONAL COMPATIBILITY                          │
├─────────────────────────────────────────────────────────────────────────────────────────┤
│ [ M1 Family (M1 / M1 Pro / M1 Max / M1 Ultra) ]                                         │
│   - Bandwidth: 68 GB/s (Base) to 800 GB/s (Ultra)                                       │
│   - Benefit: Massive uplift. Eliminating Avalonia UI overhead frees crucial CPU/GPU     │
│     cycles on entry-level 8-core GPU M1 machines, making 60 FPS achievable.             │
├────────────────────────────────────────────────────────────────────────────────────────┤
│ [ M2 Family (M2 / M2 Pro / M2 Max / M2 Ultra) - Target Device ]                         │
│   - Bandwidth: 100 GB/s (Base) to 800 GB/s (Ultra)                                      │
│   - Benefit: Perfect sweet spot for fanless M2 Air. Mach QoS + MetalFX upscaling cuts   │
│     thermal load in half, preventing passive thermal throttling during long sessions.   │
├────────────────────────────────────────────────────────────────────────────────────────┤
│ [ M3 Family (M3 / M3 Pro / M3 Max) ]                                                    │
│   - Architecture: ARMv8.6-A + GPU Dynamic Caching + Mesh Shading                        │
│   - Benefit: Hardware Dynamic Caching optimizes zero-copy texture memory buffers;       │
│     ANE offloads MetalFX spatial upscaling with virtually 0% GPU penalty.               │
├────────────────────────────────────────────────────────────────────────────────────────┤
│ [ M4 Family (M4 / M4 Pro / M4 Max) ]                                                    │
│   - Architecture: ARMv9.2-A + SME (Scalable Matrix Extension) + Metal 4 API + 38 TOPS ANE│
│   - Benefit: 120 GB/s base bandwidth; native MTL4ArgumentTable support, multi-core      │
│     command encoding pools, pure hardware zero-copy binding, and 60 FPS lock.           │
├────────────────────────────────────────────────────────────────────────────────────────┤
│ [ Future M5 & M6 Architectures ]                                                        │
│   - Architecture: Advanced nodes, enhanced vector execution, higher UMA bandwidth.      │
│   - Benefit: 100% forward-compatible. No deprecated APIs, no intermediate translation   │
│     tax, and direct adherence to macOS Darwin kernel and native Metal standards.        │
└─────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## 4. Technical Grounding & Reality Matrix (Current Codebase vs Target State)

| Subsystem | Upstream Ryujinx Architecture | Ryu Native Apple Silicon Target |
| :--- | :--- | :--- |
| **GUI Framework** | Avalonia UI + Skia / XAML desktop windowing (~300MB idle RAM, heavy GC allocations). | **Astris-Style Headless Terminal CLI** + borderless `CAMetalLayer` native viewport with zero managed UI overhead. |
| **Memory Tracking** | 4KB coarse memory tracking running inside 16KB macOS pages via software mprotect page faults. | **HostMappedUnsafe (Zero-Copy UMA)** with fine-grained 4KB dirty bitmask tracking aligned to 16KB Darwin virtual memory. |
| **Cache Management** | Managed heap dictionaries and unmanaged RAM pools without OS memory eviction awareness. | Integrate **Darwin Mach Purgeable Memory (`VM_FLAGS_PURGABLE`)** for texture and shader caches to prevent macOS memory compression/SSD swap wear. |
| **Headless Frontend** | Headless mode was folded into the main project (`Ryujinx --no-gui`), pulling full Avalonia/Skia dependencies. | Extract a standalone, decoupled **`Ryujinx.Headless`** target with zero Avalonia/XAML dependencies, cutting ~300MB idle RAM and eliminating UI compositor overhead. |
| **Synchronization** | Standard .NET `Monitor` locks (`lock(obj)`), `ConcurrentQueue`, and `AutoResetEvent`. | Implement **Cache-Line Padded (128-byte) Lockless SPSC Ring Buffers** with Acquire-Release memory barriers across GPU and audio submission queues. |
| **Compilation Model** | Standard .NET JIT with runtime reflection in IPC dispatchers. | **Adjustment A:** Target **.NET 10 ReadyToRun (R2R) + Dynamic PGO** for instant startup and peak throughput, incrementally expanding Roslyn Source Generators toward NativeAOT. |
| **Thread Scheduling** | Standard managed .NET thread pool with default OS scheduling. | Direct Darwin Mach QoS bindings (`pthread_set_qos_class_self_np`) locking JIT/Render to **Performance Cores (P-Cores)** and background workers to **Efficiency Cores (E-Cores)**. |
| **Frame Timing** | .NET `Thread.Sleep` / spin-waiting loop in presentation. | Direct **`CVDisplayLink`** Darwin kernel synchronization using `[UnmanagedCallersOnly]` non-allocating callbacks, locking frame delivery with zero CPU spin-wait cycles. |
| **Graphics & Textures** | MoltenVK translation layer (single-threaded bottleneck at 150-250k commands/sec, capping heavy scenes at ~20 FPS). | **Native Metal 4 GAL Backend (`Ryujinx.Graphics.Metal`)**: Zero-copy `MTL4ArgumentTable` bindings, multi-threaded P-core command encoding, block-free `MTLSharedEvent` sync, and Metal 3 presentation/format-blit hybrid pipelines. |
| **Shader Compilation** | Live SPIR-V to MSL translation via `libspirv-cross.dylib` causing frame hitching and translation tax. | **Native C# MSL Generator (`CodeGen/Msl/`)**: Directly emit MSL from `StructuredProgramInfo`, verified via Golden Reference Diff Lock, backed by persistent `MTLBinaryArchive`. |
| **Audio Processing** | CoreAudio backend exists, but DSP mixing/biquads/resampling run in scalar C# loops. | Vectorize audio DSP pipelines using ARM64 **`AdvSimd` (NEON)** intrinsics and Apple **`Accelerate.framework` (`vDSP`)** with 16-byte aligned native memory buffers. |
| **Input Subsystem** | SDL3 controller polling with default event latency. | **Adjustment B:** Configure **Low-Latency SDL3 Apple Backend** with direct event polling and native Apple haptic/rumble dispatch. |
| **Git & Privacy** | Private keys and game files at risk of accidental tracking. | **Addition 3:** Strict **Git Safety & Privacy Lock** in `.gitignore` + macOS Hardened Runtime JIT/Hypervisor entitlements (bypassing restrictive App Sandbox). |

---

## 5. Architectural Additions & Strategic Adjustments

### Addition 1: Native Metal 4 GAL Architecture & M4/M3 Hybrid Engine
To overcome MoltenVK's single-core command encoding ceiling (~150–250k commands/s vs ~280k/s needed for NieR City Ruins at 30 FPS), Ryu implements a **direct native Metal backend**:
* **Metal 4 Command Queue & Allocator Pool (`Metal4CommandQueue`):** Per-thread allocators submitting batches via `commit:count:` to eliminate driver submission contention.
* **`MTL4ArgumentTable` Direct Binding:** Zero-copy buffer bindings via GPU address (`setAddress:atIndex:`), textures and samplers via `gpuResourceID`.
* **Block-Free Synchronization:** Inter-queue synchronization and CPU frame pacing via non-blocking `MTLSharedEvent` signaling.
* **Intentional Metal 3 Presentation & Blit Paths:** Metal 4 does not feature a blit encoder; Ryu intentionally pairs M4 render/compute pipelines with proven M3 presentation (`MetalWindow`) and format-converting blit (`MetalFormatBlit`) queues.
* Direct hardware **ASTC texture decode** passthrough directly into Metal.

### Addition 2: Dual CPU Execution Engine (`AppleHv` + `ARMeilleure`)
The repository contains the foundation for **`Ryujinx.Cpu.AppleHv`** ([src/Ryujinx.Cpu/AppleHv](src/Ryujinx.Cpu/AppleHv)):
* **Hypervisor Mode (`--cpu-backend=hypervisor`):** Executes 64-bit ARM instructions natively on Apple Silicon CPU hardware without dynamic recompilation (maximum raw throughput).
* **ARMeilleure JIT Mode (`--cpu-backend=jit`):** Uses our optimized JIT recompiler with inlined software fastmem and FPCR register sync (maximum compatibility for 32-bit titles and custom mods).

### Addition 3: Git Safety, Privacy & Sandbox Freedom
* **Git Lockdown:** Keep `nintendo games/`, `*.keys`, `*.xci`, `*.nsp`, `*.zip`, and firmware directories strictly locked in `.gitignore`.
* **Entitlements over App Sandbox:** Rather than enabling restrictive App Sandbox (which caused Astris's file permission popups), use macOS Hardened Runtime Entitlements (`com.apple.security.cs.allow-jit`, `com.apple.security.hypervisor`, `com.apple.security.cs.allow-unsigned-executable-memory`) for full, frictionless access to games and controllers.

### Adjustment A: .NET 10 ReadyToRun (R2R) + Dynamic PGO First
* Rather than risking IPC dispatch breakages with premature NativeAOT compilation, compile with **.NET 10 ReadyToRun (R2R) + Tiered Dynamic PGO**. This achieves instant cold starts and optimized machine code while preserving full HLE compatibility. Roslyn Source Generators will be expanded incrementally toward NativeAOT.

### Adjustment B: Low-Latency SDL3 Apple Backend Tuning
* Rather than building an entire custom input subsystem from scratch, leverage SDL3’s internal Apple `GCController` driver with direct low-latency polling flags and wire native Apple haptics into the HLE input layer.

---

## 6. Phased Implementation Master Plan

The graphics and Metal milestones are governed by the operational phases detailed in **[Native Metal Execution Roadmap](docs/native-metal-execution-roadmap.md)**:

```
[ Phase 0: Baseline & Diagnostics Checkpoint ]
  ├── Build validation (Ryujinx.Graphics.Metal and Ryujinx.Headless)
  └── 15/15 subsystems operational in Ryu --test diagnostic harness (PASSED)

[ Phase 1: Prove & Repair M4 Compute ]
  ├── Minimal M4 compute kernel with MTL4ArgumentTable output buffer
  └── Compute lifecycle audit & shared-event completion verification (PASSED)

[ Phase 2: Maxwell -> Metal 4 State Adapters & Virtual Input ]
  ├── Virtual input feeder in HeadlessRyujinx to advance past loading screens
  ├── Explicit swapchain target tracking via Horizon OS `vi` (replaces `LastDrawn`)
  ├── Maxwell TIC/TSC sampler deduplication (safely fits 18 samplers into <=16 slots)
  ├── Hardware texture component swizzling (`MTLTextureSwizzleChannels`)
  ├── Scissor bounds normalization and clamping to active attachments
  ├── Depth bias precision scaling and depth clamping (`MTLDepthClipModeClamp`)
  └── Gate: [READBACK] sawNonzero=true on a real varying 3D game frame

[ Phase 3: Verify M3/M4 Boundary & Synchronization ]
  ├── Verify intentional M3 presentation (MetalWindow) & blit (MetalFormatBlit)
  ├── Audit M4 pipeline to prevent M3 selector exceptions
  └── Synchronize M4 render completion to M3 drawable presentation via MTLSharedEvent

[ Phase 4: Performance, Thermal Budgeting & 30 FPS Lock (M2 Air) ]
  ├── Multi-threaded pass command encoding across Avalanche P-Cores
  ├── Strict 30.0 FPS frame cadence via Darwin CVDisplayLink to prevent thermal throttling
  ├── MetalFX Spatial upscaling to reduce pixel-fill thermal load by ~35%
  └── Persistent MTLBinaryArchive shader caching for stutter-free gameplay

[ Phase 5: Self-Containment: Native C# MSL Generator (Deferred) ]
  ├── Emits raw MSL from StructuredProgramInfo without libspirv-cross
  └── Golden Reference Binding Diff Lock verification
```

---

## 7. Verification & Benchmark Protocol

1. **1% Low Frame Times:** Frame time variance within $\pm 1.2\text{ ms}$ window during high-load scenes, confirming zero GC stalls.
2. **Resident Memory (RSS):** Headless baseline memory footprint $\le \text{Guest RAM } (4\text{ GB}) + 350\text{ MB}$.
3. **Input Polling Latency:** Controller event delivery latency $\le 1.0\text{ ms}$ via low-latency SDL3 Apple backend.
4. **Audio Latency & Load:** Audio DSP mixer overhead $< 0.5\%$ CPU with deterministic sub-5ms output buffering.
5. **Thermal Stability & Throughput:** Sustained $\ge 28\text{ FPS}$ avg in NieR City Ruins across a continuous 60-minute benchmark on fanless MacBook Air M2 without thermal degradation.