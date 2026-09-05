# Native Metal Execution Roadmap

> Working execution plan for completing the native Metal 4 backend with a real, non-black game frame.
>
> `docs/native-engine-plan.md` remains the architectural source of truth. `docs/full-metal.md` remains the MTL4 implementation and binding reference. This document turns those requirements into ordered execution phases.

## 1. Objective

Complete the macOS Apple Silicon native Metal path so that:

- the game renders a real, varying, non-black frame;
- M4 render and compute paths complete without selector crashes or GPU timeouts;
- M3 presentation/blit paths remain intentional and correctly synchronized with M4;
- the existing SPIR-V/SPIRV-Cross path is replaced only after a native C# MSL generator passes binding parity;
- audio remains an independent issue and does not obscure graphics validation;
- performance optimization happens only after correctness is proven.

## Execution status — Completed Milestones

- **Phase 0 (Source/Build Baseline):** Build passed for `Ryujinx.Graphics.Metal` and `Ryujinx.Headless` on .NET 10.
- **Phase 1 (M4 Infrastructure & Diagnostics):** Passed — M4 compute, M4 parallel encoding, shared-event completion, render, MRT, blit, texture, and presentation diagnostics all pass (15/15 tests green).
- **Phase 2 (State Adapters & Visual Fixes):**
  - **Near-Plane Clipping & Missing Geometry:** Fixed via shader clip-space normalization (`supportsDepthClipControl = false`, `needsFragmentOutputSpecialization = true`, mapping $[-w, w] \to [0, w]$).
  - **Alpha-to-Coverage & Stencil Pipeline:** Fixed via `SetMultisampleState` (`alphaToCoverageEnabled`), front/back `MTLStencilDescriptor`s, and binding `stencilAttachment` in `MTLRenderPassDescriptor`.
  - **Loading Screen & Muddy Textures:** Fixed via `MetalSampler` mapping `LinearMipmapLinear` and `LinearMipmapNearest` to linear minification, `MTLSamplerMipFilterNotMipmapped` (0) for 2D un-mipmapped textures, R address mode, compare functions, and LOD clamps.
  - **Independent MRT Blending:** Fixed via per-target `BlendDescriptor[] _blends = new BlendDescriptor[MaxRenderTargets]`.
  - **Memory & Resource Leak Elimination:** Removed redundant `Retain` calls on `newTextureWithDescriptor`, `newSamplerStateWithDescriptor`, `newDepthStencilStateWithDescriptor`, and `newSharedEvent`.
  - **Hot-Path & Compute Optimizations:** Cached `MTLComputePipelineState` on `MetalProgram`, cached static configuration flags, and eliminated heap array allocations on draw loops.
- **Diagnostics Gate:** Passed on Apple Silicon — `Ryu --test` reports 15/15 subsystem checks green (100%).

## 2. Non-negotiable architecture

```text
StructuredProgramInfo
        |
        +--> Current: SPIR-V -> SPIRV-Cross -> MSL
        |
        `--> Future:  C# CodeGen/Msl -> raw MSL
                         |
                    Metal pipeline state
                         |
                    MTL4 render/compute encoders
                         |
                    MTL4ArgumentTable bindings
                         |
                    MTL4 command buffers + allocators
                         |
                    MTL4 queue commit:count:
                         |
                    MTLSharedEvent completion
                         |
                    M3 presentation/blit queue
                         |
                    drawable / headless readback
