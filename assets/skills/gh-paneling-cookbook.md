Surface paneling C# idioms — isotrim UV grid, attractor-driven openings, and thickness solids. Fetch this for facade/paneling tasks so you adapt vetted RhinoCommon calls instead of deriving each algorithm.

# Paneling cookbook (C# script components)

Vetted RhinoCommon idioms for the recurring facade-paneling chain. Each block is a stage
component body (see gh-csharp-cookbook.md for the script-mode scaffold — top-level statements,
no RunScript wrapper — plus defensive input coalescing and Parallel.For crash rules). Author
them as a staged chain: Grid -> Openings -> Solids, each its own component, outputs feeding
the next stage's inputs.

All calls are RhinoCommon; `RhinoDoc.ActiveDoc.ModelAbsoluteTolerance` is unavailable on worker
threads — pass a tolerance in or use a constant like `tol = 0.001`.

## Stage 1 — Isotrim UV panel grid (GH "SubSrf")

Inputs: `Surface srf`, `int nu`, `int nv`. Output: `List<Brep> panels` (row-major i*nv+j).

```csharp
var uD = srf.Domain(0);
var vD = srf.Domain(1);
var panels = new List<Brep>();
for (int i = 0; i < nu; i++)
  for (int j = 0; j < nv; j++)
  {
    var ui = new Interval(uD.ParameterAt(i / (double)nu), uD.ParameterAt((i + 1) / (double)nu));
    var vj = new Interval(vD.ParameterAt(j / (double)nv), vD.ParameterAt((j + 1) / (double)nv));
    Surface sub = srf.Trim(ui, vj);          // isotrim: a subsurface over the cell's domain
    if (sub != null) panels.Add(sub.ToBrep());
  }
```

`Interval.ParameterAt(t)` maps normalized t∈[0,1] onto the domain — this handles non-uniform
surface domains correctly (never assume 0..1). Keep the grid as a flat list with a known stride
(nv) so downstream stages can rebuild the data tree by `{i}` branches if the task needs it.

## Stage 2 — Attractor-driven opening curves

Inputs: `List<Brep> panels`, `Point3d attractor`, `double near`, `double far`, `double falloff`.
Output: `List<Curve> openings` (one inner boundary per panel; empty where the ratio ≈ 0).

```csharp
var openings = new List<Curve>();
foreach (var panel in panels)
{
  var amp = AreaMassProperties.Compute(panel);
  if (amp == null) { openings.Add(null); continue; }
  Point3d c = amp.Centroid;
  double t = Math.Min(1.0, c.DistanceTo(attractor) / Math.Max(1e-9, falloff)); // 0 near -> 1 far
  double ratio = near + (far - near) * t;                                       // opening fraction
  // Inner boundary = the panel's outer edge loop scaled toward its centroid.
  Curve[] edges = panel.DuplicateEdgeCurves();
  Curve loop = Curve.JoinCurves(edges, 0.01).FirstOrDefault();
  if (loop == null) { openings.Add(null); continue; }
  Curve inner = loop.DuplicateCurve();
  inner.Transform(Transform.Scale(c, Math.Max(0.0, Math.Min(0.95, ratio))));
  openings.Add(inner);
}
```

Clamp the scale factor (≤0.95) so the opening never reaches the panel edge. Scaling the real edge
loop (not a bounding rectangle) keeps the opening shaped like the panel, which respects curved or
non-rectangular boundaries — do NOT approximate with an axis rectangle.

## Stage 3 — Perforated panel solids (thickness)

Inputs: `List<Brep> panels`, `List<Curve> openings`, `double thk`, `double tol` (e.g. 0.001).
Output: `List<Brep> solids` (closed).

```csharp
var solids = new List<Brep>();
for (int k = 0; k < panels.Count; k++)
{
  // 1) Thicken the panel to a CLOSED solid along its normals.
  Brep[] shell = Brep.CreateOffsetBrep(panels[k], thk, true, true, tol, out _, out _);
  Brep solid = shell?.FirstOrDefault(b => b != null && b.IsSolid);
  if (solid == null) continue;

  // 2) Perforate: extrude the opening curve into a through-cutter and subtract. Extrusion.Create
  //    needs a PLANAR closed curve, so this only fires where the opening is planar; anywhere it is
  //    not (or the boolean fails) we FAIL SAFE to the un-perforated solid rather than dropping the panel.
  Curve hole = k < openings.Count ? openings[k] : null;
  if (hole != null && hole.IsClosed && hole.IsPlanar())
  {
    // Extrude both ways so the cutter fully spans the thickened panel (offset it back by thk*2 first).
    Curve seat = hole.DuplicateCurve();
    Vector3d n = hole.TryGetPlane(out var pl) ? pl.Normal : Vector3d.ZAxis;
    seat.Transform(Transform.Translation(n * (-thk * 2.0)));
    Brep cutter = Extrusion.Create(seat, thk * 4.0, true)?.ToBrep();
    if (cutter != null)
    {
      Brep[] diff = Brep.CreateBooleanDifference(new[] { solid }, new[] { cutter }, tol);
      if (diff != null && diff.Length > 0) { solids.AddRange(diff.Where(b => b.IsSolid)); continue; }
    }
  }
  solids.Add(solid); // un-perforated fallback
}
```

`Brep.CreateOffsetBrep(brep, distance, solid:true, extend:true, tol, out blends, out walls)` is the
solidify workhorse — with `solid:true` it returns a CLOSED thick shell. Verify each with `b.IsSolid`
and count solids in committed.outputs. Booleans are the fragile, slowest step: keep panel counts
modest on the first pass, verify committed.outputs, then raise `nu`/`nv`. A committed un-perforated
pass beats a failed perforated one — never resubmit a timed-out boolean; reduce the count first.

## Chain shape

- Stage 1 grid feeds Stage 2 and Stage 3 (panels). Stage 2 feeds Stage 3 (openings).
- Sliders: `nu`, `nv` (grid); `near`, `far`, `falloff` (openings); `thk` (solids). Wire each to the
  stage that consumes it; the attractor is a referenceRhinoObjects point or an internalised Point3d.
- Type hints: the `Surface`/`Brep`/`Curve`/`Point3d` sockets that carry geometry BETWEEN components
  MUST use the matching geometry type hint on both ends (surface, brep, curve, point3d), or the
  receiver gets an untyped Guid.
