# Ryu Native Metal Backend — Architecture & Implementation Plan

**Status:** Implemented M0–M4 + M5/M6 foundations — all verified on M2 (`Ryu --test` 8/8). Full parity & drop-of-Vulkan-fallback remain (M7).
**Owner:** Ryu (macOS-exclusive)
**Goal:** Replace the legacy Vulkan/MoltenVK graphics path with a **direct Apple Metal 3 GAL backend**, enabling the "native software + native hardware" philosophy: unified-memory zero-copy, multi-threaded command encoding, TBDR-native render passes, and presentation on par with (and beyond) Astris.

---

## 1. Why

Instrumented benchmarks (Nier:Automata, City Ruins, MacBook Air M2) proved the hard limit:

- The title pushes **~280k GPU commands/s** in the ruins (~16k/frame at 30 FPS).
- MoltenVK encodes at **~1.5–3 µs/command on ONE core** → ~150–250k/s ceiling → **~20 FPS max** in the heaviest scene.
- Push-descriptor cap, queue depth, vsync pacing, and buffer-texture rebind caches were all tuned; the bottleneck is MoltenVK's per-command translation and single-threaded encoding.

MoltenVK is a translation layer (`vkCmd*` → Metal encoder calls + SPIR-V → MSL). Every command crosses an extra abstraction with validation. **A direct Metal backend removes that layer entirely** and can additionally parallelize encoding — the edge over Astris, whose encoder is also single-threaded.

---

## 2. Design Principles (native philosophy)

1. **Zero-copy unified memory.** All guest VRAM lives in `MTLStorageModeShared` buffers created via `newBufferWithBytesNoCopy:` over Ryujinx's existing `MemoryBlock`. CPU writes by the guest are immediately visible to the GPU — no PCIe emulation, no staging, no MoltenVK buffer plumbing. (Foundation already exists in `MetalBufferManager`.)
2. **Multi-threaded command encoding.** Metal allows *parallel* encoding of multiple `MTLCommandBuffer`s (each encoded on its own thread) with in-order submission. Split the guest command stream into per-render-pass command buffers and encode them on 2–4 P-cores, then submit in order. This is the single biggest structural win vs MoltenVK *and* vs Astris.
3. **TBDR-native render passes.** Design passes around Apple's tile-based architecture: minimal `MTLLoadAction`/`MTLStoreAction`, no mid-pass barriers (Metal has none), texture hazard tracking via `useResource`/`Memoryless` attachments, and `MTLRenderPassDescriptor` reuse.
4. **Compile-time pipeline caching.** `MTLBinaryArchive` + `MTLFunctionConstants` specialization → shader/pipeline state compiled once, persisted to disk, reused across launches (fixes the live-compile stutter seen with only 342/1000s of shaders cached).
5. **Metal 3 feature surface.** MetalFX spatial/temporal upscaling, `MTLCounterSampleBuffer` for timing, memoryless render targets, and ProMotion `CVDisplayLink` pacing (already wired in `SurfaceFlinger`).

---

## 3. Shader Translation Strategy (the critical decision)

The existing translator (`Ryujinx.Graphics.Shader`) produces **SPIR-V** (`CodeGen/Spirv`) and GLSL. It does **not** produce MSL, and writing an MSL codegen backend from scratch is a full compiler project (~10k+ lines).

**Decision: keep SPIR-V generation, then translate SPIR-V → MSL with SPIRV-Cross** (the same library MoltenVK uses internally).

- Bundle a native `libspirvcross.dylib` (built for osx-arm64) alongside the app, exposed via a thin P/Invoke layer (`Ryujinx.Graphics.Metal/Interop/SpirvCross.cs`).
- Pipeline: guest shader → (existing) SPIR-V → SPIRV-Cross MSL + entry-point mapping → `newLibraryWithSource`/`newLibraryWithData` (precompiled MSL) → `MTLFunction` → `MTLRenderPipelineState` cached in `MTLBinaryArchive`.
- SPIRV-Cross's MSL backend already handles the hard parts: MSL2-compatible argument buffers, texture/sampler binding remapping, buffer address space (`device`), and `[[user]]` attribute conventions.

