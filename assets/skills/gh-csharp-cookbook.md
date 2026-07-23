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
  (`canvas.create` GUID `ae3b6678-0856-4e38-8100-3e31ceb6779b`, `python.setSource`
  runtime `"csharp"`).
- **Null-guard inputs**: slider-fed generic inputs arrive as `object` (often boxed `double`)
  and are `null` until wired. Coalesce then cast: `var n = (int)(count ?? 5.0);`. Casting
  a boxed double straight to `int` throws — go through `double` first or use
  `Convert.ToInt32(count ?? 5.0)`.
- **Geometry types**: `using Rhino.Geometry;` and construct explicitly
  (`new Point3d(x, y, z)`, `new Line(a, b)`, `NurbsCurve.Create(...)`). Sockets carrying
  geometry between components need the geometry type hint on BOTH ends (set via
  setComponentIo/setTyping), same as Python.
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
