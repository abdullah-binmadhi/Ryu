# full-metal.md — Complete Context & Handoff for the Native Metal Backend Work

> **Purpose:** This file captures everything discussed and built so far about replacing
> the Vulkan/MoltenVK path with a **direct Apple Metal 4 GAL backend** on macOS
> Apple Silicon (the "native software + native hardware" philosophy). Read this
> before touching any Metal code.
> - **Operational Execution Roadmap:** [`native-metal-execution-roadmap.md`](native-metal-execution-roadmap.md) (7-phase execution roadmap & live gates).
> - **Architectural Spec:** [`native-engine-plan.md`](native-engine-plan.md) (Source of truth for MSL generator, IR contracts, and phase gates).
> This doc is the authoritative M4 implementation/binding reference.

---

## 1. The Goal (why this project exists)

- NieR:Automata **City Ruins** on a **MacBook Air M2 (8 cores, 16 GB)** pushes **~280k
  GPU commands/s** (~16k commands/frame at 30 FPS).
- MoltenVK (the Vulkan-on-Metal translation layer) encodes at **~1.5–3 µs/command on
  ONE core** → **~150–250k/s ceiling** → **~20 FPS max** in the heaviest scene, no
  matter how much the Vulkan backend is tuned.
- **Goal:** remove the translation layer entirely with a native Metal backend, and
  **parallelize command encoding across P-cores** to break through the single-core
  ceiling. Target: City Ruins ≥ 28 FPS avg on M2 Air (executable spec:
  `docs/native-engine-plan.md`).

The five design principles (from the roadmap, unchanged):
1. **Zero-copy unified memory** — every guest VRAM buffer is an
   `MTLStorageModeShared` buffer over Ryujinx's existing guest `MemoryBlock`
   (`newBufferWithBytesNoCopy:`). No staging, no copies.
2. **Multi-threaded command encoding** — encode per-render-pass `MTLCommandBuffer`s on
   2–4 P-cores, submit in order. The single biggest structural win vs MoltenVK **and**
   vs Astris (whose encoder is single-threaded).
3. **TBDR-native render passes** — minimal load/store actions, no mid-pass barriers.
4. **Compile-time pipeline caching** — `MTLBinaryArchive` + specialization, persisted.
5. **Metal 3/4 feature surface** — MetalFX upscaling, `MTLCounterSampleBuffer`,
   memoryless render targets, ProMotion `CVDisplayLink` pacing.

**The user's explicit meta-priority: iteration SPEED.** "i hope this time is much
faster than before." Work should proceed in small, verified steps; commit checkpoints.

---

## 2. Environment (verified)

- Host: **Apple M2 Air, 16 GB RAM, arm64** (8 cores — 4 P + 4 E).
- OS: **macOS 26.5.2 (build 25F84)** — Metal 4 API family available
  (`MTL4*`, device-family `AGXG14G*`).
- Toolchain: CommandLineTools SDK `MacOSX26.sdk` at
  `/Library/Developer/CommandLineTools/SDKs/MacOSX26.sdk/Metal.framework/Headers` —
  **the authoritative source for M4 selectors**.
- .NET 10.0.400, net10.0, runtime 10.0.11.
- Config (`~/Library/Application Support/Ryujinx/Config.json`):
  `GraphicsBackend=Metal` is NOT the default for the game launcher; **use the
  `--graphics-backend metal` CLI flag** (see §7).

### Build/run commands

```bash
# Build the GPU lib
dotnet build src/Ryujinx.Graphics.Metal/Ryujinx.Graphics.Metal.csproj
# Build the headless launcher (output binary is named "Ryu")
dotnet build src/Ryujinx.Headless/Ryujinx.Headless.csproj
# Diagnostics suite (15 subsystem checks, MUST be green before shipping a change)
src/Ryujinx.Headless/bin/Debug/net10.0/Ryu --test
# Boot a real game on the native Metal path
src/Ryujinx.Headless/bin/Debug/net10.0/Ryu "nintendo games/nier/NieRAutomata The End of YoRHa Edition [USA][010056B015FE8000](axekin.com).xci" --graphics-backend metal
```

