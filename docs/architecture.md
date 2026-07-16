# Architecture

GPTino runs one orchestration set for one explicitly saved Rhino/Grasshopper
file pair. The Rhino panel is the primary UI; there is no second dashboard to
keep in sync. A session's **Terminal** button attaches the terminal client to
that same persistent session and history.

```text
Rhino panel / session terminals
              |
       loopback HTTP + token
              |
        AgentHost (one pair)
       /        |          \
Codex threads   |      SQLite chat/session state
 (parallel)     |
                v
        deterministic broker
       priority list + conflicts
                |
        exactly one writer lease
                |
 mutually authenticated named pipe (protocol v2 / HMAC-SHA-256)
          /                    \
 Grasshopper bridge          Rhino bridge
 Wireify-owned Python        Cordyceps-owned scene
 Cordyceps canvas/topology   operations
```

## File-pair lifecycle

The Rhino plug-in waits until both documents are saved and form an unambiguous
pair. Their normalized paths produce a stable project ID. Only then does it
start AgentHost, generate an ephemeral pipe secret and API token, and register
the exact Rhino process, Rhino document serial, Grasshopper document GUID,
paths, and generation. A bridge response for any other target is rejected.
Before opening SQLite or recovering jobs, AgentHost acquires an OS-enforced
exclusive file handle in that pair's canonical data directory. A second Rhino
process targeting the same pair fails fast instead of sharing queues, history,
or an in-process writer; the operating system releases the handle on exit or
crash without deleting and recreating the lock file.

Changing or closing either document tears down that pair's connection. GPTino
never falls back to whichever Rhino or Grasshopper document happens to be
active.

## Sessions, agents, and priority

Each visible session owns a persistent Codex App Server thread, chat history,
isolated draft-artifact directory, model preference, pause state, and position
in the user's ordered list. Multiple sessions can inspect snapshots, reason,
and prepare code concurrently. They are not allowed to call the live document
bridge directly.

Read requests are concurrent and snapshots are briefly cached at the AgentHost
layer. A writer-preferring document gate lets independent reads overlap, but
blocks new reads once a write is waiting and keeps validation, mutation, and
post-write verification in one exclusive write epoch. RhinoCommon and
Grasshopper object access is still marshalled onto their required UI/document
context, so "parallel reads" means sessions do not block one another's reasoning
or draft work; it does not bypass Rhino's thread-safety rules or promise
simultaneous UI-thread execution. The current Rhino-side receive loop processes
bridge/UI-context requests sequentially even when AgentHost read leases overlap.

The ordered session list is the priority policy. Dragging a session changes a
versioned order in SQLite. The broker recomputes the ready queue immediately;
among jobs that can run, the highest visible session wins, then enqueue order.
There are no ambiguous P0/P1 ties.

When a session submits a `ChangeSet`, the broker performs the following steps:

1. authenticate the calling Codex thread and bind it to its session;
2. require the exact project, session, snapshot ID, and idempotency key;
3. compare queued read/write domains and show conflicts;
4. recapture the Grasshopper canvas and re-inspect touched Python/Rhino
   resources;
5. compare touched fingerprints and ask RhinoCommon, without mutating the
   document, to decode each frozen generic Rhino upsert, check geometry type and
   `IsValidWithLog`, and require non-empty attributes JSON to decode as
   `ObjectAttributes`;
6. issue a single writer lease and execute typed operations in order through the
   owning adapter;
7. recapture state, combine adapter result fingerprints, and evaluate every
   explicit acceptance predicate;
8. only after verification, commit canonical state and provenance to the
   managed text-history repository.

Steps 4–8 run inside one exclusive write epoch. A waiting writer blocks later
read leases, so no inspection can observe a mixture of pre- and post-write state.

A model saying that work succeeded has no effect on job state.

## Adapter ownership

- Wireify semantics own Python source, runtime selection, parameter schema,
  socket typing, execution, and runtime messages.
- Cordyceps semantics own Grasshopper canvas objects, layout, groups, wire
  topology, snapshots, recovery boundaries, and Rhino scene mutations.
- GPTino ports the required behavior into its own assemblies. End users do not
  install Wireify or Cordyceps and do not configure their ports.

The exact supported operation and payload contract is documented in
[operation-contract.md](operation-contract.md).

Source, I/O, and value mutations of one Python component share Wireify's whole
component fingerprint. Competing jobs therefore conflict, while consecutive
mutations inside one ChangeSet carry the previous operation's verified
after-fingerprint forward. Because a Grasshopper solve can change runtime
messages, that ChangeSet may write only one Python component and cannot mix in
another kind of write. Layout remains a separate Cordyceps domain in other jobs.

## Model routing

`Auto` is a policy, not a fixed model. GPTino classifies each request before a
turn. Read-only work may use the fastest available read model; exact small
move/wire operations may use a live-write-qualified fast model. Python,
I/O-schema, geometry/topology, delete/bake, runtime-error, ambiguous, recovery,
or large-context work has a high-assurance floor. A user's Deep choice is never
downgraded, and a pinned weak model cannot bypass the floor. If the model catalog
cannot prove that a high-assurance model is available, the turn fails closed.

Routing decides who may plan; typed operations, fingerprints, the broker, and
verification still decide what may mutate the documents.

## Security boundary

- HTTP listens on an ephemeral loopback address only and requires a random
  runtime token. If an HTTP `Origin` header is present, it must be one exact
  absolute same-scheme loopback origin for that ephemeral authority; opaque,
  malformed, multiple, or cross-port origins are rejected.
- The Rhino parent requests a short-lived, single-use panel nonce with a
  separate parent credential bound to the exact Rhino document serial. The
  panel exchanges that nonce for an HttpOnly, SameSite=Strict session cookie
  and redirects to a clean URL.
- The named pipe is current-user-only and protocol v2 mutually authenticates
  server and client with role-separated HMAC proofs bound to endpoint, nonces,
  and peer IDs. Correlation IDs are checked, the server handshake has a
  three-second deadline, Windows uses `CurrentUserOnly | FirstPipeInstance`, and
  reconnect occurs only after the prior connection is released. The bridge
  secret is inherited only by the file-pair runtime.
- Codex child processes receive neither the panel parent credential nor any
  `GPTINO_*` environment variable.
- GPTino's dynamic artifact tools bind every path to the calling session and
  reject traversal or reparse-point escapes. Codex threads still share the
  user's OS account and project filesystem permissions; this is workflow
  isolation, not an OS confidentiality boundary. Do not place secrets in chat
  or draft artifacts.
- Package validation rejects credentials, `.mcp.json`, runtime databases,
  document binaries, debug symbols, and secrets.

## Failure semantics

Stopping before a write is `cancelled`. Once a write request may have reached
Rhino or Grasshopper, a disconnect, cancellation, failed predicate, or history
failure becomes `recoveryRequired`; GPTino does not report success or silently
pretend that an in-memory rollback occurred. `RollbackBeforeImages` are retained
as provenance in the current alpha but are not yet an automatic rollback engine.

The alpha persists sessions, order, chat history, accepted ChangeSets,
idempotency keys, and every live-job phase in SQLite with full synchronous WAL
durability. A process crash marks interrupted session turns failed and converts
unfinished live jobs to `recoveryRequired` on restart. It never replays those
mutations blindly. Keep ordinary `.3dm`/`.gh` backups until automatic
before-image rollback has completed live Rhino validation.
