# C# script component cookbook — Rhino 8 script-mode (default authoring language)

Reference notes for authoring C# Script components through GPTino. Script-mode only: plain
top-level statements, no RunScript wrapper, no class/SDK boilerplate.

## Source scaffold

The first line must be `// #! csharp` (GPTino prepends it if missing). Inputs arrive as
variables named after the input sockets; outputs are assigned to variables named after the
output sockets — exactly like the Python component.

```csharp
// #! csharp
using System;
using System.Collections.Generic;
using Rhino.Geometry;

// Guard every input DEFENSIVELY: an unwired socket arrives empty/null.
var n = (int)(count ?? 5.0);
var step = (double)(spacing ?? 2.0);

var pts = new List<Point3d>();
for (var i = 0; i < n; i++)
{
    pts.Add(new Point3d(i * step, 0.0, 0.0));
}

points = pts;   // assign each output socket variable exactly once
```

## Rules that prevent the common failures

- **Directive**: `// #! csharp` first line. A `#! python 3` directive on a C# component (or
  vice versa) is rejected by the adapter — runtime must match the created component
  (`canvas.create` GUID `b6ba1144-02d6-4a2d-b53c-ec62e290eeb7`, `python.setSource`
  runtime `"csharp"`).
- **Null-guard inputs**: slider-fed generic inputs arrive as `object` (often boxed `double`)
  and are `null` until wired. Coalesce then cast: `var n = (int)(count ?? 5.0);`. Casting
  a boxed double straight to `int` throws — go through `double` first or use
  `Convert.ToInt32(count ?? 5.0)`.
- **Typed geometry inputs**: an input that carries geometry ALSO arrives as `object` unless you
  gave its socket a geometry type hint. Never pass a raw generic input into a RhinoCommon call
  expecting a typed argument — `Extrusion.Create(curve, ...)` where `curve` is `object` fails to
  compile ("cannot convert from 'object' to 'Rhino.Geometry.Curve'"). Either set the socket's
  typeHint (curve/brep/mesh/...) so it arrives typed, or cast explicitly: `var c = (Curve)curve;`
  / `var crv = curve as Curve;` with a null check. Build geometry inside the script from scalar
  inputs when you can, so inputs stay generic and only outputs need geometry hints.
- **Geometry types**: `using Rhino.Geometry;` and construct explicitly
  (`new Point3d(x, y, z)`, `new Line(a, b)`, `NurbsCurve.Create(...)`). Sockets carrying
  geometry between components need the geometry type hint on BOTH ends (set via
  setComponentIo/setTyping), same as Python.
- **Definite assignment**: initialize every declared local on ALL branches, ideally at declaration
  (`double t = 0;`) — C# treats "use of unassigned local variable" as a compile error, not a warning.
- **Raw source payloads**: script source is raw text. Never re-escape newlines — a literal
  backslash-n sequence lands in the source verbatim and breaks compilation.
- **No component context in script-mode**: there is NO `ghenv`, NO `Component`, NO `RunScript`
  this-object. Read the document directly (`Rhino.RhinoDoc.ActiveDoc` and its Layers/Objects tables).
- **Socket names are C# identifiers**: never name a socket a C# reserved keyword — `out` foremost
  (the console output socket is not yours to declare). When you set a component's schema, list only
  YOUR sockets and simply omit the console `out`; GPTino preserves it automatically at its live
  position, so omitting it is never a "removed socket" error. Use plain ASCII identifier names;
  names with spaces or non-ASCII characters are rejected before anything runs.
- **Verify RhinoCommon signatures against Rhino 8**: for uncertain APIs (`Unroller`, duplicate/offset
  families) keep to the documented forms in this cookbook — a wrong overload fails at compile with
  the exact [line:col]; fix the signature and resubmit rather than guessing variants.
- **List/tree access**: a `list` input arrives as `IList<object>` (or typed when hinted) —
  iterate and cast per element or hint the socket type. Vectorize inside the script: one
  script processing a whole list beats the solver iterating an item-access component.
- **Outputs**: assign every output variable; an unassigned output emits nothing downstream.
  Delete unused outputs from the schema instead of leaving them unassigned.
- **No package headers**: the `#r "nuget:..."` reference header triggers network restore at
  load, like Python's `# r:` — never ship it; standard .NET plus RhinoCommon covers the
  cookbook patterns.
- **Determinism**: no `DateTime.Now`/`Random` without a seeded input — solves must be
  reproducible for verification.

## RhinoCommon patterns (fast paths)