```

The architecture is deliberately hybrid:

- M4 handles the live render and compute pipeline.
- M4 resources bind through `MTL4ArgumentTable`.
- M3 remains responsible for presentation and format-converting blits because M4 has no blit encoder.
- M4 command buffers use per-buffer allocators and batch submission.
- Completion is block-free through `MTLSharedEvent`.

## 3. M4 binding contract

Never send M3 resource-binding selectors to an M4 encoder.

### M4 rules

- Buffers: `gpuAddress + offset` through `setAddress:atIndex:`.
- Textures: `gpuResourceID` through `setTexture:atIndex:`.
- Samplers: `gpuResourceID` through `setSamplerState:atIndex:`.
- Render: install tables with `setArgumentTable:atStages:`.
- Compute: install one table with `setArgumentTable:`.
- Indexed draws: GPU index-buffer address plus index-buffer length.
- Non-indexed draws: the supported M4 `drawPrimitives:vertexStart:vertexCount:` selector.

Never use these on M4 render or compute encoders:

```text
setVertexBuffer:offset:atIndex:
setFragmentBuffer:offset:atIndex:
setBuffer:offset:atIndex:
setVertexTexture:atIndex:
setFragmentTexture:atIndex:
setVertexSamplerState:atIndex:
setFragmentSamplerState:atIndex:
setIndexBuffer:offset:indexType:
old indexed draw with buffer object + offset
```

Argument-table capacities and shader bindings must remain compatible:

- buffers: maximum 31;
- textures: maximum 128;
- samplers: maximum 16;
- uniform-buffer set: 0;
- storage-buffer set: 1;
- texture set: 2;
- image set: 3.

Every binding returned as `uint.MaxValue`, every required zero resource ID, and every invalid GPU address/length must be detected before submission.

## 4. Phase 0 — Baseline and checkpoint

### Purpose

Create a reproducible baseline before further Metal changes.

### Actions

1. Keep unrelated working-tree changes separate from the Metal correctness checkpoint.
2. Build the Metal and headless projects.
3. Run the complete diagnostic harness.
4. Record the current game boot behavior, present handles, readback output, FPS, and errors.
5. Create a coherent checkpoint before risky changes.

### Commands

```bash
dotnet build src/Ryujinx.Graphics.Metal/Ryujinx.Graphics.Metal.csproj
dotnet build src/Ryujinx.Headless/Ryujinx.Headless.csproj
src/Ryujinx.Headless/bin/Debug/net10.0/Ryu --test
```

### Gate

The baseline is recorded and reproducible. No unrelated changes are reverted.

## 5. Phase 1 — Prove and repair M4 compute

The current active blocker is the M4 compute timeout:

```text
MTL4 shared-event wait timed out after 5000 ms
```

The old M3 `setBuffer:offset:atIndex:` selector crash is already a separate, fixed failure. Do not confuse the two.

### 5.1 Add a minimal M4 compute diagnostic

Create a small diagnostic modeled after the proven M4 render test:

1. Create a trivial MSL compute kernel.
2. Create one output/storage buffer.
3. Bind it through an `MTL4ArgumentTable`.
4. Encode on an M4 compute encoder.
5. Dispatch one valid workload.
6. End the encoder and command buffer.
7. Submit with `commit:count:`.
8. Wait through the shared event.
9. Read back the buffer and verify that the kernel changed it.

This distinguishes queue/fence failure from game shader/binding failure.

### 5.2 Audit `MetalPipeline.DispatchCompute`

Verify all of the following against actual shader reflection:

- uniform-buffer bindings;
- storage-buffer bindings;
- sampled textures;
- samplers;
- images;
- MSL binding indices;
- GPU address plus byte offset;
- valid buffer length;
- valid texture/sampler resource IDs;
- valid table index range;
- positive dispatch dimensions;
- valid threadgroup size.

Do not submit a dispatch with invalid values. Log the program, resource, binding, and computed address/length, then skip it safely.

### 5.3 Improve compute lifecycle

- Inspect command-buffer errors on timeout or failure.
- Confirm whether compute requires an in-buffer signal like the render path or whether the queue signal is sufficient.
- Cache compute pipeline states by compute-function identity.
- Release cached pipeline states during disposal.
- Keep allocator ownership until the submitted command buffer has completed.

### Gate 1

The minimal M4 compute test completes, signals, and modifies its output buffer. A real game boot produces no compute timeout and no M4 selector exception.

## 6. Phase 2 — State adaptation, virtual input & render writer isolation

The final composite shader samples a bound 1920x1080 texture that is zero-filled or unpopulated. To resolve this and reach actual 3D gameplay, we apply state adapters and input progression:

### 6.1 Headless virtual input feeder (loading screen gate)
Switch titles (such as NieR:Automata) require button interaction (A button / South face button) to advance past initial splash screens and loading rings into the title menu and 3D gameplay.
- Implement an automated or interactive input feeder in `HeadlessRyujinx.cs` interfacing with the Switch Horizon OS HID service (`NPad`).
- Simulate A button press periodically after boot to advance from loading/splash screens into the main menu and 3D world.

### 6.2 Explicit swapchain display tracking (replaces `LastDrawn`)
`[TARGET_DIAG] Swapchain 0x... != LastDrawn 0x...` indicates divergence between the registered presentation target and the last GPU render target.
- `LastDrawn` is a fragile heuristic that frequently points to a 1-channel depth pass, shadow map, or offscreen temporary buffer, causing black-and-white or distorted visuals.
- Bypassing heuristics: Trace and present the exact texture handle registered by the guest OS display service (`vi` / `SurfaceFlinger` / `nvhost`).
- Ensure the final composite pass resolves directly into this registered surface before presentation.

### 6.3 Maxwell -> Metal 4 State Adapter Layer
Switch games rely on Nvidia Maxwell (IMR) hardware conventions that must be translated into Apple Silicon TBDR primitives:

1. **Maxwell TIC/TSC Sampler Deduplication**:
   - Metal 4 argument tables enforce `maxSamplerStateBindCount <= 16`.
   - Complex composite passes (e.g. NieR Shader 150) reference up to 18 independent samplers.
   - Hash sampler descriptor states in C# before writing to `MTL4ArgumentTable`. Deduplicating identical sampler states (e.g. Linear-Clamp) compresses 18 logical samplers into 4–8 physical Metal samplers, preventing driver compilation assertions.
2. **Texture Component Swizzling (`MTLTextureSwizzleChannels`)**:
   - Maxwell textures often use non-standard channel arrangements (BGRA, Depth-in-Red).
   - Apply `MTLTextureSwizzleChannels` directly on `newTextureViewWithPixelFormat:` to remap channels in hardware with 0.00% GPU latency.
3. **Scissor Rect Normalization & Clamping**:
   - Maxwell pushbuffers frequently issue negative offsets or dimensions exceeding attachment bounds.
   - Enforce clamp in `MetalPipeline.SetScissor`: `[0, 0, width, height]` to prevent dropped draws or Metal validation panics.
4. **Depth Bias Precision Scaling**:
   - Normalize constant and slope depth bias based on active depth attachment precision (`D16Unorm`, `D32Float`) before passing to `setDepthBias:slopeScale:clamp:` to fix shadow map striping and acne.
5. **Depth Clamping (`MTLDepthClipModeClamp`)**:
   - Toggle `setDepthClipMode:MTLDepthClipModeClamp` when Maxwell state disables depth clipping, preventing skyboxes and character geometry from clipping into voids.
6. **Memoryless Transient Targets (`MTLStorageModeMemoryless`)**:
   - Allocate offscreen depth/stencil passes with `MTLStorageModeMemoryless` and `MTLStoreActionDontCare` so intermediate passes remain entirely inside on-chip TBDR tile cache.

### 6.4 Log complete pass state
In `MetalPipeline` log, per frame and per pass:
- pass number, command-buffer handle, allocator index;
- every color attachment index, texture handle, dimensions, format;
- load action, store action, clear color, depth/stencil handle and format;
- program identity, draw count, and M4 signal value.

### 6.5 Gate 2
The game advances past splash/loading screens, the true swapchain buffer is presented, and `[READBACK] sawNonzero=true` confirms real, varying 3D gameplay graphics through the production `libspirv-cross` pipeline.

## 7. Phase 3 — Verify the M3/M4 boundary and presentation

M3 selectors are not universally obsolete. They are valid in intentional M3 presentation/blit code and invalid only when sent to M4 encoders.

### 7.1 M3 code that must remain

#### `MetalWindow.cs`

The presentation path uses an M3 render encoder to sample the M4-produced source and write the drawable. Its M3 texture and draw selectors are intentional.

#### `MetalFormatBlit.cs`

The format-converting blit uses the M3 queue/render encoder because M4 has no blit encoder. Its M3 vertex bytes, fragment bytes, fragment texture, and draw selectors are intentional.

#### `Interop/MetalBindings.cs`

M3 selector declarations must remain while M3 presentation/blit infrastructure exists. Declarations do not crash by themselves; sending them to an M4 encoder does.

### 7.2 M3 code that must never remain in M4 paths

Search `MetalPipeline.cs` and all M4 command-buffer paths for calls to:

```text
SelSetVertexBufferOffsetAtIndex
SelSetFragmentBufferOffsetAtIndex
SelSetComputeBufferOffsetAtIndex
SelSetVertexTextureAtIndex
SelSetFragmentTextureAtIndex
SelSetVertexSamplerStateAtIndex
SelSetFragmentSamplerStateAtIndex
SelSetIndexBufferOffsetIndexType
SelDrawIndexedPrimitivesIndexCountIndexTypeIndexBufferIndexBufferOffsetInstanceCount
```

Any such call on an M4 encoder must be replaced with argument-table binding or the M4 indexed draw form.

### 7.3 Verify the presentation chain

Compare the following handles and completion values each frame:

```text
M4 render target
    -> M3 blit source
    -> M3 blit destination/drawable
    -> presented/readback texture
