# Metal Magenta Investigation

**Status:** Root causes identified & fixes applied. On-hardware verification has confirmed
the FPS fix and that magenta is gone (no debug branch anymore); a dark-constant presented
texture on Nier's loading screen is still being investigated.

**Backend:** Native Apple Metal 3 (`-g Metal`). Tested headless with Nier: Automata (USA XCI).

## Symptom

- Game runs (audio OK), frames render at ~30 FPS once warm, but the screen shows a
  **full-screen magenta** instead of an image.
- FPS OSD reads `0.0` during the first ~2.4s of warmup then stabilises at `30.6 → 30.0`.

Two distinct issues are independent.

## Issue 1 — Full-screen magenta

### Root cause

Magenta was an **intentional debug branch**, not a Metal failure:

- `MetalWindow.cs` presenter fragment shader returned `float4(1, 0, 1, 1)` when a sampled
  texel is **exactly `(0,0,0,0)`**.
- Whole-screen magenta ⇒ the **present surface was all-zero**, i.e. no image data was copied.

The all-zero surface comes from the copy path in `MetalResources.cs::CopyRegion`:

- Metal's `MTLBlitCommandEncoder copyFromTexture:` requires source and destination to share
  the **same pixel format** and **silently no-ops** otherwise.
- The Nier presentation copy crosses formats:
  `R11G11B10Float (RG11B10Float)` → `R8G8B8A8Unorm (RGBA8Unorm)`.
- The blit no-ops, leaving the present (drawable) texture zeroed → magenta.

### Evidence

From `/tmp/ryu-metal-nier.log` (tail):

```
[SRT] color[0] fmt=R11G11B10Float 1920x1080 (0xBA5797700)
[COPY] src=0xBA5797700 dst=0xBA40E2A80 ...
[PRESENT] fmt=R8G8B8A8Unorm 1920x1080
```

Both are 32 bpp but map to different `MTLPixelFormat` values in `MetalFormats.cs`.

### Fix

1. **De-weaponised magenta** — the `(0,0,0,0)` branch was removed; the presenter now renders
   whatever texel it samples (a real black frame shows black, not magenta). **Confirmed:** the
   all-zeros debug branch is gone.
2. **Format-converting blit** — new `MetalFormatBlit.cs` runs a minimal fullscreen-triangle
   render pass that samples the source texture (nearest / clamp_to_edge) and writes the
   destination color attachment, converting pixel format on-device. The converted copy is
   ordered on the same command queue (render → blit → present).
3. `MetalResources.CopyRegion` detects mismatched formats and routes through the GPU pass
   (level 0 / slice 0), or a CPU byte-preserving copy for slice/mip combos. Same-format
   copies use the fast blit path. **Confirmed:** the conversion path fires (~1,400×/s on
   Nier, R11G11B10Float → R8G8B8A8Unorm, no shader/pipeline errors).
4. **Readback signal** — `MetalWindow` reads the presented framebuffer back and logs
   mean/min/max/nonzero plus corner/center texels, until a definitely-live frame appears.

### Remaining open question

On Nier's early loading screen, all readback frames report `mean=(0,0,0,0)` with a
**constant** `0x020x120x22` texel value at every corner and center, i.e. the presented
texture is (near-)black and constant rather than a varying image. The question is whether:

- (a) the source `R11G11B10Float` framebuffer is genuinely near-black at this point (Nier's
      title/loading screen is very dark), or
- (b) the format-converting fragment shader is still not sampling the source correctly.

A `[BLIT_PROBE]` diagnostic (added to `MetalFormatBlit.Copy`) reads the converted
destination back *after* `waitUntilCompleted` to answer this directly — it has not yet been
captured due to a tooling failure mid-run.

## Issue 2 — FPS reads 0.0 during warmup

### Root cause

`PerformanceStatistics.RecordFrameTime` accumulates the **first phantom delta** — the first
`RecordGameFrameTime` fires when `previousFrameTime == 0`, so `elapsed` is time since
**process start**, polluting the averaged frame time. Also, `GetGameFrameTime()` computes
`1000 / frameRate`, yielding garbage when the rate is 0.

### Fix

- Skip accumulating the frame delta on the first call (`previousFrameTime == 0`).
- Guard `GetGameFrameTime()` against `frameRate <= 0` (returns 0 instead of a divide).

**Confirmed:** warmup FPS now climbs to a clean `30.5 FPS` instead of a giant
`∞ms`/`88,394,326.9ms` spike. Note `RecordGameFrameTime` only fires on a guest
`OnFrameAvailable`; until the first real frame (~1.8s), the metric legitimately stays 0.

## Files touched

- `src/Ryujinx.Graphics.Metal/MetalFormatBlit.cs` — (new) GPU format-converting copy pass + probe.
- `src/Ryujinx.Graphics.Metal/MetalResources.cs` — format-mismatch detection + conversion routing.
- `src/Ryujinx.Graphics.Metal/MetalWindow.cs` — removed magenta branch, added readback diagnostic.
- `src/Ryujinx.HLE/PerformanceStatistics.cs` — first-frame FPS accounting + `1000/0` guard.
- `docs/metal-magenta-investigation.md` — this file.

## Verification

Build: `dotnet build src/Ryujinx.Headless/Ryujinx.Headless.csproj --no-restore -t:Rebuild` (0 errors).

Run:
```
./src/Ryujinx.Headless/bin/Debug/net10.0/Ryu --graphics-backend Metal "…/NieRAutomata The End of YoRHa Edition [USA]….xci"
```

Look for:
- `[READBACK] … nonzeroGrid=…/… … sawNonzero=true` — the presented surface has real,
  varying image data.
- `[BLIT_PROBE] … nonzeroR=N/64` — the format-converting dest rect has nonzero data after
  the pass waits for completion.

If the screen is a solid color (esp. the old magenta), magenta is fixed; the remaining
question is whether it's a real dark image or still a constant black.