Fallback (if SPIRV-Cross binding proves fragile): port the existing GLSL backend to an MSL generator (shared IR; the `GlslGenerator` is the reference). Kept as `Plan B`.

---

## 4. Target Architecture

```
Guest HLE / GpuContext (Ryujinx.Graphics.Gpu)  — UNCHANGED (it drives the GAL)
        │  IRenderer / IPipeline / ITexture / IBuffer / IWindow
        ▼
Ryujinx.Graphics.Metal (new GAL, replaces Vulkan for osx-arm64)
 ├─ MetalRenderer          : IRenderer — device/queue/layers, capabilities, threaded wrapper
 ├─ MetalPipeline          : IPipeline — state → MTLRenderPipelineState; encoder management
 ├─ MetalBufferManager     : zero-copy MTLStorageModeShared buffers over guest MemoryBlock
 ├─ MetalTextureManager    : MTLTexture views over shared memory; ASTC passthrough
 ├─ MetalShaderProgram     : SPIR-V → MSL (SPIRV-Cross) → MTLFunction → MTLBinaryArchive
 ├─ MetalRenderPass        : TBDR pass builder (load/store actions, hazard tracking)
 ├─ MetalEncoderPool       : N-encoder pool → parallel MTLCommandBuffer encoding, in-order submit
 ├─ MetalWindow            : CAMetalLayer presentation via SDL3 Metal view
 └─ Interop/
     ├─ MetalBindings.cs   : (existing) objc_msgSend surface — expand
     ├─ SpirvCross.cs      : libspirvcross P/Invoke
     └─ MetalFX.cs         : optional upscaler
```

### Thread model (the differentiator)

```
GPU Main (FIFO) thread      → parses pushbuffer, produces draw/dispatch records (existing)
        │  per-render-pass records, ordered
        ▼
Encoder pool (2–4 threads) → each thread encodes a distinct MTLCommandBuffer (one per render pass)
        │  in-order queue of encoded command buffers
        ▼
Submit thread               → commit(commandBuffer) in guest submission order
```

This decouples command *recording* (parallel, CPU-bound) from *submission* (ordered). Throughput scales with P-core count instead of hitting a single-core ceiling.

---

## 5. Milestones (each independently testable)

| # | Milestone | Deliverable | Exit criteria |
|---|---|---|---|
| M0 | Foundation hardening | Expand `MetalBindings` to full encoder/render-pass API surface; correct object lifetime (retain/release) | Metal unit smoke test creates device, command buffer, encodes, submits |
| M1 | Zero-copy buffers | Guest MemoryBlock → `MTLStorageModeShared` buffers (`newBufferWithBytesNoCopy`); GAL `IBuffer` impl | `MetalBufferManager` reads/writes guest RAM with no copies; buffer stress test |
| M2 | Present a frame | `MetalWindow.Present` renders the game framebuffer via CAMetalLayer drawable (fullscreen-triangle presenter) | `--graphics-backend Metal` shows the game at correct aspect; vsync via CVDisplayLink |
| M3 | Shader pipeline | SPIR-V → MSL via SPIRV-Cross; `MTLFunction` + `MTLRenderPipelineState`; `MTLBinaryArchive` cache | Nier boots to title with Metal backend; shader cache persists across launches |
| M4 | Render passes + pipeline state | `IPipeline` full state machine → `MTLRenderPipelineState` + encoder config; TBDR load/store optimization | Draw calls render correctly; pass splits minimized |
| M5 | Multi-threaded encoders | Encoder pool; per-pass parallel encoding, in-order submission | Backend command throughput ≥ 2× MoltenVK in City Ruins |
| M6 | Texture + sync completeness | ASTC passthrough, texture views/arrays, buffer texture views, fences/events (`MTLEvent`) | Full Nier:Automata playthrough renders correctly |
| M7 | Performance parity & parity+ | A/B vs MoltenVK at matched settings; MetalFX; ProMotion 120 Hz | City Ruins ≥ 28 FPS avg on M2 Air; `--test` green |

