Karamba3D structural C# idioms — Toolkit assemble/analyze/results, drift-safe API rules, solver-script guards. Fetch for structural tasks.

Reference notes for authoring structural-analysis C# script components against the installed
Karamba3D plugin. Extends gh-csharp-cookbook.md (script-mode scaffold, null-guards, socket rules) —
read that first; only the structural deltas live here. Design intent stays yours; this cookbook
standardizes the solver plumbing.

## Scope and preconditions

- Engine: installed Karamba3D **3.1.60519** (YAK package). All modes (GH components, scripts,
  standalone) call the same `karambaCommon.dll`; a script gets identical results to the native
  components. The interactive view components (ModelView/BeamView) are the only GH-exclusive parts.
- License is Rhino-session-scoped: whatever license the session holds (trial by default) applies to
  script code automatically. **Trial cap: 20 beam / 50 shell ELEMENTS** — subdivision counts toward
  the cap. Design benchmark/test models within it; tell the user explicitly when a request exceeds it.
- One solver component per definition stage: geometry stages feed ONE Karamba solver script; results
  fan out from it. Do not scatter Karamba calls across several scripts.

## Assembly references (#r)

```csharp
// #! csharp
#r "KarambaCommon.dll"   // PREFERRED (bare name, survives updates) — VERIFY LIVE on first use
// FALLBACK — pinned path (works, breaks on Karamba update; keep the pin explicit):
// #r "C:\Users\<user>\AppData\Roaming\McNeel\Rhinoceros\packages\8.0\Karamba3D\3.1.60519\net7.0-windows\KarambaCommon.dll"
```

- **Runtime folder matters**: the package ships `net48` / `net7.0-windows` / `net8.0-windows`.
  Rhino 8 script components run .NET 7 → use `net7.0-windows`. A wrong-runtime or duplicate copy
  causes "Data conversion failed from Model to Model" (assembly double-load).
- Reference `Karamba.gha` (same folder) ONLY when wrapping output for native components
  (`Karamba.GHopper.Models.GH_Model`) or using `Karamba.GHopper.Geometry` converters.
- NEVER glob "latest version folder" — pin. If the pinned path is missing after an update, report it;
  do not silently load a newer unverified build.
- The gh-csharp-cookbook rule "no package headers" forbids **network restore** (`#r "nuget:..."` /
  `# r:`). A local-DLL or bare-name `#r` does not hit the network and is allowed here.

## Version-drift-safe API rules (CRITICAL — the official docs contradict themselves)

- **`out var` for every Analyze-family out parameter.** v3 changed `List<>` → `IReadOnlyList<>`;
  explicitly-typed `out` declarations from older examples fail to compile. `out var` survives.
- **Units factory is SINGULAR**: `UnitsConversionFactory.Conv()`. The plural
  `UnitsConversionFactories` is dead v1.x API that still appears on some official guide pages.
- **Toolkit facade first** (`new KarambaCommon.Toolkit()`): stable-signature entry for parts,
  supports, loads, model assembly, ThI analysis, eigenmodes, cross-section optimization. Features
  missing from the facade (buckling modes, nonlinear, BESO, exporters, result visitors) live in
  version-unstable static classes (`Karamba.Algorithms.*`, `Karamba.Results.*`) — use them only when
  the facade lacks the feature, and expect signature drift between builds.
- **Trust the K3D_tests repo patterns over the prose guide** — several guide pages carry stale v1
  code. When a call fails to compile, the fix is usually the K3D_tests form of the same call.
- PENDING LIVE VERIFICATION (backfill after first smoke): bare-name `#r` resolution, exact
  3.1.60519 `AssembleModel`/`AnalyzeThI` arities, `FactoryLoad.LoadCaseCombination` scope.

## Solver source scaffold

