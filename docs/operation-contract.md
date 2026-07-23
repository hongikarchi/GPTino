# Typed operation contract

Codex sessions can reason, draft, and consume cached state in parallel, but they
cannot write the bridge directly. AgentHost read leases may overlap; actual
Rhino/Grasshopper UI-context bridge work is currently processed sequentially.
Codex first saves one JSON payload per operation with `artifact_write`, then
submits an immutable `ChangeSet`. JSON properties and enum values are camelCase.

## ChangeSet rules

- `projectId`, `sessionId`, `baseSnapshotRevision`, and the separately supplied
  `expectedSnapshotId` must match the bound runtime and calling thread.
- `readSet` and `writeSet` contain exact resource addresses and fingerprints
  returned by `snapshot_read` or a scoped inspection.
- Every `operations[].reads` entry is covered by an actual fingerprint in
  `readSet`; every `operations[].writes` entry is covered by `writeSet`.
- A supported exact create target uses the shared optimistic sentinel
  `gptino:absent`. It passes only while that resource is still absent. This is
  supported for `createComponent`, `createRhinoPrimitive`, `createRhinoObject`,
  `bakeGeometry`, `connectWire`, and a new `setGroup`; all other resources
  require actual fingerprints.
- Every operation has a unique `operationId`, an owning adapter, and a
  session-relative `payloadArtifact` path.
- Resource fields are `*`. Object-like IDs use canonical lowercase D-format
  UUIDs; wire IDs use canonical lowercase N-format endpoint UUIDs.
- Operation write domains are exact: layout, component, wire, group, Python
  source/I/O/value, and whole Rhino object. Payload targets must equal all
  declared writes; unused expectations, extra writes, and overlapping writes by
  two operations in one ChangeSet are rejected. Python source/I/O/value writes
  on one component share a whole-component fingerprint conflict domain; a
  contiguous sequence is the sole sibling-domain exception and rolls each
  verified after-fingerprint into the next operation. A ChangeSet containing
  that sequence may not write a second Python component or any non-Wireify
  resource because solver activity can change runtime-sensitive fingerprints.
- Grasshopper component and Rhino object parent resources conflict with their
  child domains, so deletion or whole-object mutation cannot evade a
  source/layout/geometry/attributes dependency.
- Distinct Rhino object IDs cannot claim the same case-sensitive
  `logicalEntityId` in one ChangeSet; the broker rejects the batch before it is
  queued or any bridge write runs.
- Before durable acceptance, the broker validates every payload and copies its
  original JSON bytes into job-owned `.gptino-reserved` storage with a SHA-256
  digest. Validation, freezing, and bridge execution all use those unmodified
  bytes, so integer syntax and floating-point negative zero are preserved. User
  artifact writes cannot access the reserved namespace.
- An idempotency key is bound to a separate semantic canonical hash of the
  accepted request, including payload content. Object property order and
  equivalent number spellings compare equally, while negative zero remains
  distinct from positive zero. Reusing the key for different accepted content
  is rejected.
- Every live write is verified by at least one `acceptancePredicate`. Predicates
  are optional in the submission: when omitted, the broker attaches the standard
  set per write kind (creates/bakes → `objectExists`, deletes → `objectAbsent`,
  wires → `wireExists`/`wireAbsent`, everything else → `runtimeErrorAbsent`)
  before the request hash is computed. Explicit predicates are used as declared.
- `change_submit` returns a job ID (with `wait=true`, fast jobs return their
  terminal state in the same response). Only `job_status=committed` means
  success. A `failed` job carrying an `applied` block landed its writes without
  committing — deterministic script compile/runtime errors report this way; the
  session iterates by fixing the source and resubmitting with `gptino:auto`.

Resource kinds are `document`, `grasshopperComponent`,
`grasshopperComponentSource`, `grasshopperComponentIo`,
`grasshopperComponentValue`, `grasshopperComponentLayout`, `grasshopperWire`,
`grasshopperGroup`, `grasshopperSolver`, `rhinoObject`, `rhinoObjectGeometry`,
`rhinoObjectAttributes`, `rhinoLayer`, `rhinoGroup`, `rhinoMaterial`, and
`rhinoLinetype`. A resource field of `*` addresses its whole conflict domain.

Two bounded discovery tools are read-only and do not enter the writer queue:

- `component_catalog` searches installed Grasshopper component metadata with
  `{query?,limit?:1..100,includeObsolete?:boolean}`.
- `rhino_list` lists at most 500 objects using optional object/layer/name/type,
  logical-entity, and selection filters. It returns deterministic GUID order,
  per-object fingerprints and bounds, a union bound, and a truncation flag.

Use those results to choose exact component type IDs, object IDs, and base
fingerprints before drafting a ChangeSet.

Both tools acquire the shared document-read gate. Independent reads can overlap,
but once a writer is waiting, new reads wait and never overlap its exclusive
validation/mutation/verification epoch.

## Supported operations and payloads

Every payload file must contain exactly
`{"bridgeOperation":"...","arguments":{...}}`. The explicit bridge operation,
owner, and payload `operationId` must match the typed operation. The entire
batch receives local schema/shape/resource preflight before acceptance. At job
execution, every frozen `rhino.upsert` receives an additional read-only Rhino
bridge preflight before the first write in the batch. `geometryJson` must decode
to `GeometryBase`, its RhinoCommon object type must match `geometryType`, and
`IsValidWithLog` must succeed. A non-empty `attributesJson` must decode to
`ObjectAttributes`; the preflight checks its RhinoCommon type but does not
simulate applying it to the document tables. The same pass also checks the exact
live object fingerprint, requested identity, and logical-entity constraints.
This prevents invalid or type-mismatched later geometry, and a non-attribute
attributes payload, from partially applying an earlier operation. Rhino's
eventual `ObjectTable.Add`, `Replace`, or `ModifyAttributes` call can still fail
at execution time—for example because of document-table constraints—and the job
can then enter `recoveryRequired`.