```

Verify:

- M3 waits for the latest M4 shared-event value;
- the M4 source uses `Store` before M3 reads it;
- the source selected by `Present` is the intended source;
- `LastDrawn` is diagnostic evidence, not a permanent workaround;
- the drawable receives the actual game image;
- temporary red/cyan diagnostic clears are disabled after diagnosis.

### Gate 3

M4 never receives M3 resource selectors. M3 presentation/blit paths remain functional and correctly synchronized. The drawable/readback shows the same non-black image produced by the game render path.

## 8. Phase 4 — Lock down correctness diagnostics

Before changing shader generation, require all of the following:

- both projects build;
- `Ryu --test` is fully green;
- minimal M4 render test passes;
- minimal M4 compute test passes;
- no M4 selector exceptions;
- no shared-event timeouts;
- no command-buffer errors;
- non-zero readback from a real game frame;
- varying image data across frames;
- stable presentation source/destination handles;
- no invalid binding warnings;
- no GPU address or buffer-length warnings.

Use descriptive diagnostics for every failed GPU submission. Do not silently continue after invalid argument-table state.

## 9. Phase 4 — Performance & Thermal Optimization (M2 Air 30+ FPS)

Correctness enables throughput. Once Gate 2 and Gate 3 pass:

### 9.1 Multi-Threaded P-Core Command Encoding
- Distribute pass command encoding across Apple Silicon Avalanche Performance Cores using dedicated `MTL4CommandAllocator` instances.
- Submit batched buffers via `commit:count:` to eliminate driver submission contention and shatter the single-core 150k-250k commands/sec barrier.

### 9.2 Hardware Pacing & Thermal Budgeting
- Fanless Apple Silicon (M2 Air) has a sustained ~12–15W thermal ceiling.
- Enforce strict 30.0 FPS presentation cadence via Darwin `CVDisplayLink` to allow the SoC to sleep between frames, preventing passive thermal throttling.
- Deploy Apple MetalFX Spatial Scaler (`MetalFX.framework`) to render internal 3D passes at 720p/900p and upscale to 1080p, reducing GPU pixel-fill thermal load by ~35%.

### 9.3 Persistent Pipeline Caching
- Ensure all PSO compilation writes to `MTLBinaryArchive` to eliminate runtime shader compilation stutter across subsequent boots.

### Gate 4
NieR:Automata maintains a rock-solid 30.0 FPS with sub-1.5ms frame pacing variance in high-load areas (City Ruins) on a fanless MacBook Air M2.

---

## 10. Phase 5 — Self-Containment: Native C# MSL Generator (Deferred)

The production engine relies on `libspirv-cross.dylib` + `MTLBinaryArchive`, which runs asynchronously and caches permanently. Custom MSL generation is an optional, post-playability self-containment milestone:

### 10.1 Implementation Scope
Create `src/Ryujinx.Graphics.Shader/CodeGen/Msl/` mirroring GLSL AST traversal and instruction type tables, adding:
- `vecN` to `floatN` type mapping, `[[stage_in]]` / `[[color(n)]]` interfaces, and constant UBO structures.

### 10.2 Golden Binding Lock Gate
- Compare emitted MSL and binding annotations byte-for-byte against the golden reference before any backend switch.
- Verify zero regression in real-game visual rendering.

## 12. File ownership map

| Area | Files | Responsibility |
|---|---|---|
| Write-path debugging | `src/Ryujinx.Graphics.Metal/MetalPipeline.cs` | Passes, attachments, draws, readback probes |
| Presentation/readback | `src/Ryujinx.Graphics.Metal/MetalWindow.cs` | M3 drawable presentation and synchronization |
| Format conversion | `src/Ryujinx.Graphics.Metal/MetalFormatBlit.cs` | Intentional M3 blit path |
| M4 queue/sync | `src/Ryujinx.Graphics.Metal/Metal4CommandQueue.cs` | Allocators, submission, shared events |
| M4 interop | `src/Ryujinx.Graphics.Metal/Interop/Metal4Bindings.cs` | M4 selectors and ABI signatures |
| M3 interop | `src/Ryujinx.Graphics.Metal/Interop/MetalBindings.cs` | M3 selectors used by presentation/blit and shared descriptors |
| Renderer ownership | `src/Ryujinx.Graphics.Metal/MetalRenderer.cs` | Device, queues, allocator pool, resource access |
| Existing shader reflection | `src/Ryujinx.Graphics.Metal/MetalProgram.cs` | Current SPIRV-Cross path and binding reference |
| New shader backend | `src/Ryujinx.Graphics.Shader/CodeGen/Msl/` | Native MSL generation after write-path gate |
| Target switch | `src/Ryujinx.Graphics.Shader/Translation/TranslatorContext.cs` | Enable native MSL only after parity |
| IR reference | `src/Ryujinx.Graphics.Shader/StructuredIr/` | `StructuredProgramInfo`, AST, instruction metadata |

## 13. Final definition of done

Graphics and emulation correctness are considered complete when:

- the game boots and advances on `--graphics-backend metal`;
- no M4 M3-selector exception occurs;
- no compute shared-event timeout occurs;
- no command-buffer GPU error occurs;
- the registered swapchain target contains non-zero data and advances past splash/loading screens into 3D gameplay;
- the presented drawable displays clean, uncorrupted, varying 3D game visuals;
- `Ryu --test` remains 15/15 green;
- stable 30.0 FPS presentation on Apple Silicon M2 Air via `CVDisplayLink` and MetalFX;
- any remaining audio issue is isolated and documented separately.