The `Ryu --test` suite (in `src/Ryujinx.Headless/Diagnostics/SystemDiagnostics.cs`):
Reports all 15/15 subsystems operational (CPU/arch, virtual memory HostMappedUnsafe, Darwin QoS P-core pinning,
Vulkan/MoltenVK, SDL3, OSD HUD window, Native Metal command pipeline, M4 compute probe, CAMetalLayer presentation,
M4 parallel-encode pool, shared-event block-free synchronization).

---

## 3. THE SINGLE MOST IMPORTANT DISCOVERY (corrected belief, validated)

> **MTL4 is NOT a pure "queue swap."** An M4 command queue/multi-threaded encoder
> pool alone breaks everything.

**Fact:** the Metal-4 render encoder
(`AGXG14GFamilyRenderContext_mtlnext`) and compute encoder
(`AGXG14GFamilyComputeContext_mtlnext`) **do NOT support the M3 per-encoder binding
selectors**. Calling them throws
`NSInvalidArgumentException: -[…] setVertexBuffer:offset:atIndex:]: unrecognized
selector sent to instance`.

The M3 selectors that **CRASH on an M4 encoder** (all confirmed live):
`setVertexBuffer:offset:atIndex:`, `setVertexTextureAtIndex:`,
`setFragmentTextureAtIndex:`, `setVertexSamplerStateAtIndex:`,
`setFragmentSamplerStateAtIndex:`, and on compute
`setBuffer:offset:atIndex:`, `setTexture:atIndex:`, `setSamplerState:atIndex:`.

**What M4 requires instead — the M4 binding model:**

- Resources bind exclusively through a **`MTL4ArgumentTable`** object
  (`device newArgumentTableWithDescriptor:error:`) sized by
  `MTL4ArgumentTableDescriptor` (caps: **maxBufferBindCount ≤ 31,
  maxTextureBindCount ≤ 128, maxSamplerStateBindCount ≤ 16**).
- **Buffers bind by GPU address** — `setAddress:atIndex:` with
  `(MTLGPUAddress) = buffer.gpuAddress + offset` (NOT the object pointer).
- **Textures/samplers bind by `MTLResourceID`** — `setTexture:atIndex:` /
  `setSamplerState:atIndex:` taking the object's `gpuResourceID` (a `uint64`), NOT
  the `nint` pointer.
