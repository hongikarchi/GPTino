# GPTino contributor instructions

- Keep the active Rhino and Grasshopper target explicit; never use an active-document fallback for writes.
- Reads may run concurrently against immutable snapshots. Live writes must pass through the single-writer broker.
- Wireify-derived behavior owns Python source, parameter typing, execution, and runtime errors.
- Cordyceps-derived behavior owns canvas topology, layout, groups, snapshots, and Rhino scene operations.
- Never commit `.3dm`, `.gh`, credentials, MCP configuration, runtime databases, or chat transcripts.
- Preserve attribution for ported implementation and update `docs/upstream-map.json`.
- Use document units and tolerances; do not hard-code geometry epsilons.
- A model's claim of success is not verification. Executor predicates decide success.
