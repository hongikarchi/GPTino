namespace GPTino.AgentHost.Codex;

internal static class DynamicToolSpecs
{
    private static readonly string PayloadGuide = Hosting.InstructionAssets.LoadOrFallback(
        "payload-guide.md",
        DefaultPayloadGuide);

    private const string DefaultPayloadGuide = """

        PAYLOADS — first call artifact_write with one JSON object per operation (exactly {"bridgeOperation":"...","arguments":{...}}), then set payloadArtifact to that session-relative path. Property and enum names are camelCase.
        bridgeOperation mapping:
        - moveComponent/setLayout -> canvas.move {operationId,pivots:{guid:{x,y}},expectedFingerprints:{guid:sha256}}
        - setValue -> canvas.setNumberSlider {operationId,objectId,expectedFingerprint,value,minimum,maximum,decimalPlaces} (Number Slider only)
        - connectWire/disconnectWire -> canvas.setWire {operationId,wire:{sourceObjectId,sourceParameterId,targetObjectId,targetParameterId},action:connect|disconnect,rejectCycles:true}
        - createComponent -> canvas.create {operationId,objectId,componentTypeId,pivot:{x,y},nickName}
        - deleteComponent -> canvas.delete {operationId,objectId,expectedFingerprint}
        - setGroup -> canvas.setGroup {operationId,groupId,name,objectIds,argbColor}
        - updatePythonSource -> python.setSource {operationId,componentId,expectedSourceSha256,source,runtime:csharp|cpython3|ironPython2,expireSolution} — the python.* operations drive every Rhino 8 script component regardless of language; runtime must match the component that was created. Use expectedSourceSha256:"gptino:auto" (a fresh component's seeded template hash is unknowable; the fingerprint chain still guards concurrent edits) — pass a concrete sha only to assert a specific prior source
        - setComponentIo -> python.setSchema {operationId,componentId,inputs,outputs,preserveIncidentWires}. Appends sockets only (removal unsupported): list every existing socket in order, then appended ones. Each socket is {name,access,typeHint?} — OMIT parameterId and nickName (server-assigned and reconciled by position; nickname defaults to the name; missing typeHint defaults to a generic object socket). Scalars fed by sliders stay generic (coerce in-script); any socket carrying GEOMETRY between components needs the geometry type hint (point3d, vector3d, line, curve, plane, mesh, brep, surface, geometry, ...) on BOTH ends or the receiver gets an untyped/Guid value.
        - convertSocket -> python.setTyping {operationId,componentId,inputParameterId,typeHint,access:item|list|tree}
        - executePython -> python.execute {operationId,componentId,expireUpstream,recomputeDocument}
        - readRuntimeMessages -> python.runtimeMessages {componentId}
        - createRhinoPrimitive -> rhino.createPrimitive {operationId,objectId,logicalEntityId,kind,one matching primitive definition,attributes}
        - transformRhinoObject -> rhino.transform {operationId,objectId,expectedFingerprint,matrix:{m00..m33}}
        - Rhino create/modify/bake/attributes -> rhino.upsert {operationId,objectId,logicalEntityId,geometryType,geometryJson,attributesJson,expectedFingerprint}
        - deleteRhinoObject -> rhino.delete {operationId,objectId,expectedFingerprint}
        - reads use {objectId} for canvas/Rhino or {componentId} for Wireify
        DECLARATIONS:
        - Every operation read needs a readSet fingerprint; every write needs an exact writeSet expectation. Unused expectations and payload-unrelated writes are rejected. Typed reads keep writes empty; a read-only ChangeSet keeps writeSet empty.
        - Resource ids: field='*'; lowercase D-format UUIDs (8-4-4-4-12) for object resources. A wire's writeSet id is the exact string sourceObjectId/sourceParameterId>targetObjectId/targetParameterId in N format (32 hex, no dashes) — same guids as the payload. If a payload-alignment error reports the expected id, declare exactly that string and resubmit.
        - Write domains are exact: move/layout=grasshopperComponentLayout; slider setValue=grasshopperComponentValue; component create/delete=grasshopperComponent; wire=grasshopperWire; group=grasshopperGroup; python source|schema-or-typing|execute=grasshopperComponentSource|grasshopperComponentIo|grasshopperComponentValue; every Rhino mutation=rhinoObject. Two operations in one ChangeSet cannot write overlapping domains.
        - Fingerprints are PER-DOMAIN: take the concrete fingerprint from the SAME resource kind you declare (move -> the grasshopperComponentLayout resource, setValue -> grasshopperComponentValue, delete -> grasshopperComponent). Independent edits no longer stale each other: a concurrent component move does not invalidate a pending value write.
        - Python source/I/O/value writes share whole-component state: one ChangeSet writes exactly one Python component, contiguously, with no other writes mixed in.
        - Canvas points are exactly x/y; Rhino points/vectors exactly x/y/z. Rhino geometryJson must be native RhinoCommon JSON matching geometryType; attributesJson is native ObjectAttributes JSON or "" for defaults. Distinct Rhino object IDs in one ChangeSet use distinct case-sensitive logicalEntityId values.
        BOOKKEEPING (server-owned):
        - Set expectedSnapshotId='gptino:auto', baseSnapshotRevision=-1, and existing-resource writeSet/readSet expectedFingerprint='gptino:auto' — the server fills them from this session's own last write; a genuine foreign change still Blocks.
        - Creates (createComponent, createRhinoPrimitive, createRhinoObject, bakeGeometry, connectWire, a new setGroup) use writeSet expectedFingerprint='gptino:absent'.
        - Value/geometry payload+writeSet fingerprints (setNumberSlider, move, delete, rhino transform/upsert) must be the concrete value, not gptino:auto; payload fingerprints for existing resources must exactly match writeSet. For createRhinoObject/bakeGeometry only, payload arguments.expectedFingerprint is null.
        - acceptancePredicates may be [] — the server attaches the standard set (creates/bakes objectExists, deletes objectAbsent, wires wireExists/wireAbsent, everything else runtimeErrorAbsent).
        """;