```csharp
// Points grid (vectorized, list output)
var grid = new List<Point3d>();
for (var i = 0; i < nx; i++)
    for (var j = 0; j < ny; j++)
        grid.Add(new Point3d(i * dx, j * dy, 0.0));

// Polyline / curve
var poly = new Polyline(grid);            // from points
Curve curve = poly.ToNurbsCurve();

// Line + extrusion + brep
var line = new Line(a, b);
var extrusion = Extrusion.Create(profileCurve, height, cap: true);
Brep brep = extrusion?.ToBrep();

// Transform in place
var move = Transform.Translation(new Vector3d(0, 0, dz));
geometry.Transform(move);

// Loft
Brep[] lofted = Brep.CreateFromLoft(
    new[] { curveA, curveB }, Point3d.Unset, Point3d.Unset, LoftType.Normal, closed: false);

// Boolean
Brep[] union = Brep.CreateBooleanUnion(new[] { brepA, brepB }, tolerance);
```

- Take tolerance from the document when it matters:
  `Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance` (legitimate inside a user-run script).
- Check nullable results (`CreateBooleanUnion`, `ToBrep`) — RhinoCommon returns null/empty
  on failure; fail loud by assigning a message output rather than silently emitting nothing.

## Multithreading (Parallel.For) for CPU-bound geometry

Reach for this ONLY when one component does heavy, independent per-item geometry math (thousands of
items) that would otherwise approach the 45s budget — it uses every core for that one component's
solve. It does NOT make Grasshopper responsive (the solve still blocks the UI thread until the loop
joins) and there is NO cross-component parallelism (the graph solves sequentially on one thread).
Wrong threading CRASHES Rhino, which is worse than a slow solve — follow the hard rules exactly.

Safe pattern — capture doc values on the main thread, preallocate the output array, write only your
own index:

```csharp
using System.Collections.Generic;
using System.Threading.Tasks;
using Rhino.Geometry;

// Capture EVERYTHING doc-derived on the MAIN thread, before the loop:
double tol = Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;
var input = (IList<Curve>)curves;          // read the (curve-hinted) list input on the main thread
double dist = (double)(distance ?? 1.0);

var result = new Curve[input.Count];       // preallocate, sized to input
Parallel.For(0, input.Count, i =>
{
    // Worker body: pure Rhino.Geometry only; input is READ-ONLY here.
    var offsets = input[i].Offset(Plane.WorldXY, dist, tol, CurveOffsetCornerStyle.Sharp);
    result[i] = (offsets != null && offsets.Length > 0) ? offsets[0] : null;  // write ONLY result[i]
});

curvesOut = result;   // assemble outputs on the main thread, AFTER the loop
```

Hard rules (each one prevents an immediate Rhino crash, not just a failed solve):
- **Only Rhino.Geometry on workers.** Only the Rhino.Geometry namespace is thread-safe; the rest of
  RhinoCommon is not. Safe worker work: pure Point3d/Vector3d/Plane/Transform math, per-item `new`
  curve/mesh/brep creation, `Intersection.*`, and Area/VolumeMassProperties on UNCHANGED inputs.
- **No RhinoDoc from a worker.** Never call `Rhino.RhinoDoc.ActiveDoc` or any document/RhinoObject
  member inside the loop — capture tolerance/units on the main thread and pass them by value. Doc
  access off the main thread crashes Rhino immediately.
- **Preallocate and index-write.** Size an array to the input and write only `result[i]` per
  iteration — unique indices never collide, so no lock is needed. NEVER `Add` to a shared
  List/Dictionary/DataTree from workers without a lock; a shared `Add` corrupts the collection.
- **Shared inputs are read-only.** Never mutate a geometry object another thread can read, and never
  modify a shared object while another thread evaluates/splits/meshes it — this corrupts native
  caches and access-violation-crashes. If you must mutate, give each thread its own `Duplicate...()`.
- **One owner per object.** Do not `Dispose` or let a geometry object be GC'd while another thread
  still references the same underlying native handle.
- **Assemble outputs (and any DataTree) on the main thread, after the join** — DataTree writes are
  not thread-safe.
- Determinism still applies (no `Random`/`DateTime.Now`) — the parallel loop must produce identical
  outputs every run for Verify.

## Data trees (only when branch structure matters)

```csharp
using Grasshopper;
using Grasshopper.Kernel.Data;

var tree = new DataTree<Point3d>();
for (var b = 0; b < branches; b++)
{
    var path = new GH_Path(b);
    for (var i = 0; i < perBranch; i++)
        tree.Add(new Point3d(i, b, 0), path);
}
result = tree;
```

Prefer flat lists whenever grouping is not semantically needed — trees are the top source
of downstream wiring surprises.