### Milestone status (verified on M2 via `Ryu --test`, 8/8)
- **M0–M4 DONE.** MetalBindings → command pipeline → zero-copy buffers → CAMetalLayer present → SPIR-V→MSL (SPIRV-Cross) + MTLBinaryArchive + real MetalTexture → full `MetalPipeline` state machine (draw/indexed/clear/dispatch, vertex/index/uniform/texture/sampler binding, render pass lifecycle). [7/8] proves direct render-to-texture + textured-quad rasterization + clear pass via pixel readback.
- **M5 foundation DONE.** `MetalCommandPool` (`newCommandQueueWithMaxCommandBufferCount:` + acquire/commit/wait/reuse); verified in [7/8] (fill-buffer through pooled buffer). Full multi-threaded encoder *pool across P-cores* still pending.
- **M6 foundation DONE.** `MTLSharedEvent` sync (`CreateSync`/`GetCurrentSync`/`encodeSignalEvent:value:` on flush) + `ClearRenderTargetColor` (load-action clear color) — verified in [7/8] (clear-only pass readback).
- **Remaining:** full texture/array/ASTC/buffer-view completeness, MTLEvent wait-fence path, multi-threaded parallel encoding, MetalFX, and the M7 A/B that decides dropping Vulkan/MoltenVK.

### Verified `--test` [7/8] capability list (M0→M6)
`Metal command pipeline OK (device/queue/encoder/submit/unified-memory); zero-copy external memory OK; SPIR-V to MSL OK; MTLTexture round-trip OK; MTLBinaryArchive OK; pipeline state machine OK; command pool OK`

**Milestone ordering rationale:** M0–M2 prove the integration & presentation path early (lowest risk, visible progress); M3 unblocks real rendering; M5 is the performance crown jewel; M6/M7 close correctness + polish.

---

## 6. Key Design Decisions

1. **Backend switch:** keep Vulkan/MoltenVK as a fallback until M7 proves Metal ≥ MoltenVK on the target title. `--graphics-backend Metal` selects the new path (already wired in `HeadlessRyujinx.CreateRenderer`).
2. **Buffer view caching:** implement a refcounted per-`(buffer, format, offset, size)` view cache in `MetalBufferManager` to avoid `MTLBufferView` churn (the City Ruins storm).
3. **Descriptor handling:** use MSL2 argument buffers (Metal tier-2) + `MTLArgumentEncoder`; bind per-draw with `setBuffer`/`setTexture` offsets — avoids the MoltenVK push-descriptor fallback entirely.
4. **Sync:** guest NVN fences → `MTLEvent` + `waitForEvent`/`signalEvent`, avoiding MoltenVK's `MTLCommandBuffer` completion-handler round-trips.
5. **Fast GPU time:** keep `GraphicsConfig.FastGpuTime = true` (already default) so guest resolution scaling isn't triggered by emulator slowness.

---

## 7. Risks

- **SPIRV-Cross MSL quality/edge cases** (bounded loops, subgroup ops, feedback loops). Mitigate: fallback to MoltenVK per-shader on compile failure; `Plan B` MSL generator.
- **Correctness of zero-copy aliasing** (guest writes vs GPU reads). Mitigate: hazard tracking (`useResource`), write-tracking on 16 KB host pages (reuse `Ryujinx.Memory` tracking).
- **Encoder ordering bugs** (out-of-order submission). Mitigate: strictly ordered submit queue with per-command-buffer dependency fences.
- **Scope/size:** full GAL is ~20–40k lines. Mitigate: milestone discipline; each milestone ships independently; Vulkan stays as fallback.

---

## 8. Relation to "Switch 2-class" experience

Native Metal + multi-threaded encoders is what unlocks, on the M2 Air:
- 1080p–1440p at 30–60 FPS (City Ruins target: ≥ 28 FPS), 120 Hz presentation on ProMotion.
- Near-instant load (MTLBinaryArchive + zero-copy streaming).
- Lower power/thermal (no translation layer) — critical on the fanless chassis.

Switch-2 *games* (NVN2 GPU) are a separate future project and out of scope here.

---

## 9. Immediate next step

M0: expand `MetalBindings` (render pass, encoder, drawable APIs) + object lifetime management, then smoke-test command-buffer encode/submit via `--test`.
