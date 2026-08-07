Structural domain guide — pipeline layers, ULS/SLS load combos, deflection limits, verdict rules. Pair with gh-pynite-cookbook.md.

Domain knowledge for structural-analysis tasks. The HOW-TO-CODE lives in
gh-pynite-cookbook.md (fetch both); this file is the WHAT-AND-WHY: what a valid
structural model needs, which loads feed which check, and how to read results without
inventing verdict math. For checking EXISTING Rhino geometry, the host pipeline
(structural_extract → ask-backs → structural_solve) is the primary path and already
embeds these rules; this guide governs definition-side (Grasshopper) analysis work.

## The pipeline and who owns each layer

```
[1] model input     geometry -> structural axes + supports + loads (G/Q tagged) + sections
[2] load combos     ULS (factored) for strength, SLS (unfactored) for deflection
[3] solve           PyNite (host structural_solve, or in-canvas per gh-pynite-cookbook.md)
[4] raw output      displacements / member forces / reactions
[5] verdicts        DETERMINISTIC CODE ONLY. Deflection limits: the host solve's built-in
                    L/ratio member check, or in-canvas the vetted structural_check.py
                    payload (skill_read + wire verbatim, like bake_manager.py). Code-based
                    strength/utilization checks (EC3-style member design) are NOT in the
                    current check set — say so rather than improvising one
[6] interpretation  YOU read the numbers and explain; you never do the safety arithmetic
```

Never compute pass/fail thresholds in ad-hoc script code or in your head. Your job in
[6] is translation ("that girder is at 4x its deflection limit — span or section"), not
arithmetic.

## Model input rules ([1])

- A structural model is geometry + supports + loads + sections/materials. Geometry alone
  is not a model; refuse to "analyze" until supports and loads are defined (ask or state
  assumptions explicitly in your report).
- Supports: fixed (all 6 DOF) for column bases cast into foundations; pin (translations
  only) for typical connections; a simply-supported beam is pin + roller (one end must
  release axial DOF or transverse load cases become artificially restrained). PyNite
  needs torsion fixed at at least one support or the matrix goes singular (nan results).
- Loads: tag every load as G (permanent/dead) or Q (variable/live) when the user gives
  real loads — the combination layer needs the split. Self-weight is automatic in the
  host structural_solve; in-canvas it is NOT — add it explicitly or state it is excluded.
- Node coincidence: members only connect where their axis endpoints coincide within
  tolerance. Crossing lines do NOT connect.

## Load combinations ([2]) — never mix the two questions

- "Does it break?" (strength) uses ULS factored loads: 1.35·G + 1.5·Q (EC0 base case;
  ψ factors on secondary variable actions).
- "Does it sag/annoy?" (deflection, vibration) uses SLS unfactored (characteristic) loads.
- Running a strength judgment on unfactored loads is UNSAFE (non-conservative); running
  deflection on factored loads over-reports. PyNite load combos: define separate cases
  and `add_load_combo` with the factors — one solve serves both questions.

## Reading results ([4]->[6])

- Deflection limits are span- and finish-dependent SLS conventions, not universal:
  simply-supported span L: total deflection ~ L/250 (appearance/general), L/300 where
  brittle finishes could crack. Cantilever with overhang a: use the equivalent-span
  convention (limit ~ a/125 total) — NEVER apply L/250 to a cantilever directly.
- Sanity invariants to report every time (they catch model-input errors): sum of support
  reactions must mirror applied loads (sign included); deflection direction must match
  gravity; model mass must be plausible (length x area x density).
- PyNite may only WARN (or silently produce nan/huge values) on a singular model —
  always run the sanity invariants before trusting output.

## Scope honesty

- This layer gives early-design feedback, not a stamped structural design. Say so when
  the user asks for "final" verification: code-complete member design (utilization, LTB,
  shear, connections, fire) is beyond the current check set.
- Boundary conditions carry assumptions the geometry cannot prove (podium bearings,
  connection stiffness): name every support assumption in every report, and route
  ambiguity back to the user as a question, never as a silent guess.