    public static object[] Create() =>
    [
        new
        {
            type = "namespace",
            name = "gptino_v1",
            description = "Read the bound Rhino/Grasshopper pair and submit centrally serialized, conflict-checked, verified changes.",
            tools = new object[]
            {
                Function(
                    "snapshot_read",
                    "Read an immutable snapshot. Parallel-safe; never acquires the writer lease. The response includes the exact sessionId and target projectId required by ChangeSet. Use exact scopes before drafting a change.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            scopes = new
                            {
                                type = "array",
                                description = "Optional reads: canvas, wireify:<component-guid>, wireify-messages:<component-guid>, or rhino:<object-guid>.",
                                items = new { type = "string" }
                            },
                            knownSnapshotId = NullableString("Return unchanged=true when this still identifies the current snapshot.")
                        },
                        additionalProperties = false
                    }),
                Function(
                    "component_catalog",
                    "Search the installed Grasshopper component catalog before creating a component. Parallel-safe and read-only.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            query = new { type = "string", description = "Name, nickname, category, subcategory, or description text." },
                            limit = new { type = "integer", minimum = 1, maximum = 100, description = "Maximum deterministic matches; default 25." },
                            includeObsolete = new { type = "boolean", description = "Include obsolete components; default false." }
                        },
                        additionalProperties = false
                    }),
                Function(
                    "rhino_list",
                    "List or filter objects in the exact bound Rhino document. Parallel-safe and read-only; use returned IDs and fingerprints for changes.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            limit = new { type = "integer", minimum = 1, maximum = 500, description = "Maximum objects; default 100." },
                            objectId = Uuid(),
                            layerId = Uuid(),
                            layerFullPath = new { type = "string" },
                            name = new { type = "string" },
                            nameContains = new { type = "string" },
                            geometryType = new { type = "string" },
                            logicalEntityId = new { type = "string" },
                            selected = new { type = "boolean" }
                        },
                        additionalProperties = false
                    }),
                Function(
                    "inspect_outputs",
                    "Read a component's live output data: per-output DataCount, TypeNames, GeometryBounds, and capped " +
                    "sample values. Use it to ground input access (item/list/tree), type hints, and to verify a script " +
                    "produced sensible geometry — never guess the data when you can read it. Committed jobs already " +
                    "include the same report under committed.outputs; call this for ad-hoc inspection when idle. If a " +
                    "writer session is active this returns writerActive=true immediately instead of queueing.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            objectId = new { type = "string", format = "uuid", description = "Grasshopper component object id." }
                        },
                        required = new[] { "objectId" },
                        additionalProperties = false
                    }),
                Function(
                    "artifact_read",
                    "Read a draft artifact belonging only to this chat session.",
                    new
                    {
                        type = "object",
                        properties = new { path = new { type = "string" } },
                        required = new[] { "path" },
                        additionalProperties = false
                    }),
                Function(
                    "artifact_write",
                    "Write code or a structured operation payload into this chat session's isolated draft storage. This " +
                    "never changes Rhino or Grasshopper. Operation payloads are exactly one JSON object " +
                    "{\"bridgeOperation\":\"...\",\"arguments\":{...}} — the full mapping is documented on change_submit.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            path = new { type = "string", description = "Session-relative path such as operations/move-01.json; traversal and the broker-owned .gptino-reserved namespace are rejected." },
                            content = new { type = "string", description = "UTF-8 text. Operation payloads must contain one JSON object." }
                        },
                        required = new[] { "path", "content" },
                        additionalProperties = false
                    }),
                Function(
                    "change_submit",
                    "Submit a typed ChangeSet to the central single-writer broker. Pass wait=true to receive the terminal " +
                    "result (state, diagnostics, committed view with sockets/outputs) in this same response for fast jobs. " +
                    "If the returned state is still queued/executing — normal when other sessions are ahead — fall back to " +
                    "polling job_status; the jobId is always returned. state=failed with an applied block means the writes " +
                    "landed but did not commit (e.g. script compile/runtime errors): read diagnostics[], fix, and resubmit " +
                    "with gptino:auto — the retry is not stale-blocked. " + PayloadGuide,
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            changeSet = ChangeSetSchema(),
                            expectedSnapshotId = new { type = "string", description = "gptino:auto to let the server anchor to the current snapshot, or the exact snapshotId returned by snapshot_read." },
                            idempotencyKey = new { type = "string", description = "Stable unique key for retrying this logically identical submission." },
                            summary = new { type = "string", description = "Short user-visible queue/history summary." },
                            wait = new { type = "boolean", description = "Block briefly (bounded well under the tool deadline) for the terminal result; default false. Timeout is normal, not an error — poll job_status then." }
                        },
                        required = new[] { "changeSet", "expectedSnapshotId", "idempotencyKey", "summary" },
                        additionalProperties = false
                    }),
                Function(
                    "job_status",
                    "Read queue, execution, verification, commit, recovery-required, or failure state for a submitted job. " +
                    "Terminal states include diagnostics[] (per-operation errors/warnings/remarks from the live solve). " +
                    "A committed job includes committed { snapshotId, revision, resources[].fingerprint, sockets, outputs }: " +
                    "base the next ChangeSet on these fingerprints, wire using the Grasshopper-assigned socket ids in " +
                    "committed.sockets, and verify results from committed.outputs instead of calling snapshot_read again. " +
                    "A failed job with an applied block landed its writes without committing (script errors report this " +
                    "way): read diagnostics[], fix the source, resubmit with gptino:auto.",
                    new
                    {
                        type = "object",
                        properties = new { jobId = new { type = "string", format = "uuid" } },
                        required = new[] { "jobId" },
                        additionalProperties = false
                    }),
                Function(
                    "skill_read",
                    "Read a built-in GPTino skill: vetted Python sources and reference notes shipped with the plugin. " +
                    "The available skills are indexed in your instructions. Use skill code verbatim for conventional " +
                    "plumbing such as baking; adapt reference notes freely.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            name = new { type = "string", description = "Skill file name from the index, for example bake_manager.py." }
                        },
                        required = new[] { "name" },
                        additionalProperties = false
                    }),
                Function(
                    "memory_append",
                    "Append a durable note to this project's MEMORY.md (append-only, folded into every future session for " +
                    "this project). Use ONLY for a non-obvious, reusable lesson: a symptom -> cause -> fix, a hard project " +
                    "constraint, or a convention the user confirmed. One concise entry; never restate the obvious, the " +
                    "current task, or code the repo already records. Refused if MEMORY.md is near its size cap.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            entry = new { type = "string", description = "Markdown note to append, e.g. a short '## Title' with symptom/cause/fix lines." }
                        },
                        required = new[] { "entry" },
                        additionalProperties = false
                    })
            }
        }
    ];

    private static object ChangeSetSchema() => new
    {
        type = "object",
        description = "Immutable optimistic-concurrency contract. IDs and fingerprints must come from the bound snapshot/inspections.",
        properties = new
        {
            changeSetId = Uuid(),
            projectId = Uuid(),
            sessionId = Uuid(),
            baseSnapshotRevision = new { type = "integer", minimum = -1, description = "-1 to let the server anchor to the current revision, or the exact revision from snapshot_read/job_status." },
            baseGitCommit = NullableString("Managed-history HEAD from the snapshot, or null before baseline."),
            dependencies = new { type = "array", items = Uuid() },
            readSet = new { type = "array", items = ResourceExpectationSchema() },
            writeSet = new { type = "array", items = ResourceExpectationSchema() },
            operations = new { type = "array", minItems = 1, items = TypedOperationSchema() },
            acceptancePredicates = new { type = "array", items = PredicateSchema() },
            rollbackBeforeImages = new { type = "array", items = RollbackSchema() },
            createdAt = new { type = "string", format = "date-time" }
        },
        required = new[]
        {
            "changeSetId", "projectId", "sessionId", "baseSnapshotRevision", "baseGitCommit",
            "dependencies", "readSet", "writeSet", "operations", "acceptancePredicates",
            "rollbackBeforeImages", "createdAt"
        },
        additionalProperties = false
    };

    private static object TypedOperationSchema() => new
    {
        type = "object",
        properties = new
        {
            operationId = new { type = "string", minLength = 1 },
            kind = Enum(
                "read", "moveComponent", "connectWire", "disconnectWire", "setValue",
                "updatePythonSource", "setComponentIo", "convertSocket", "createComponent", "deleteComponent",
                "setLayout", "createRhinoObject", "modifyRhinoObject", "deleteRhinoObject",
                "bakeGeometry", "updateRhinoAttributes", "setGroup",
                "executePython", "readRuntimeMessages", "createRhinoPrimitive", "transformRhinoObject"),
            owner = Enum("wireify", "cordyceps", "rhinoBridge"),
            reads = new { type = "array", items = ResourceAddressSchema() },
            writes = new { type = "array", items = ResourceAddressSchema() },
            reversible = new { type = "boolean" },
            payloadArtifact = new { type = "string", minLength = 1, description = "Path previously written with artifact_write in this same session." }
        },
        required = new[] { "operationId", "kind", "owner", "reads", "writes", "reversible", "payloadArtifact" },
        additionalProperties = false
    };

    private static object ResourceExpectationSchema() => new
    {
        type = "object",
        properties = new
        {
            resource = ResourceAddressSchema(),
            expectedFingerprint = new
            {
                type = "string",
                minLength = 1,
                description = "gptino:auto (server fills it from this session's own last commit), the actual snapshot fingerprint, or gptino:absent only for a supported exact create target."
            }
        },
        required = new[] { "resource", "expectedFingerprint" },
        additionalProperties = false
    };

    private static object ResourceAddressSchema() => new
    {
        type = "object",
        properties = new
        {
            kind = Enum(
                "document", "grasshopperComponent", "grasshopperComponentSource", "grasshopperComponentIo",
                "grasshopperComponentValue", "grasshopperComponentLayout", "grasshopperWire", "grasshopperGroup",
                "rhinoObject", "rhinoObjectGeometry", "rhinoObjectAttributes"),
            id = new { type = "string", minLength = 1 },
            field = new { type = "string", minLength = 1, description = "Use * for the whole conflict domain." }
        },
        required = new[] { "kind", "id", "field" },
        additionalProperties = false
    };

    private static object PredicateSchema() => new
    {
        type = "object",
        properties = new
        {
            name = new { type = "string", minLength = 1 },
            kind = Enum(
                "fingerprintEquals", "runtimeErrorAbsent", "wireExists", "wireAbsent",
                "objectExists", "objectAbsent"),
            resource = new { oneOf = new object[] { ResourceAddressSchema(), new { type = "null" } } },
            expectedValue = NullableString("Expected fingerprint/value, or null for existence and runtime-error checks.")
        },
        required = new[] { "name", "kind", "resource", "expectedValue" },
        additionalProperties = false
    };

    private static object RollbackSchema() => new
    {
        type = "object",
        description = "Optional provenance-only before image in this alpha; a failed live write is marked recoveryRequired rather than silently rolled back.",
        properties = new
        {
            resource = ResourceAddressSchema(),
            artifactReference = new { type = "string", minLength = 1 },
            fingerprint = new { type = "string", minLength = 1 }
        },
        required = new[] { "resource", "artifactReference", "fingerprint" },
        additionalProperties = false
    };

    private static object Uuid() => new { type = "string", format = "uuid" };

    private static object NullableString(string description) =>
        new { type = new[] { "string", "null" }, description };

    private static object Enum(params string[] values) => new { type = "string", @enum = values };

    private static object Function(string name, string description, object inputSchema) => new
    {
        type = "function",
        name,
        description,
        inputSchema
    };
}