```csharp
// #! csharp
#r "KarambaCommon.dll"
using System;
using System.Collections.Generic;
using Karamba.CrossSections;
using Karamba.Geometry;
using Karamba.Loads;
using Karamba.Supports;
using Karamba.Utilities;

// ---- SOLVER GUARD (first lines, cheap): structural inputs are wire-fed; there is no meaningful
// defensive-default model. Any missing input => skip ALL Karamba calls. Wire writes auto-solve the
// component BEFORE the final execute, so this guard runs several times — keep it trivial.
var lineList = axisLines as IList<object>;
if (lineList == null || lineList.Count == 0 || supportIndices == null || loadVector == null)
{
    status = "waiting: structural inputs not wired";
    return;   // do NOT assign solved/model/results on this path
}

// ---- Units: Karamba base units are m/kN. Convert Rhino doc units at the boundary, ONCE.
double s = Rhino.RhinoMath.UnitScale(
    Rhino.RhinoDoc.ActiveDoc.ModelUnitSystem, Rhino.UnitSystem.Meters);

var k3d = new KarambaCommon.Toolkit();
var logger = new MessageLogger();

// ---- Geometry: Karamba's own types (Point3/Line3/Vector3 — no 'd' suffix, not RhinoCommon).
var k3dLines = new List<Line3>();
foreach (Rhino.Geometry.Line ln in lineList)
{
    k3dLines.Add(new Line3(
        new Point3(ln.FromX * s, ln.FromY * s, ln.FromZ * s),
        new Point3(ln.ToX * s, ln.ToY * s, ln.ToZ * s)));
}

// ---- Build -> assemble -> analyze (out var EVERYWHERE; arities: verify live once, then trust).
var beams = k3d.Part.LineToBeam(
    k3dLines, new List<string> { "b" }, new List<CroSec>(), logger, out var nodes);

var supports = new List<Support>();
foreach (int ni in (IList<object>)supportIndices.ConvertAll())   // fixed: all six DOF true
    supports.Add(k3d.Support.Support(ni, new List<bool> { true, true, true, true, true, true }));

var pload = k3d.Load.PointLoad(loadNodeIndex, new Vector3(0, 0, -(double)loadVector), new Vector3());

var model = k3d.Model.AssembleModel(
    beams, supports, new List<Load> { pload },
    out var info, out var mass, out var cog, out var msg, out var runtimeWarning);

model = k3d.Algorithms.AnalyzeThI(
    model, out var maxDisp, out var gravityForces, out var elasticEnergy, out var warning);

// ---- Outputs. 'solved' is assigned ONLY on this success path (see contract below).
solved = true;
modelOut = new Karamba.GHopper.Models.GH_Model(model);  // needs #r Karamba.gha; for native views
results = "{\"solved\":true,\"maxDispM\":" + maxDisp[0].ToString("G6")
        + ",\"massKg\":" + mass.ToString("G6")
        + ",\"warn\":\"" + (warning ?? "") + "\"}";
status = "ok";
```

(The `supportIndices.ConvertAll()` line is illustrative — coerce your wire inputs per the
gh-csharp-cookbook list-input rules; the load convention here is a downward kN point load.)

## Solver-script execution contract (GPTino-specific, mandatory)

- **`solved` output is assigned ONLY on a successful solve.** Never assign `solved = false` — an
  unassigned output emits nothing, which is what verification checks. Declare the acceptance
  predicate `outputCountInRange` with expectedValue `"solved:1:1"` on the final execute so a
  guard-skipped or failed run can never read as green. Put the human-readable reason in `status`.
- **Final execute after wires commit is the only valid green** (house-rules step 4). The wire
  ChangeSets auto-solve partial states; those runs hit the guard and that is expected and fine.
- **`results` = ONE compact JSON string** on its own output. Output sampling caps at 5 items /
  200 chars per value — a single JSON line survives inspection; long lists do not. Include at
  minimum: `solved`, max displacement (m), model mass, and the solver warning string.
- Karamba reports many model errors via `MessageLogger`/out-strings instead of exceptions — after
  Analyze, check the warning string and include it in `results`; do not assume no-exception = valid.
- **Trial-cap awareness**: element count > 20 beams (after subdivision) on a trial license fails —
  say so in `status` instead of retrying.

## GH boundary rules

- **GH_Model sockets stay GENERIC** — deliberate exception to the "geometry hint on BOTH ends"
  rule. Karamba's goo has no RhinoCode converter; the generic (object) socket passes it intact.
  Set typeHint only on real geometry sockets (the curves/points feeding the solver).
- Model → native Karamba components (ModelView etc.): wrap as
  `new Karamba.GHopper.Models.GH_Model(model)`. Script → script: pass the wrapped model (or raw
  `Karamba.Models.Model`) through generic sockets; unwrap with `as Karamba.Models.Model` /
  `(gh_model).Value` and null-guard.
- **Units at every boundary**: geometry in → scale to meters once (RhinoMath.UnitScale); results
  out → Karamba base units are m/kN/t; convert for display with
  `UnitsConversionFactory.Conv()` (e.g. `ucf.cm().toUnit(v)`), never by hand-typed factors.
- Mutating an assembled model (re-analysis loops, cross-section swaps): clone first —
  `model = model.Clone(); model.cloneElements();` then after edits
  `model.initMaterialCroSecLists(); model.buildFEModel();` — skipping the clone mutates shared
  state across the definition.

## What stays forbidden

- Hand-rolled FE/solver math in scripts (stiffness assembly, eigen solvers) — the house-rules solver
  exception covers Karamba Toolkit calls only.
- Improvised verdict math: utilization/design-check/deflection-limit verdicts belong to the vetted
  check payload (structural_check.py, when present) or Karamba's own Utilization/OptiCroSec — a
  script that invents its own pass/fail thresholds is a defect.
- Unpinned references, `UnitsConversionFactories` (plural), explicitly-typed Analyze out params,
  scattering Karamba calls across multiple scripts.
