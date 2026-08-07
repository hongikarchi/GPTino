PyNite open-source FE idioms for Python 3 components — import pattern, section-axis convention, solver contract. Fetch for in-canvas structural tasks.

Reference notes for running the PyNite open-source FE engine INSIDE a Grasshopper Python 3
component. Companion to structural-analysis.md (domain rules: model input, load combos,
verdict discipline). Live-verified 2026-08-05: in-canvas PyNite reproduced the 8 m beam
oracle to 0.004% (-7.6187 vs -7.619 mm theory). PyNite is GPTino's structural engine:
no license, no element cap, runs anywhere Python does. Members are Euler-Bernoulli (no
shear deformation) — fine for slender members; note it when members are stocky (L/h < ~10).

## Import (pre-installed environment)

```python
#! python 3
from Pynite import FEModel3D   # PyNiteFEA is pip-installed in Rhino's Python env
```

- NEVER put `# r: PyNiteFEA` in shipped scripts (house rule: pip-on-load blocks file open).
  The package is pre-installed into Rhino's environment (.rhinocode py env). If the import
  fails, report it — do not add package headers.

## Units and the SECTION-AXIS TRAP

Work in consistent kN / m. E=2.1e8, G=8.076e7 kN/m2 for steel; density slot takes unit
weight 78.5 (used only if you model self-weight explicitly).

```python
fe = FEModel3D()
fe.add_material("s", 2.1e8, 8.076e7, 0.3, 78.5)
# add_section(name, A, Iy, Iz, J) — Iy = STRONG axis (governs vertical bending).
# LIVE-VERIFIED: swapping Iy/Iz under-stiffens vertical bending 4x for an H-section and
# the model still solves plausibly — this is a silent wrong-answer trap.
# THE TWO SHIPPED CATALOGS NAME THEIR COLUMNS DIFFERENTLY — map by MEANING, not by name:
#   structural/sections-ks.json (KS): strong = "Ix", weak = "Iy"
#   structural/sections.json    (EU): strong = "Iy", weak = "Iz"
# Feed the STRONG column into add_section's Iy argument and the WEAK column into Iz.
# Sanity check before trusting a run: the strong value must be the larger of the two.
fe.add_section("r", 0.02, 6.667e-5, 1.667e-5, 4.58e-6)
```

## Minimal verified model shape

```python
fe.add_node("N0", 0, 0, 0); fe.add_node("N1", 4, 0, 0); fe.add_node("N2", 8, 0, 0)
# simple beam: pin + torsion fix at one end, roller (axial release) at the other —
# leaving torsion free makes the matrix singular (nan results, not an error message!)
fe.def_support("N0", True, True, True, True, False, False)
fe.def_support("N2", False, True, True, False, False, False)
fe.add_member("m1", "N0", "N1", "s", "r")
fe.add_member("m2", "N1", "N2", "s", "r")
fe.add_node_load("N1", "FZ", -10.0, case="L")
fe.add_load_combo("L", {"L": 1.0})
fe.analyze(check_statics=False)
dz = fe.nodes["N1"].DZ["L"]          # meters, signed
rz = fe.nodes["N0"].RxnFZ["L"]       # reactions per support node
```

- Distributed/self-weight: `fe.add_member_dist_load(m, "FZ", -w, -w, case=...)` with
  w = A_m2 * 78.5 kN/m per member.
- Singular matrix -> results are nan/huge, PyNite may only WARN: always run the sanity
  invariants (sum reactions == applied loads, displacement scale) before trusting output.

## Solver-script contract

- Wire-fed inputs -> null-guard first lines; skip the solve and leave `solved` UNASSIGNED
  unless the analysis completed (never assign solved=False).
- `results` = ONE compact JSON string ({"solved":true,"midDzM":...,"engine":"PyNite"}).
- Verdict math stays in structural_check.py / vetted code — this engine only produces
  displacements and forces.
