# Architecture overview

```text
Rhino 8 / Grasshopper
        | protocol v2 mutually authenticated named pipe
        v
GPTino AgentHost ---- local HTTP ---- Rhino Eto WebView / gptino-term
        | stdio JSON-RPC
        v
Codex App Server
```

## Invariants

1. A `DocumentRuntime` explicitly identifies the Rhino process, Rhino document,
   Grasshopper document, paths, and binding generation.
2. Sessions reason and draft in parallel; document reads share a writer-preferring
   gate and never overlap an exclusive write epoch.
3. Session order resolves contention among ready sessions. Dependencies and
   recovery invariants remain hard constraints.
4. Exactly one executor owns the writer lease for a document runtime.
5. Plan sessions and read-only subagents cannot submit executable changes.
6. Canonical history advances only after live verification and compare-and-swap.

## Model profiles

- ReadFast: read-only, low-latency model.
- FastSafe: Terra-or-better, allowlisted reversible operations.
- Standard: general code and multi-operation work.
- HighAssurance: complex modeling, review, and shadow iteration.
- Recovery: highest available qualified model and exclusive execution.

## Deterministic core and model judgment

SkillMeld's separation between a deterministic mechanical engine and model-supplied
judgment is used here as a design constraint, not as a runtime dependency. Codex may
interpret a request, draft source, and propose a typed `ChangeSet`; deterministic
GPTino code owns target binding, model floors, session ordering, dependency checks,
conflict detection, the writer lease, verification, history, and provenance. Every
dynamic tool response is therefore data for the session, never permission to bypass
the broker.

The session orchestrator stays thin: it routes work into explicit read, artifact,
submit, and status capabilities. Adapter ownership remains split between
Wireify-derived source/runtime behavior and Cordyceps-derived topology/scene behavior,
so a broad chat instruction cannot silently expand its authority.
