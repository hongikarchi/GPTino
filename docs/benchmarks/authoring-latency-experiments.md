# Authoring-latency improvement campaign — 2026-07-28

Goal: cut the deep-profile authoring wall-clock (a 1000-panel pipeline took ~15 min) WITHOUT
lowering quality. Method: dev-loop the fixed benchmark (`authoring-task.txt`: reference surface
→ 20×15 grid → attractor openings → 30 mm solids), decompose wall-clock with
`scripts/dev-latency.ps1` (model-inference gaps vs GPTino tool-handling). One run per config
(model inference is noisy; treat <10% deltas as within variance).

## Phase 0 — baseline (deep) — DECISIVE

| metric | value |
|---|---|
| wall-clock (turn) | 517 s (tool span 457.6 s) |
| **model-inference (gaps)** | **446 s — 98%** |
| GPTino tool-handling | 11.4 s — 2% |
| tool calls | 65 (18 change_submit, 40 artifact_write, 3 inspect_outputs, …) |
| commits | 18 |

**The bottleneck is 100% model inference.** change_submit (solve+verify) is 8.1 s total = 1.8%.
`job_status` polling = 0 (change_submit is synchronous). This kills two planned levers outright:
- Tier 1c (polling): no polling exists.
- Tier 2 (verification lightening): verification is ~8 s, nothing to win.

It also shows effort-reduction (Tier 1b) is only the obvious speed/quality tradeoff, and per-stage
effort routing is impossible today (effort is per-MESSAGE via MessageRoutingPolicy; per-stage needs
a host coordinator = deferred). So the campaign targets **quality-preserving inference reduction**.

## Tier 1a — batch creates/groups + skip redundant execute — ROLLED BACK

Instruction change: create all components+sliders in one upfront ChangeSet, batch all setGroup,
skip standalone executePython, verify intermediate stages only when downstream typing depends.

| metric | baseline | Tier 1a | verdict |
|---|---|---|---|
| wall-clock (tool span) | 457.6 s | 500.7 s | ❌ +9% |
| model-inference | 446 s | 493 s | ❌ +10% |
| commits | 18 | 15 | ✔ −3 |
| tool calls | 65 | 73 | ❌ +12% |
| artifact_write | 40 | 54 | ❌ +14 |

**Negative result.** Batching cut commits (18→15) but INCREASED operations (artifact_write 40→54)
and total calls, so model inference rose and wall-clock got slightly worse. Lesson: **model
inference scales with the number of OPERATIONS (irreducible work), not the number of ChangeSets.**
Reducing round-trips is not a lever. Rolled back (git revert).

## Tier 2 — paneling cookbook skill — KEPT (modest net positive, mechanism validated)

Added `gh-paneling-cookbook.md` (vetted isotrim / attractor / CreateOffsetBrep idioms) + a
house-rules pointer to fetch it. The model fetched it (3 skill_reads) and built a clean 4-stage
grouped chain (`01 Base+Attractor → 02 UV Grid → 03 Openings → 04 30mm Solids`, 21 objects, 0
conflicts/fails).

| metric | baseline | Tier 2 | verdict |
|---|---|---|---|
| wall-clock (turn) | 517 s | 463 s | ✔ −10% |
| model-inference | 446 s | 389 s | ✔ −13% |
| **per-call inference** | 6.9 s/call | **4.1 s/call** | ✔ **−40%** |
| tool calls | 65 | 94 | ↑ +45% |
| commits | 18 | 18 | = |
| change_submit avg | 449 ms | 1500 ms | heavier solves |

**The per-call-reasoning lever WORKS**: with ready idioms the model reasoned ~40% less per
operation. But it reinvested most of that into MORE operations (94 vs 65 calls) and heavier solves
(change_submit 449 ms→1.5 s — it actually built the CreateOffsetBrep solids, likely a *more
complete* result), so net wall-clock only −10%. Quality equal-or-better (numbered stage groups,
real 30 mm solids). Kept. Caveat: one run each; the −10% wall-clock is within noise range, but the
−40% per-call inference is large and structural (skill demonstrably fetched + used).

## Implication for remaining tiers

The only quality-preserving lever is **per-call inference time**, not call count:
- Tier 2 (skills/templates): the model retrieves verified C# instead of composing each op from
  scratch → less reasoning per operation.
- Tier 3 (prompt slimming): fewer input tokens per call → faster inference per call.
- Parallel authoring (real wall-clock win) needs the deferred coordinator.
