# Native Metal 4 Engine — Executable Execution Spec

> **ARCHITECTURAL SOURCE OF TRUTH.** Read this before touching any Metal/shader
> code. Older project docs have been consolidated here; do not rely on deleted files
> (`docs/Latest.md`, `docs/m4-draw-black-diagnostic-checklist.md`,
> `docs/native-metal-backend.md`, `docs/Instructions.md`).
>
> - **Operational Execution Roadmap:** See **[`native-metal-execution-roadmap.md`](native-metal-execution-roadmap.md)** for the active 7-phase execution roadmap and live gate statuses.
> - **Binding & Implementation Reference:** See **[`full-metal.md`](full-metal.md)** for authoritative MTL4 binding and selector rules.

---

## 0. The Goal

- Replace the Vulkan/MoltenVK + SPIR-V + `libspirv-cross.dylib` translation chain on
  macOS/Apple Silicon with a **native Metal 4 pipeline** that emits **raw MSL directly**
  from the existing C# shader compiler.
- **Phase 1 (C#):** fix the black-screen write-path (Phase 1a / Roadmap Phase 2), then build a `CodeGen/Msl/` backend
  mirroring `CodeGen/Glsl/`, deleting the SPIRV-Cross dependency (Phase 1b / Roadmap Phase 5-6).
- **Phase 2 (separate, later):** optionally port the proven generator + compiler frontend
  into a native Swift app.
- Performance goal (deferred to Phase 2 correctness windows): City Ruins ≥ 28 FPS avg on
  an M2 Air, via multi-threaded M4 command encoding.

**Environment (verified):** macOS 26.5.2, Apple M2 Air arm64, `net10.0`, Metal 4 API
family (`AGXG14G*`). Build:
```bash
dotnet build src/Ryujinx.Headless/Ryujinx.Headless.csproj
src/Ryujinx.Headless/bin/Debug/net10.0/Ryu --test          # diagnostics, must be green
src/Ryujinx.Headless/bin/Debug/net10.0/Ryu "<Nier.xci>" --graphics-backend metal
```

---

## 1. Current State (verified facts — do NOT re-investigate)

- **M4 substrate is proven.** Raster → encode → commit → fence → store → readback all
  render non-black: `[MINI_M4]`, `[DIRECT_TEX]`, `[CLASSPROBE]`, magenta/cyan clears.
- **15/15 subsystems operational in `Ryu --test`**: M4 compute probe added and passing,
  shared-event block-free wait verified, M4 parallel encoder pool verified.
- **M3/M4 selector audit clean**: No M3 resource selectors remain in the M4 live pipeline.
  M3 selectors are strictly confined to intentional presentation (`MetalWindow`) and format-blit (`MetalFormatBlit`) queues.
- **Active Operational Gate (Roadmap Phase 2):** Locate the real game render writer
  for the 1920×1080 surface sampled by the composite shader. Upstream passes run, but
  the composited surface must be populated and verified via `[READBACK] sawNonzero=true`
  on a real varying NieR frame.
- **THE SMOKING GUN:** the fragment composite shader samples `bind=128`, a 1920×1080
  texture, that is **bound but never written** — `nonzeroBytes=0/64` in `getBytes`. The
  fragment shader runs (probe alpha 255→0) but sRGB-linearizes an empty texture → black.
  Seen at draw=5 and draw=100, new handle each frame.
- The sampled handle is **never a `target0` of any captured `[PASS]`**; every captured
  pass has exactly one color attachment. So its writer is a multi-attachment/offscreen/
  blit/CPU-upload path, or it is never written at all.
- All upstream offscreen HDR passes that DO draw (e.g. 3 draws into 1920×1080
  `R11G11B10Float`) read back `nonzeroBytes=0/64` — their draws are culled/discarded/
  never-landing, or the M4 attachment write is broken for those pass types.
- Game runs at ~3 fps (abnormally slow for native Metal); frames DO advance (readbacks
  reached 879+), so it's not hard-stuck — but it may be sitting on a near-black frame.

**Root-cause direction:** not a store-action problem (Store already works, proven by
cyan/magenta). The writer pass for the sampled surface is unidentified or failing to
populate it. This is **Phase 1a** below. Do not assume it is a bind/fence/store flip.

---

## 2. Phase-Gate Order (Ordered Pragmatic Milestones)

```
GATE 1: Subsystem Baseline & M4 Compute Probe (PASSED)
   │    15/15 subsystems green in Ryu --test; M4 compute & parallel encode verified
   ▼
GATE 2: Virtual Input Feeder & Real 3D Gameplay Frame (ACTIVE)
   │    Advance past splash/loading screens, trace registered swapchain target (replaces LastDrawn)
   ▼
GATE 3: Maxwell -> Metal 4 State Adapters
   │    Sampler dedup (18 -> <=16), texture swizzles, depth bias scaling, scissor normalization
   ▼
GATE 4: Performance & Thermal Stability
   │    Multi-threaded P-core command encoding, CVDisplayLink 30 FPS locking, MetalFX upscaling
   ▼
PHASE 5: Self-Containment: Native C# MSL Generator (DEFERRED)
        Drop libspirv-cross only after 3D gameplay is fully verified and stable
```

---

## 3. PHASE 1a — Write-Path Diagnosis & Black-Screen Fix

**Objective:** locate the missing writer for the 1920×1080 sampled texture and produce a
real, non-black game frame through the existing SPIRV-Cross pipeline.

### 3.1 Diagnostics in `src/Ryujinx.Graphics.Metal/MetalPipeline.cs`

1. **Raise driver log thresholds:** `_flushLogCount` 60→600; `_passLogCount` 80→800.
2. **Log ALL color attachments + depth-stencil handle per pass** in `[PASS]`/`[FLUSH]`
   (not just `target0`), so every pass's full target set is transparent. This is how we
   find the pass that (should) write the sampled 1920×1080 surface.
3. **Writer-vs-reader isolation:** when a pass target handle *matches* the sampled
   surface (or a `MetalFormatBlit`/`CopyRegion` targets it), read the texture back
   immediately after that pass closes — determine whether the writer runs and stores.
4. **Log blit/CPU-upload writers:** `MetalFormatBlit.Copy` and `MetalTexture.SetData`
   keyed by texture handle, to catch a copy/upload writer that `[PASS]` misses.
5. **Decouple winding from attachment writes:** enable `RYU_METAL_FORCE_NO_CULL`
   (MetalPipeline.cs ~line 920). If geometry becomes visible, that isolates a culling/
   Y-flip issue (`isYFlipped` → invert CW/CCW at lines 869-944) from a write failure.
6. **Command-buffer completion handler** on submitted M4 batches to surface silent GPU
   faults (illegal argument-table index, OOB fetch) instead of leaving targets unmodified.

### 3.2 Expected outcomes & fixes

- If a pass writes the sampled surface but bytes stay zero → inspect **load action**
  (unconditional `Clear` erases prior content; must be `Load` across passes), store
  action, and attachment format. Apply strict TBDR rules:
  - transient/offscreen temporaries → `MTLStoreActionDontCare` (memoryless);
  - the final present/composite target → `MTLStoreActionStore`.
- If no pass ever targets it → trace the **offscreen / MRT / blit / CPU-upload** path.
- If draws are culled/discarded → fix winding/viewport/scissor per §3.1.5 and the
  known normalization (scissor clamp, front-face inversion, depth `Always` when disabled).
- If frames only ever advance slowly → confirm the game reaches a real scene (not stuck
  on a near-black boot/loading frame).

### 3.3 Gate for 1a

`[READBACK] … sawNonzero=true` with a **varying, real image** on a NieR frame, at a
plausible FPS (≥ ~20), through the *existing* SPIRV-Cross path.

---

## 4. PHASE 1b — Native C# MSL Generator

**Objective:** emit raw MSL directly from `StructuredProgramInfo`, replacing
`libspirv-cross.dylib`.

### 4.1 The compiler surface (what a generator actually consumes)

`StructuredProgram.MakeStructuredProgram` produces one `StructuredProgramInfo` with
exactly three fields — a generator never sees Maxwell bytes, SSA, registers, or phi:
- `List<StructuredFunction> Functions` (each: `AstBlock` tree of `AstOperation`/`AstOperand`)
- `HashSet<IoDefinition> IoDefinitions`
- `HelperFunctionsMask`

Emission is not a 122-case maze (`Instruction` = ~122 real values). Metadata is
centralized once:
- **`InstType`/`InstInfo`** (`CodeGen/Glsl/Instructions/`) categorize every op as
  nullary/unary/binary/ternary/atomic/call/special with an `OpName` + precedence.
- **`InstructionInfo._infoTbl`** (`StructuredIr/InstructionInfo.cs:141`) is a single
  `(destType, srcTypes)[]` type table for all instructions.
- **Vector width** from `operation.Index` (PopCount → Vector2/3/4); `VectorExtract`
  splatting via `AstOperation.GetVectorType`.
- **Helper functions** selected by `HelperFunctionsMask`.

So ~70% of an MSL emitter is a near-clone of `GlslGenerator` (~1,700 lines). The MSL
delta (~30%) is the binding/annotation/UBO-struct work in §4.3.

### 4.2 Build structure

1. Create `src/Ryujinx.Graphics.Shader/CodeGen/Msl/` mirroring `CodeGen/Glsl/`.
2. Wire `TargetLanguage.Msl` in `src/Ryujinx.Graphics.Shader/Translation/TranslatorContext.cs`
   (lines ~373-377):
   ```csharp
   TargetLanguage.Msl => MslGenerator.Generate(info, parameters),
   ```
3. Reuse verbatim: `AstBlock` walker, `InstType`/`InstInfo` dispatch, `InstructionInfo`
   type table, `HelperFunctionsMask`, `ResourceManager` binding allocation, `IoMap`
   (adapted), control-flow emission.
4. Add MSL-only: type/annotation renaming (`vecN`→`floatN`, `matN`→`floatNxN`),
   `[[stage_in]]`/`[[color(n)]]` structs, UBO `constant` structs, entry-point signature,
   binding-index allocator (§4.3).

### 4.3 THE GOLDEN-REFERENCE BINDING DIFF LOCK (critical gate — do not skip)

Correctness lives in the **binding contract**, not the pass count. The emitted
`[[buffer(n)]]`/`[[texture(n)]]`/`[[sampler(n)]]` indices **must exactly match** the
`MTL4ArgumentTable` slots the driver writes at bind time, including `VertexBufferSlotOffset`
(for the special vertex-data buffer) and the `constant` UBO struct at `[[buffer(0)]]`.

**Protocol:**
1. Run the new generator against a static pool of test shaders.
2. Diff emitted MSL **and** binding annotations byte-for-byte against current
   `MetalProgram.GetMslBinding` + `SpirvCross` output.
3. **Do NOT swap in the new backend** until the indices map identically to the slots
   used by `BindTableBufferForSet` / `BindTexturesAndSamplers` / `VertexBufferSlotOffset`.

A mismatch by even one slot produces a *validly-rendered but wrong-binding* frame that
looks exactly like the current all-black — that would restart the debugging loop.

### 4.4 Delete the translation tax

Once the golden-reference gate passes:
- Rewire `MetalProgram.cs` to ingest the raw MSL text directly (skip SPIR-V + SPIRV-Cross).
- Drop the `libspirv-cross.dylib` dependency permanently.
- Verify `--test` green + a real non-black game frame.

### 4.5 M4 binding contract reference

The authoritative M4 argument-table model (buffer=gpuAddress, texture/sampler=
gpuResourceID, `setArgumentTable:atStages:`, indexed draw by address+length) is captured
in `docs/full-metal.md` §3/§9 and `MetalBindings.cs`/`Metal4Bindings.cs`. **Mirror the
M4 parallel-encode reference in `MetalDiagnostics.RunMetal4ParallelTest`.** Do not
reintroduce M3 per-encoder selectors on an M4 encoder.

### 4.6 Gates for 1b

- Diff-verified binding match vs golden reference (test-shader pool).
- `Ryu --test` green (MSL generation wired, no `libspirv-cross`).
- Real NieR frame non-black with correct bindings.

---

## 5. Architectural Philosophy: Adapter, Not Clone (Why Ryu Remains in .NET 10)

### 5.1 Rejection of the Swift Rewrite Trap
Ryu permanently avoids the multi-year trap of rewriting 500,000+ lines of emulation code in Swift:
- **Horizon OS is Not a Bottleneck:** Guest kernel scheduling, IPC dispatch, and filesystem services consume less than 3% total CPU time.
- **.NET 10 ARM64 Near-Native Performance:** With ReadyToRun (R2R), Tiered Dynamic PGO, and direct unmanaged memory pointers (`HostMappedUnsafe`), C# generates machine code within 3–5% of native C/Swift.
- **True Bare-Metal Acceleration:** Critical paths bypass the runtime anyway:
  - CPU: 64-bit guest instructions execute directly on hardware via Apple `Hypervisor.framework`.
  - Memory: CPU and GPU share physical DRAM with zero copies via `newBufferWithBytesNoCopy:`.
  - GPU: Multi-threaded command encoding drives Metal 4 directly via zero-overhead P/Invoke.
  - Audio: Vectorized with ARM64 `AdvSimd` NEON and Apple `Accelerate/vDSP`.

### 5.2 The "Adapter, Not Clone" Principle
Maxwell (Nvidia GM20B) and Apple Silicon (AGX TBDR) are fundamentally distinct:
- **Maxwell (IMR):** Immediate-mode rasterization directly to global memory buffers.
- **Apple Silicon (TBDR):** Bins primitives into $32 \times 32$ on-chip tiles, resolves inside tile memory, and enforces strict argument table limits.

Trying to force Metal 4 to clone Maxwell's physical hardware introduces massive pipeline stalls. Ryu acts as a high-fidelity **Adapter**:
- Faithfully translates Maxwell state (TIC/TSC separation, GOB block-linear memory, depth biases) into TBDR-native Metal 4 primitives (argument tables, sampler deduplication, memoryless depth targets, and hardware texture swizzles).

---

## 6. Skills Routing (already in `.agents/skills/`)

- `writepath-debugger.md` → PHASE 1a diagnostics (MetalPipeline.cs).
- `msl-codegen-target.md` → PHASE 1b generator (CodeGen/Msl/).
- `AGENTS.md` → component isolation + anti-regression lock. If a fix in File A needs a
  change in File B across subsystems, STOP and report, do not refactor across the boundary.

---

## 7. Files & State (map)

| Area | File(s) | Phase |
|---|---|---|
| Write-path diagnostics | `src/Ryujinx.Graphics.Metal/MetalPipeline.cs` (`FlushFrame`, `EnsureRenderPass`, `DrawInternal`, `_totalDrawCount`, probes) | 1a |
| Readback/fence | `src/Ryujinx.Graphics.Metal/MetalWindow.cs` (polling `SignaledValue >= TargetFenceValue`) | 1a |
| M4 reference / bindings | `src/Ryujinx.Graphics.Metal/Metal4CommandQueue.cs`, `Interop/Metal4Bindings.cs`, `Interop/MetalBindings.cs` | all |
| MSL reflection (today) | `src/Ryujinx.Graphics.Metal/MetalProgram.cs` (`SpirvToMsl`, `GetMslBinding`, `GetMslEntryPoint`) | 1a (keep) / 1b (replace) |
| Target-language switch | `src/Ryujinx.Graphics.Shader/Translation/TranslatorContext.cs:373-377` | 1b |
| IR surface | `src/Ryujinx.Graphics.Shader/StructuredIr/` (`StructuredProgramInfo`, `AstBlock`, `AstOperation`, `AstOperand`, `InstructionInfo.cs`) | 1b |
| GLSL model | `src/Ryujinx.Graphics.Shader/CodeGen/Glsl/` (`GlslGenerator`, `InstGen.cs`, `Instructions/`, `IoMap.cs`) | 1b |
| SPIR-V model | `src/Ryujinx.Graphics.Shader/CodeGen/Spirv/` | 1b (reference only) |
| M4 implementation reference | `docs/full-metal.md` (bindings, command queue, files) | 1b |

---

## 8. Deleted / Consolidated Docs (do not re-create confusion)

- `docs/Latest.md` — write-path findings, folded into §1/§3.
- `docs/m4-draw-black-diagnostic-checklist.md` — diagnostic checklist, folded into §3.
- `docs/native-metal-backend.md` — stale (recommended keeping SPIRV-Cross, now reversed).
- `docs/Instructions.md` — stale Swift-first routing to non-existent files; contradicted
  the phase-gate (Phase 1 is C#). Routing now lives in `.agents/skills/`.