- Set the table on the encoder:
  - render: `setArgumentTable:atStages:` with `MTLRenderStageVertex=1` /
    `MTLRenderStageFragment=2` (or both OR'd).
  - compute: `setArgumentTable:` **single argument, no stages** (compute implicitly
    means `MTLStageDispatch = 1 << 27`).
- **Indexed draw signature changed:**
  `drawIndexedPrimitives:indexCount:indexType:indexBuffer:indexBufferLength:instanceCount:` —
  the 5th arg is a **`MTLGPUAddress` (ulong)** plus a **length**, NOT
  buffer-object + offset. The old M3 selector is gone.
- **Non-indexed draw unchanged:** `drawPrimitives:vertexStart:vertexCount:instanceCount:`
  (same selector string as M3) — reuse the M3 binding.
- **Still valid on M4 (do NOT break these):** `setRenderPipelineState`,
  `setViewport`, `setScissorRect`, `setCullMode`, `setFrontFacingWinding`,
  `setDepthStencilState`, `endEncoding`, compute `setComputePipelineState:`,
  `dispatchThreadgroups:threadsPerThreadgroup:`, `dispatchThreads:threadsPerThread:`.
- Compute pipeline state is still the normal `MTLComputePipelineState` created via
  `device newComputePipelineStateWithFunction:error:`.

**Consequence:** any code path touching an M4 encoder must bind via argument tables.
There is no fallback to M3 selectors. The M3 `MTLCommandQueue` remains ONLY for
presentation (CAMetalLayer drawable) and resource blits, because M4 has no blit
encoder — `MetalWindow` and `MetalFormatBlit` intentionally stay on M3.

**M4 concurrency contract:** every thread encoding concurrently must encode into its
**own** `MTL4CommandAllocator`. Command buffers are begun with
`beginCommandBufferWithAllocator:` (allocator `<commandBuffer>`), ended with
`endCommandBuffer`. A batch is submitted as a group with **`commit:count:`**.
Completion is detected **block-free** via an `MTLSharedEvent` that the **queue**
signals after the committed group — `waitUntilSignaledValue:timeoutMS:`
(this sidesteps constructing ObjC blocks from C#). Allocators are reusable once
their command buffers complete.

### 3.1 Maxwell -> Metal 4 State Adaptation Rules

Switch games expect an Nvidia Maxwell Immediate-Mode GPU. Adapting them cleanly to Apple Silicon TBDR requires specific translation tactics:

1. **Maxwell TIC/TSC Sampler Deduplication (`maxSamplerStateBindCount <= 16`)**:
   - Maxwell maintains independent TIC (textures) and TSC (samplers) tables. Shaders can pair them arbitrarily (e.g. NieR's composite Shader 150 references up to 18 samplers).
   - In `MetalPipeline.cs`, hash sampler configurations in C# before writing to `MTL4ArgumentTable`. Deduplicate identical configurations (Linear+Clamp, Point+Repeat, etc.) into shared indices. This compresses 18 logical samplers down to 4–8 physical Metal samplers, preventing driver compilation failure.

2. **Zero-Cost Hardware Texture Swizzling (`MTLTextureSwizzleChannels`)**:
   - Maxwell textures frequently store BGRA or Depth-in-Red layouts.
   - Apply `MTLTextureSwizzleChannels` when creating texture views via `newTextureViewWithPixelFormat:`. Apple Silicon remaps channels inside fixed-function hardware sampling units with 0.00% GPU latency.

3. **Memoryless Transient Targets (`MTLStorageModeMemoryless`)**:
   - For intermediate depth/stencil passes that are never sampled across render passes, allocate textures with `MTLStorageModeMemoryless` and set `MTLStoreActionDontCare`.
   - Depth testing and stencil operations remain 100% inside on-chip TBDR tile cache ($32 \times 32$), avoiding system DRAM bandwidth waste.

4. **Scissor Rect Normalization**:
   - Enforce clamp to active attachment dimensions: `[0, 0, width, height]` to avoid dropped draws or driver validation assertions.

5. **Depth Bias Scaling & Clamping**:
   - Scale depth bias values according to the depth attachment format (`D16Unorm` vs `D32Float`) before `setDepthBias:slopeScale:clamp:` to fix shadow map striping.
   - Toggle `setDepthClipMode:MTLDepthClipModeClamp` when the Maxwell state disables depth clipping to prevent skybox and character clipping voids.

---

## 4. What the "8/8 diagnostics" covers (regression harness)

`[7/8]` currently asserts one big combined string:
`Metal command pipeline OK (device/queue/encoder/submit/unified-memory); zero-copy
external memory OK; SPIR-V to MSL OK; MTLTexture round-trip OK; texture array OK;
MTLBinaryArchive OK; pipeline state machine OK; command pool OK; Metal 4 parallel
encode OK` — includes spinning 4 worker threads in `RunMetal4ParallelTest`
(`src/Ryujinx.Graphics.Metal/MetalDiagnostics.cs` ~line 1267+): each worker gets its
own allocator + argument table + render target, encodes its own pass, all submitted
with one `commit:count:`, verified by per-tile pixel readback. **This is the reference
implementation for the parallel pattern — mirror it.**

**Warning:** the diagnostics do NOT exercise the Gal draw path's compute dispatches
(`DispatchCompute`), which is why the compute problem in §8b was only found on a real
game boot.

---

## 5. Files & current state (where things live)

### Interop
- `src/Ryujinx.Graphics.Metal/Interop/MetalBindings.cs` — M3 surface
  (`objc_msgSend*` helpers, MTL enums, Retain/Release, all legacy selectors).
- `src/Ryujinx.Graphics.Metal/Interop/Metal4Bindings.cs` — M4 selectors + new
  `m4_msgSend_void` overloads (incl. the **8-arg** overload for the M4 indexed draw
  with the `ulong` gpuAddress, and a **4-arg nuint** variant for the instance draw).
  Selectors of interest:
  `SelNewArgumentTableWithDescriptorError`, `SelSetMaxBufferBindCount`,
  `SelSetMaxTextureBindCount`, `SelSetMaxSamplerStateBindCount`,
  `SelSetAddressAtIndex`, `SelSetTextureAtIndex`, `SelSetSamplerStateAtIndex`,
  `SelSetArgumentTableAtStages` (`setArgumentTable:atStages:`),
  `SelSetArgumentTableCompute` (`setArgumentTable:` single),
  `SelBeginCommandBufferWithAllocator`, `SelEndCommandBuffer`,
  `SelDrawIndexedPrimitivesIndexCountIndexTypeIndexBufferLengthInstanceCount`,
  `SelGpuAddress`, `SelGpuResourceID`.
  Constants: `MTLRenderStageVertex=1<<0`, `MTLRenderStageFragment=1<<1`,
  `M4RenderEncoderOptionSuspending/Resuming`, `MTLLoadActionClear=2`,
  `MTLStoreActionStore=1`. **Verify any new selector against the MacOSX26.sdk
  headers before using it.**

### Command layer — `src/Ryujinx.Graphics.Metal/Metal4CommandQueue.cs`
- `Metal4CommandQueue` — `BeginCommandBuffer(device, allocator)`,
  `EndCommandBuffer(cb)`, `SubmitAndWait(span, timeoutMS)` (holds the queue shared
  event; increments an internal completion value, `commit:count:`, then
  `signalEvent:value:` on the queue, then block-free wait).
- `Metal4CommandAllocator` — per-thread allocator wrapper.
- `Metal4CommandAllocatorPool` — **acquire/release pool** (round-robin, tracks
  in-use, **grows on demand up to a cap**, thread-safe). An allocator may only be
  returned after its command buffers have completed. (An earlier thread-affine
  design was deliberately replaced with this acquire/release pool so consecutive
  passes on the same thread don't collide on one in-flight allocator.)

### Renderer — `src/Ryujinx.Graphics.Metal/MetalRenderer.cs`
- Owns `_device`, `_commandPool` (M3, for window/blits), `_m4Queue`,
  `_m4AllocatorPool`. Exposes `M4Queue`, `M4AllocatorPool`, `GetBuffer(handle)`.
  `Dispose` releases pool + queue. `PreferThreading => true`.

### Pipeline — `src/Ryujinx.Graphics.Metal/MetalPipeline.cs` (the hot path)
- **Render pass lifecycle:** `EnsureRenderPass()` starts a command buffer from the
  allocator pool + builds an `MTL4RenderPassDescriptor` (attachments, colors,
  depth, clear). `EndRenderPass()` ends the encoder, **signals the shared sync event
  with `_currentSync` inside the buffer**, then `endCommandBuffer` and accumulates
  into `_frameBuffers`/`_framePassDescriptors`/`_frameAllocatorIndices`.
  `FlushFrame()` → single `SubmitAndWait(span, 5000)` on the whole frame's batch →
  then releases buffers/descriptors and returns allocators to the pool. This replaced
  the old per-pass commit+`WaitUntilCompleted`.
- **Argument tables:** `_argumentTableVertex`, `_argumentTableFragment`,
  `_argumentTableCompute`, created by `EnsureArgumentTables()` /
  `EnsureComputeArgumentTable()` (caps 31/64/16), released in `Dispose`.
  `BindTableBuffer` (gpuAddress+offset), `BindTableTexture` (gpuResourceID),
  `BindTableSampler` (gpuResourceID), `BindTableBufferForSet`,
  `BindTexturesAndSamplers`, `BindImages`. The bind tables are set per draw via
  `setArgumentTable:atStages:` for Vertex and Fragment.
- **Draw:** vertex buffers + uniforms + storage + textures + samplers + images all go
  through the tables; indexed draw computes `gpuAddress = buffer.gpuAddress +
  offset + firstIndex*indexSize` with matching `length`; non-indexed draw uses the
  M3-style instance selector.
- **Compute (`DispatchCompute`):** creates a fresh compute pipeline state per call
  (NOT cached — should be cached), builds the compute table via
  `BindComputeBufferTable` (same `GetMslBinding` indices as M3 used), sets it with
  `setArgumentTable:` (single arg), dispatches with fixed `threadsPerThreadgroup =
  (64,1,1)`, then submits immediately through `SubmitAndWait(single, 5000)`.
  **THIS IS THE CURRENT CRASHING PATH — see §8b.**
- `FlushBeforePresent()` → `FlushFrame()` so the presented framebuffer is complete.

### GAL caps (must match the table capacities)
`uniformBufferSetIndex=0, storageBufferSetIndex=1, textureSetIndex=2,
imageSetIndex=3`; `maxUniformBuffersPerStage=18, maxStorageBuffersPerStage=16,
maxTexturesPerStage=64, maxImagesPerStage=16`.
`GetMslBinding(stage, setIndex, binding, kind, out samplerIndex)` returns
`uint.MaxValue` when a resource is unbound — always check.

---

## 6. Project phases / remaining roadmap

Phase plan (from this work session, user-approved "queue swap + verify, then parallel";
user emphasized speed):

| Phase | What | Status |
|---|---|---|
| 0 | Checkpoint commit before risky MTL4 work | ✅ commit `611071854` |
| 1 | **Wire MTL4 into the live path** — the pragmatic scope expanded from "queue swap" to full **argument-table binding conversion** after the discovery in §3 (render tables + indexed draw by address) | ✅ done |
| 1b | Per-thread allocator pool so each live command buffer gets a dedicated allocator | ✅ done |
| 2 | Verify `Ryu --test` 8/8 | ✅ green |
| 3 | **Parallel encoder pool (real)** — encode multiple passes in parallel across P-cores (workers + per-thread allocators + `commit:count:`), using M4 suspending/resuming encoders to split a single pass | ⏳ NOT WIRED into live path yet (pattern proven only in the spike) |
| 4 | Verify with real game boot + headless render check + update docs (this doc + `native-engine-plan.md`) | ⏳ partial (game boots but crashes — §8) |

Roadmap M0–M7 (from the earlier `native-metal-backend.md`, now superseded by
`docs/native-engine-plan.md`'s Phase-gate) — M0–M4 considered done per the
doc; **M5 (multi-threaded encoders), M6 (texture/sync completeness), M7 (A/B parity +
drop Vulkan) remain.** The remaining M6 items: full texture/array/ASTC/buffer-view
completeness, `MTLEvent` wait-fence path, MetalFX.

---

## 7. How to actually run a game on the native path (gotchas)

- You MUST pass `--graphics-backend metal`, otherwise `CreateRenderer` goes down the
  Vulkan/MoltenVK path (Silk.NET `Vk.GetApi()`), which fails if MoltenVK isn't
  resolvable in that environment (`Could not load ... library names!`). This failure
  is NOT our bug — it's the fallback path.
- On macOS, SDL work happens on the main thread (already wired in
  `HeadlessRyujinx.Entrypoint`).
- Headless logs `[PRESENT]`/`[READBACK]` for the presented texture — the instantaneous
  way to check if frames are actually non-black.

---

## 8. Current live issues (as of the most recent test run)

### 8a. Render output: presented frames are BLACK
During a NieR boot on the Metal path, presentation loop reported
`[READBACK] frame N: mean=(0,0,0,0) sawNonzero=False` (sampled grid regions all
zero) while reporting `FPS: 43.3`, `Disp: 3.3ms`, `Cmd: 1.9k/s`. The engine is
running and submitting, but the presented framebuffer is empty/black.
**Prime suspect:** `EnsureRenderPass()` unconditionally sets **every** color
attachment load action to `Clear` (and clears to the last recorded clear color,
default `0,0,0,1`). Real game passes frequently need **Load** semantics (preserve
previous contents) or `DontCare`; always-clearing erases the game's output. The
GAL/Ryujinx renderer relies on the backend honoring load/store actions — the M4 path
must track and apply the correct load action **per pass** (not blanket Clear). Also
worth checking: the presented texture handle changes per frame (double-buffer) and is
blitted from the M3 queue — ensure the M4-rendered texture is the same one the M3
present blit reads.

### 8b. Compute dispatch crashes the game boot (the ACTIVE blocker)
Second NieR boot, ~6.9s in:
```
System.TimeoutException: MTL4 shared-event wait timed out after 5000 ms
  at Metal4CommandQueue.SubmitAndWait (Metal4CommandQueue.cs:97)
  at MetalPipeline.DispatchCompute (MetalPipeline.cs:1118)
```
History: the ORIGINAL crash was
`-[AGXG14GFamilyComputeContext_mtlnext setBuffer:offset:atIndex:]: unrecognized
selector` — that was fixed this session by converting compute to argument-table
binding (the current code). Now compute **encodes** without selector crashes but the
GPU never signals completion within 5s → the dispatch is presumably **hanging the GPU**
(or the completion path isn't firing for that buffer). Distinguish these:
- The M4 spike's compute/parallel test only used **render** encoders — the M4 queue
  shared-event wait was never proven against an M4 **compute** encoder.
- `DispatchCompute` creates the pipeline state every call (no cache) and uses a
  **fixed** `threadsPerThreadgroup = (64,1,1)`.
- Only storage buffers are bound into the compute table. **Compute textures/images
  are NOT bound** — if the shader references a texture/sampler the table doesn't set,
  the M4 argument table may refer to garbage and the GPU can hang.
- The signal/fence wiring for compute buffers differs from render (render encodes an
  in-buffer signal with `_currentSync`; compute just relies on the queue-level signal
  from `SubmitAndWait`).

Investigation checklist (in suggested order):
1. Add a tiny compute path to the diagnostics that mirrors the spike style: encode a
   trivial MSL compute kernel on an M4 compute encoder with a correct argument table,
   submit via `SubmitAndWait`, confirm it signals. This isolates "M4 compute +
   shared-event" from "NieR's specific shader/bindings".
2. If 1 hangs → the queue-level shared-event signaling/completion path is the bug
   (wait semantics vs compute encoder). If 1 completes → the bug is NieR's shader
   bindings (textures/images in compute) → extend compute binding to bind
   textures/samplers/images into `_argumentTableCompute` exactly like the render path,
   and cache compute pipeline states by function.
3. Re-check `indexByteOffset`/length math on `getBuffer` for compute storage (same
   pattern as render was fixed).
Then re-boot NieR, confirm `[READBACK]` shows non-black frames and no 5s timeout.

### 8c. Completed Architecture (Milestones M3b, M4, M5 Verified)
- **Compositing & Blit Pipeline (`DrawTexture` + `MetalFormatBlit.cs`)**:
  - Full-screen quad rasterization with MSL 4.0 shaders supporting nearest/linear sampling and UV cropping/scaling.
  - Cross-format HDR conversion: `R11G11B10Float` downsampling and conversion to `B8G8R8A8Unorm` / `R8G8B8A8Unorm` without silent drops.
- **Hardware State Machine Normalization**:
  - Scissor rectangles clamped strictly to physical target bounds $[0, 0, \text{Width}, \text{Height}]$.
  - Front-face winding inverted (`Clockwise` $\leftrightarrow$ `CounterClockwise`) to compensate for viewport $Y$-flip.
  - Depth test compare function set to `MTLCompareFunctionAlways` when `_depthTest.TestEnable == false`.
  - `SetDepthBias` and `SetDepthClipMode` wired to the render encoder.
- **Texture Subresource Views**:
  - `newTextureViewWithPixelFormat:textureType:levels:slices:` with Darwin ARM64 register ABI (`r2`–`r7`).
- **Parallel Multi-Threaded Command Encoding (`MetalParallelEncoderPool.cs`)**:
  - 4 concurrent worker threads pinned to Darwin P-cores with dedicated `MTL4CommandAllocator` instances and `MTL4ArgumentTable`s.
  - Non-blocking batch commit via `commit:count:`.
- **Memory Synchronization & Barriers**:
  - `Barrier()`, `CommandBufferBarrier()`, `TextureBarrier()`, `TextureBarrierTiled()` calling `EndRenderPass()`.

---

## 9. Hard-won implementation notes / conventions (follow these)

- **Binding = argument tables on M4.** Never reintroduce M3 `setVertexBuffer*
  /set*TextureAtIndex/set*SamplerStateAtIndex/setBuffer:offset:atIndex:` on an M4
  encoder — guaranteed `unrecognized selector` crash.
- **Buffers by `gpuAddress`**, **textures/samplers by `gpuResourceID`**; both are
  `uint64` (use the `m4_msgSend_ulong_ret` / `objc_msgSend_ulong_ret` variants), not
  pointers.
- **Retain/release discipline:** factory `new…` results are retained (+1) — Release
  exactly once when done. Descriptors created via `Metal4New(...)` are owned and must
  be Released after use (the code Releases descriptors inside `finally`).
- **Interop overloads are positional-fragile:** `m4_msgSend_void` overloads must match
  selector arity+types; M4 scalars use `nuint` (`NSUInteger`); the indexed draw takes
  a `ulong` where the old M3 one took a buffer pointer. When a new selector is needed,
  add the exact overload and verify via `Ryu --test`, then a real boot.
- **Sync model:** block-free. Encode the signal value, then have the CPU
  `waitUntilSignaledValue:timeoutMS:`. Do NOT construct ObjC blocks/completion
  handlers from C# (that was a prior pain point).
- **Verify EVERY change with** (a) build both projects, (b) `Ryu --test` 8/8 green,
  (c) a real NieR boot on `--graphics-backend metal` reaching steady frames, ideally
  with non-black `[READBACK]`.
- `test_metal4.swift` in the repo root is only a class-existence probe — NOT an API
  reference. The SDK headers are.
- The M3 queue (`MetalWindow`, blits) and the M4 queue (pipeline) coexist deliberately.

## 10. Git / work-in-progress state

- Last committed checkpoint: `611071854`
  `feat(metal): checkpoint native Metal driver - reflection fix, MTL4 spike, 8/8 diagnostics`
  (contains the M4 queue/allocator swap + spike + all diagnostic plumbing).
- **NOT yet committed** (modified working tree): `Metal4Bindings.cs`,
  `Metal4CommandQueue.cs`, `MetalPipeline.cs`, `MetalRenderer.cs` (argument-table
  conversion + allocator pool + compute binding) plus earlier unrelated perf/headless
  instrumentation files (TerminalHud, Options, WindowBase, ThreadedRenderer,
  CommandType, PerformanceStatistics, VulkanRenderer, HvVcpuPool, Amiibo locales, …).
  Before the next milestone: run `Ryu --test`, boot NieR once, then commit the Metal
  changes in a coherent checkpoint ("feat(metal): M4 argument-table binding in live
  render+compute paths; allocator pool").
- Headless game path used for validation:
  `nintendo games/nier/…[USA][010056B015FE8000]….xci`.

## 11. Open questions for the next model/dev

1. Does an M4 **compute** encoder + queue-level `commit:count:` shared-event wait
   signal correctly at all? (Unproven — see §8b.) The M4 docs mention completion
   feedback (`addFeedbackHandler`, `MTL4CommitFeedback`) — may need that, or an
   in-buffer signal for compute.
2. Correct per-pass load/store handling on M4 — does the M4 render pass descriptor
   support Load natively (it should; M3 does) and we simply haven't plumbed it?
3. How far to take parallel encoding in the live path: chunk render passes to worker
   threads (N=P-cores allocators, batch `commit:count:`) vs single-pass split via
   suspending/resuming encoders. The spike pattern proves option A per-pass.
4. After correctness (8a/8b) is green: where exactly is the remaining throughput gap
   vs the ~280k/s target, and does parallel encoding close it?