# Reference-op verification + stress campaign — 2026-07-27

Automated dev-loop (`scripts/dev-loop.ps1` + `scripts/dev-drive.ps1`) on a fresh paneling
scene (`dev-scene.py`: warped NURBS surface + closed boundary rectangle + 2 freeform
curves + 2 points). Grasshopper is opened head-lessly via Rhino `/runscript` chaining
(no manual "Open Grasshopper" click, no SendKeys). Ground truth read from the per-doc
LibGit2Sharp state history (`runtime/histories/<docId>/{changes,state}`) and dev-mode
diagnostic traces (`runtime/.gptino-diagnostic-*.json`).

## P0 reference-op fixes — VERIFIED (broker ground truth)

Task: "reference the surface + boundary curve as live editable GH references (do not
re-author), for facade paneling prep."

Committed `rev 2` ChangeSet contained two `"kind":"referenceRhinoObjects"` operations:
- `reference-facade-surface` → `canvas.referenceRhinoObjects` { rhinoObjectIds:[f9a41a36…],
  paramType:surface, pivot:"gptino:auto" } → Param_Surface "Facade NURBS Surface"
- `reference-facade-boundary` → { rhinoObjectIds:[5d0d854c…], paramType:curve,
  pivot:"gptino:auto" } → Param_Curve "Facade Boundary Rectangle"
- `setGroup` "Facade Inputs — Live Rhino References"

- **P0-1 (schema enum)**: the model EMITTED referenceRhinoObjects — impossible before the
  schema-enum fix. Confirms the earlier "#1 live confirmed" was a false positive.
- **P0-2 (auto-placement)**: pivot `"gptino:auto"` reached commit without the adapter's
  RequireFinite throwing — the sentinel was resolved for the reference op.
- **P0-3a (paired doc)**: geometry loaded (the adapter throws on 0 loaded), so the correct
  session-paired Rhino document resolved by serial, not ActiveDoc.

## Edge cases surfaced

### E1 — output inspection chokes on standalone reference parameters (real, low severity)
During the reference-op Verify, `CollectComponentOutputsAsync` inspected the two new
writeSet components; both threw:
`System.NotSupportedException: Grasshopper object <guid> does not expose component outputs.`
at `GrasshopperCanvasFoundationAdapter.InspectOutputsCoreAsync`.
Cause: referenceRhinoObjects creates `IGH_Param` objects (Param_Surface/Param_Curve), but
`InspectOutputsCoreAsync` only handles `IGH_Component` (`component.Params.Output`). The
exception is caught in CollectComponentOutputs (job still commits), but it (a) emits noisy
failure diagnostics, (b) wastes a bridge round-trip, and (c) makes semantic predicates
(area/closed/count/…) unavailable on a reference parameter's own data.
Fix (planned, batched): make InspectOutputsCoreAsync treat an `IGH_Param` object as its own
single output (inspect the param's VolatileData directly).

Note: E1 is NOT reference-op-specific. Wave 1/2 showed the same exception for the U/V
Number Sliders (GH_NumberSlider is an IGH_Param), so every job that writes a standalone
parameter (slider or reference) pays the exception + a wasted bridge round-trip. Pre-existing;
just made visible by the reference op. The IGH_Param fix resolves sliders too and would let
a slider's value / a reference param's geometry back a predicate.

**FIXED + re-verified live (2026-07-27).** `InspectOutputsCoreAsync` now inspects an
`IGH_Component`'s Output params OR a standalone `IGH_Param` (reference param / slider) as its
own single output. After rebuild+reinstall, the identical trigger task (reference Surface param
+ two U/V sliders + C# paneling) produced **0 new diagnostics, 0 failures, 7 commits** — the
`NotSupportedException` noise is gone and standalone-param data is now inspectable (predicates
on it work).

## Stress waves (paneling), same live Rhino session

| Wave | Task | Result |
|---|---|---|
| 1 | Panelize reference surface, 20×15 UV grid, C#, slider-driven | idle 174s, **5 commits**, 0 conflicts, only E1 noise |
| 2 | Attractor-driven per-panel opening scale, keep data-tree | idle 295s, **6 commits**, 0 conflicts, only E1 noise |
| 3 | Crank sliders to 2000×1200 (2.4M panels) | **Safe refusal in 18s, 0 commits** — agent quantified the freeze risk (~9.6M trims > 45s limit) and declined; U/V preserved |
| 4 | Two concurrent sessions editing the same doc | Both committed, **writer-lease serialized** (caught A holding the writer mid-execute), **0 conflicts**, 6 commits |
| 5 | Inject a C# runtime exception and execute it | Executed → `state: failed`, diagnosed `python_error` (ArgumentOutOfRangeException [6:1]), `runtimeErrorAbsent` **blocked the commit** (surfaced as a resolvable conflict), **no crash / no recoveryRequired** |

**Totals: 23 commits, 0 crashes, 0 recoveryRequired, 0 timeouts.** The only non-injected
issue across the whole campaign is E1 (caught, cosmetic + predicate-blocking). Heavy-solve
self-refusal, concurrency serialization, and runtime-error capture all behaved as designed.

## Decomposition test — heavy code auto-split (✅)

Task (deliberately heavy, NO split instruction, to test autonomous decomposition): from the
referenced NURBS surface build a 40×25 = 1000-panel structural facade with attractor-driven
openings, 50 mm solids, and per-panel diagonal bracing — 3000 closed Breps total.

Ground truth (git snapshot): the agent did NOT author one monolithic component. It produced a
staged, linearly-wired, per-stage-grouped chain — 21 objects, 19 wires, 4 groups:
`UV Panel Grid` [4] → `Openings - Attractor Profiles` [7] → `Panel Solids - Thickness` [2] →
`Structure - Diagonal Bracing` [3], plus 11 named control sliders. 14 staged commits, 0
conflicts, 0 generation failures.

Solve time (agent-measured): full 1000-panel / 3000-Brep recompute ≈ **4.08 s TOTAL**, i.e.
≈1 s per stage. A single monolithic component would have been one ~4 s+ op, far over the 1 s
op_duration threshold. Splitting kept every component fast — the house-rules "split by logical
unit at ~1 s + linear-flow wiring + per-stage grouping" work autonomously on a heavy real task.

Caveat: the RESULT solves fast, but the deep model took ~15 min of authoring wall-clock (14
staged commits) to produce it — an authoring-latency observation (model speed/effort), not a
decomposition-logic problem.

## Automation win
The whole run needed zero manual Rhino interaction: `dev-loop.ps1` boots Rhino in dev-mode,
opens the scene, panel, and Grasshopper doc via `/runscript` chaining, and waits for the
loopback endpoint; `dev-drive.ps1` / `dev-wave.ps1` drive sessions over HTTP (UTF-8 Korean).
Ground truth came from the per-doc git state history, not the model's self-report.