Bridge application frames use `protocolVersion=4`, carry the exact bound
document target, and validate request/response correlation. Reads carry
`BridgeOperationAccess.Read` and no lease. Writes carry `Write` plus a non-empty
host-generated writer lease, and each adapter rechecks its expected access. The
model and panel receive neither the bridge secret nor a writer lease.

The typed `read` row below is an operation inside a brokered ChangeSet and thus
runs within the exclusive job epoch. It is distinct from `snapshot_read`, scoped
inspection, `component_catalog`, and `rhino_list`.
Every typed read operation must declare an empty `writes` list. A read-only
ChangeSet must also have an empty `writeSet`; in a mixed ChangeSet, `writeSet`
may cover only its write operations.

| Typed kind | Owner / bridge operation | Payload |
|---|---|---|
| `read` | owner-specific inspect | Canvas/Rhino: `{objectId}`; Wireify: `{componentId}` |
| `moveComponent`, `setLayout` | Cordyceps / `canvas.move` | `{operationId,pivots:{guid:{x,y}},expectedFingerprints:{guid:sha256}}` |
| `setValue` | Cordyceps / `canvas.setNumberSlider` | `{operationId,objectId,expectedFingerprint,value,minimum,maximum,decimalPlaces}`; only Number Slider is supported |
| `connectWire`, `disconnectWire` | Cordyceps / `canvas.setWire` | `{operationId,wire:{sourceObjectId,sourceParameterId,targetObjectId,targetParameterId},action:"connect"|"disconnect",rejectCycles:true}` |
| `createComponent` | Cordyceps / `canvas.create` | `{operationId,objectId,componentTypeId,pivot:{x,y},nickName}` |
| `deleteComponent` | Cordyceps / `canvas.delete` | `{operationId,objectId,expectedFingerprint}` |
| `setGroup` | Cordyceps / `canvas.setGroup` | `{operationId,groupId,name,objectIds,argbColor}` |
| `updatePythonSource` | Wireify / `python.setSource` | `{operationId,componentId,expectedSourceSha256,source,runtime:"cpython3"|"ironPython2",expireSolution}` |
| `setComponentIo` | Wireify / `python.setSchema` | `{operationId,componentId,inputs,outputs,preserveIncidentWires}` |
| `convertSocket` | Wireify / `python.setTyping` | `{operationId,componentId,inputParameterId,typeHint,access:"item"|"list"|"tree"}` |
| `executePython` | Wireify / `python.execute` | `{operationId,componentId,expireUpstream,recomputeDocument}` |
| `readRuntimeMessages` | Wireify / `python.runtimeMessages` | `{componentId}` |
| `createRhinoPrimitive` | Rhino / `rhino.createPrimitive` | `{operationId,objectId,logicalEntityId,kind:"point"|"line"|"polyline"|"circle"|"box"|"sphere",point?,line?,polyline?,circle?,box?,sphere?,attributes?}`; exactly one definition must match `kind` |
| `transformRhinoObject` | Rhino / `rhino.transform` | `{operationId,objectId,expectedFingerprint,matrix:{m00,m01,m02,m03,m10,m11,m12,m13,m20,m21,m22,m23,m30,m31,m32,m33}}` |
| `createRhinoObject`, `modifyRhinoObject`, `bakeGeometry`, `updateRhinoAttributes` | Rhino / `rhino.upsert` | `{operationId,objectId,logicalEntityId,geometryType,geometryJson,attributesJson,expectedFingerprint}`; `createRhinoObject`/`bakeGeometry` require payload `null` plus writeSet `gptino:absent`, while modification/attribute updates require the same inspected fingerprint in both places |
| `deleteRhinoObject` | Rhino / `rhino.delete` | `{operationId,objectId,expectedFingerprint}` |

`Rename`, `SetSolverState`, `DocumentGlobal`, and `UpdateRhinoLayer` are
reserved backend enum values: they are not advertised in the model-facing tool
schema and any ChangeSet that reaches the broker with one of them fails closed
at submit. Layer updates remain disabled until deterministic layer inspection
can prove both presence and absence.
`geometryJson` must be RhinoCommon native JSON whose actual object type matches
`geometryType`, and the decoded geometry must pass `IsValidWithLog`.
`attributesJson` is RhinoCommon `ObjectAttributes` JSON and is type-checked; an
empty string requests default/new attributes (or a duplicate of current
attributes on modify). This does not pre-validate every layer, material, group,
or other document-table constraint. `{}` is not a valid substitute for either
RhinoCommon payload.

## Verification

Supported acceptance kinds are `fingerprintEquals`, `runtimeErrorAbsent`,
`wireExists`, `wireAbsent`, `objectExists`, and `objectAbsent`. The other enum
values are reserved and fail closed. Canvas predicates are evaluated against a
fresh post-write snapshot. Python and Rhino predicates additionally use the
adapter's correlated post-operation fingerprint, including an explicit absence
observation after deletion. Any error diagnostic fails verification.

Verification failure semantics are deterministic: script-content errors
(`updatePythonSource` compile failures, `executePython` runtime errors) do not
abort the operation loop — every operation completes, the post-state snapshot is
captured, and the job ends `failed` with the full `diagnostics[]`, an `applied`
block carrying the actual post-write fingerprints, and the session resource
ledger updated to live state so the corrective resubmission is not
stale-blocked. No history revision is committed for a red state.
`recoveryRequired` is reserved for genuinely unknown outcomes: mid-write
exceptions on non-script operations, cancellation after a write, fingerprint
chain violations, history-commit failures, and restart recovery.
