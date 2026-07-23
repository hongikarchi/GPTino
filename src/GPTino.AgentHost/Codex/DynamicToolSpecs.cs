namespace GPTino.AgentHost.Codex;

internal static class DynamicToolSpecs
{
    private const string PayloadGuide =
        "First call artifact_write with one JSON request object per operation, then set payloadArtifact to that session-relative path. " +
        "The artifact must be exactly {bridgeOperation,arguments}; bridgeOperation is mandatory and must match this mapping: " +
        "moveComponent/setLayout=canvas.move {operationId,pivots:{guid:{x,y}},expectedFingerprints:{guid:sha256}}; " +
        "setValue=canvas.setNumberSlider {operationId,objectId,expectedFingerprint,value,minimum,maximum,decimalPlaces}; only Number Slider is supported; " +
        "connectWire/disconnectWire=canvas.setWire {operationId,wire:{sourceObjectId,sourceParameterId,targetObjectId,targetParameterId},action:connect|disconnect,rejectCycles:true}; " +
        "createComponent=canvas.create {operationId,objectId,componentTypeId,pivot:{x,y},nickName}; " +
        "deleteComponent=canvas.delete {operationId,objectId,expectedFingerprint}; " +
        "setGroup=canvas.setGroup {operationId,groupId,name,objectIds,argbColor}; " +
        "updatePythonSource=python.setSource {operationId,componentId,expectedSourceSha256,source,runtime:cpython3|ironPython2,expireSolution}; " +
        "setComponentIo=python.setSchema {operationId,componentId,inputs,outputs,preserveIncidentWires}; " +
        "Python schema may append sockets only (socket removal is unsupported): list every existing socket in order followed by appended sockets. Socket UUIDs are managed by the server: existing sockets keep their id and appended sockets are assigned one, so any placeholder UUID you supply is reconciled by position — you only control each socket's name, access, and type hint. Scalar sockets fed by sliders stay generic (type object/int/double, coerced in-script), but a socket that carries GEOMETRY wired between components must use the geometry type hint (point3d, vector3d, line, curve, plane, mesh, brep, surface, geometry, ...) on both the output and the input, or the receiver gets an untyped/Guid value; " +
        "convertSocket=python.setTyping {operationId,componentId,inputParameterId,typeHint,access:item|list|tree}; " +
        "executePython=python.execute {operationId,componentId,expireUpstream,recomputeDocument}; " +
        "readRuntimeMessages=python.runtimeMessages {componentId}; " +
        "createRhinoPrimitive=rhino.createPrimitive {operationId,objectId,logicalEntityId,kind,one matching primitive definition,attributes}; " +
        "transformRhinoObject=rhino.transform {operationId,objectId,expectedFingerprint,matrix:{m00..m33}}; " +
        "Rhino create/modify/bake/attributes=rhino.upsert {operationId,objectId,logicalEntityId,geometryType,geometryJson,attributesJson,expectedFingerprint}; " +
        "deleteRhinoObject=rhino.delete {operationId,objectId,expectedFingerprint}. " +
        "Read operations use {objectId} for canvas/Rhino or {componentId} for Wireify. JSON property and enum names are camelCase. " +
        "Every operation read needs a readSet fingerprint and every write needs an exact writeSet expectation; unused expectations and extra payload-unrelated writes are rejected. " +
        "Every typed read operation must keep its writes empty; a read-only ChangeSet also keeps writeSet empty, while a mixed ChangeSet uses writeSet only for its write operations. Canvas points require exactly x/y and Rhino points or vectors exactly x/y/z. " +
        "Use field='*' and canonical lowercase UUIDs: D format (8-4-4-4-12 with dashes) for object resources. " +
        "A wire's writeSet resource id is the exact string sourceObjectId/sourceParameterId>targetObjectId/targetParameterId " +
        "where each id is N format (32 hex, no dashes) — same guids as the wire payload. If a payload-alignment error " +
        "reports the expected id, declare that exact string and resubmit. " +
        "Write domains are exact: move/layout=grasshopperComponentLayout; Number Slider setValue=grasshopperComponentValue; component create/delete=grasshopperComponent; wire=grasshopperWire; group=grasshopperGroup; " +
        "Python source/schema-or-typing/execute=grasshopperComponentSource/grasshopperComponentIo/grasshopperComponentValue; every Rhino mutation=rhinoObject. " +
        "Python source/I/O/value writes share runtime-sensitive whole-component state: one ChangeSet may write exactly one Python component, those writes must be contiguous, and no other writes may be mixed in. " +
        "Optimistic-concurrency bookkeeping is automatic: set existing-resource writeSet/readSet expectedFingerprint to 'gptino:auto', expectedSnapshotId to 'gptino:auto', and baseSnapshotRevision to -1 — the server fills them from this session's own last commit, and a foreign change still Blocks. " +
        "CreateComponent, CreateRhinoPrimitive, CreateRhinoObject, BakeGeometry, ConnectWire, and a new SetGroup use writeSet expectedFingerprint='gptino:absent' for the exact new resource. " +
        "Value/geometry payload+writeSet fingerprints (setNumberSlider, move, delete, rhino transform/upsert) must be the concrete value, not gptino:auto. For CreateRhinoObject/BakeGeometry only, payload arguments.expectedFingerprint is null. " +
        "Rhino geometryJson must be native RhinoCommon JSON and match geometryType; attributesJson is native ObjectAttributes JSON, or an empty string for default/new attributes. Distinct Rhino object IDs in one ChangeSet must use distinct case-sensitive logicalEntityId values. " +
        "Payload fingerprints for existing canvas/Rhino resources must exactly match writeSet, and two operations in one ChangeSet cannot write overlapping domains. " +
        "Every write needs an explicit supported acceptance predicate.";

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
                    "Write code or a structured operation payload into this chat session's isolated draft storage. This never changes Rhino or Grasshopper. " + PayloadGuide,
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
                    "polling job_status; the jobId is always returned. " + PayloadGuide,
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
                    "committed.sockets, and verify results from committed.outputs instead of calling snapshot_read again.",
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
