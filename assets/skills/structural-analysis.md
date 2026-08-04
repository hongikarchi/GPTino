Structural domain guide — pipeline layers, ULS/SLS load combos, deflection limits, verdict rules. Fetch with gh-karamba-cookbook.md.

Domain knowledge for structural-analysis tasks. The HOW-TO-CODE lives in
gh-karamba-cookbook.md (fetch both); this file is the WHAT-AND-WHY: what a valid
structural model needs, which loads feed which check, and how to read results without
inventing verdict math.

## The pipeline and who owns each layer

```
[1] model input     geometry -> structural axes + supports + loads (G/Q tagged) + sections
[2] load combos     ULS (factored) for strength/utilization, SLS (unfactored) for deflection
[3] solve           Karamba script (AnalyzeThI default)
[4] raw output      displacements / member forces / reactions
[5] verdicts        DETERMINISTIC CODE ONLY: Karamba Utilization/OptiCroSec, or the vetted
                    structural_check.py payload (skill_read + wire verbatim, like bake_manager.py)
[6] interpretation  YOU read the numbers and explain; you never do the safety arithmetic
```

Never compute pass/fail thresholds in ad-hoc script code or in your head. Your job in
[6] is translation ("column at 89% — tight, consider one size up"), not arithmetic.

## Model input rules ([1])

- A structural model is geometry + supports + loads + sections/materials. Geometry alone
  is not a model; refuse to "analyze" until supports and loads are defined (ask or state
  assumptions explicitly in your report).
- Supports: fixed (all 6 DOF) for column bases cast into foundations; pin (translations
  only) for typical connections; a simply-supported beam is pin + roller (one end must
  release axial DOF or transverse load cases become artificially restrained).
- Loads: tag every load as G (permanent/dead) or Q (variable/live) when the user gives
  real loads — the combination layer needs the split. Self-weight is NOT automatic:
  add it explicitly (gravity load) or state that it is excluded.
- Node coincidence: members only connect where their axis endpoints coincide within
  tolerance. Crossing lines do NOT connect. Subdivision creates elements; element count
  (after subdivision) is what the trial cap (20 beams / 50 shells) counts.

## Load combinations ([2]) — never mix the two questions

- "Does it break?" (strength/utilization) uses ULS factored loads: 1.35·G + 1.5·Q
  (EC0 base case; ψ factors on secondary variable actions).
- "Does it sag/annoy?" (deflection, vibration) uses SLS unfactored (characteristic) loads.
- Running utilization on unfactored loads is UNSAFE (non-conservative); running deflection
  on factored loads over-reports. Until Karamba LoadCaseCombination support is verified,
  run two analyses (factored / unfactored load values) and route each to its check.

## Which analysis when ([3])

- Default: first-order AnalyzeThI. Slender/sway-sensitive structures: second-order ThII
  when asked or when axial loads are significant.
- Eigenmodes / natural vibrations: only when dynamics (vibration, comfort) is asked.
- Global buckling eigenvalue analysis: out of initial scope (facade of version-unstable
  API) — say so rather than improvising.
- Member buckling is INSIDE the EC3 utilization check and depends on buckling length:
  use the PHYSICAL member length, not the subdivided element length — a subdivided
  column checked with element-length buckling is unsafe. Set buckling lengths explicitly.

## Reading results ([4]->[6])

- Deflection limits are span- and finish-dependent SLS conventions, not universal:
  simply-supported span L: total deflection ~ L/250 (appearance/general), L/300 where
  brittle finishes could crack. Cantilever with overhang a: use the equivalent-span
  convention (limit ~ a/125 total) — NEVER apply L/250 to a cantilever directly.
- Utilization: <= 1.0 passes the code check; 0.9-1.0 is tight (flag it); report the
  numeric margin, not just pass/fail.
- Sanity invariants to report every time (they catch model-input errors): sum of support
  reactions must mirror applied loads (sign included); deflection direction must match
  gravity; model mass must be plausible (length x area x density).
- Karamba reports many model problems via warning strings, not exceptions — quote the
  warning in your report; an empty warning plus equilibrium is your "model is sane" signal.

## Scope honesty

- Trial license: 20 beam / 50 shell ELEMENTS (subdivision counts). Past the cap, tell the
  user a license is needed — do not silently shrink their model.
- This layer gives early-design feedback, not a stamped structural design. Say so when
  the user asks for "final" verification: code-complete member design (LTB, shear,
  connections, fire) is beyond the current check set.
