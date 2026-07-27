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

## Implication for remaining tiers

The only quality-preserving lever is **per-call inference time**, not call count:
- Tier 2 (skills/templates): the model retrieves verified C# instead of composing each op from
  scratch → less reasoning per operation.
- Tier 3 (prompt slimming): fewer input tokens per call → faster inference per call.
- Parallel authoring (real wall-clock win) needs the deferred coordinator.
