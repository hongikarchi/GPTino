namespace GPTino.AgentHost.Codex;

internal static class DynamicToolSpecs
{
    private static readonly string PayloadGuide = Hosting.InstructionAssets.LoadOrFallback(
        "payload-guide.md",
        DefaultPayloadGuide);

    // internal (not private) so a parity test can assert this compiled fallback stays byte-identical
    // to assets/instructions/payload-guide.md — the two are edited together and must never drift.
    internal const string DefaultPayloadGuide = """

        PAYLOADS — first call artifact_write with one JSON object per operation (exactly {"bridgeOperation":"...","arguments":{...}}), then set payloadArtifact to that session-relative path. Property and enum names are camelCase.
        bridgeOperation mapping:
        - moveComponent/setLayout -> canvas.move {operationId,pivots:{guid:{x,y}},expectedFingerprints:{guid:sha256}}
        - setValue -> canvas.setNumberSlider {operationId,objectId,expectedFingerprint,value,minimum,maximum,decimalPlaces} (Number Slider only)
        - connectWire/disconnectWire -> canvas.setWire {operationId,wire:{sourceObjectId,sourceParameterId,targetObjectId,targetParameterId},action:connect|disconnect,rejectCycles:true}
        - createComponent -> canvas.create {operationId,objectId,componentTypeId,pivot:"gptino:auto",autoUpstream:[objectId,...],nickName} — ALWAYS use pivot:"gptino:auto" and list in autoUpstream the objectIds of the components/sliders that will feed this one; the server computes a clean, non-overlapping downstream position (sources left, results right). autoUpstream is optional and valid ONLY with the sentinel. Hand-pick pivot:{x,y} ONLY when the user asked for a specific location (an explicit point must NOT carry autoUpstream).
        - referenceRhinoObjects -> canvas.referenceRhinoObjects {operationId,objectId,rhinoObjectIds:[guid,...],paramType:curve|brep|mesh|surface|point|geometry,pivot:"gptino:auto",nickName} — creates a typed GH parameter that PERSISTENTLY REFERENCES existing Rhino objects by GUID (a live reference, not a baked copy). This is how "use the curves/geometry I selected in Rhino" becomes an editable definition: reference the selected object ids here, then wire this parameter downstream — never re-author the geometry in a script. writeSet is grasshopperComponent with gptino:absent, exactly like createComponent (it creates a new canvas object at objectId). Pick paramType to match the selection; "geometry" accepts mixed types.
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
        - fixRhinoEndpointPair -> rhino.fixEndpointPair {operationId,anchorObjectId,anchorEnd,moveObjectId,moveEnd,expectedAnchorFingerprint,expectedFingerprint,tolerance} — heals one audited near-miss pair: the anchor is declared as a READ (fingerprint from the audit finding), the move object is the single write; ends are 0=start/1=end; tolerance is the audit's reported value. Verified before the write — a failed strategy changes nothing.
        - purgeTableEntries -> rhino.purgeTableEntries {operationId,entries:[{table:block|dimStyle|linetype|material,id}]} — deletes unused document-table entries; "unused" is re-verified live at execution, so an entry that gained a reference since the audit is refused. Declares no rhinoObject writes.
        - moveObjectsToLayer -> rhino.moveObjectsToLayer {operationId,items:[{objectId,expectedFingerprint}],targetLayerId} — attribute-only batch (geometry untouched); this is ALSO the quarantine vehicle for invalid objects. Every item's objectId needs its own exact rhinoObject writeSet expectation whose fingerprint equals the item's.
        - updateRhinoLayerProperties -> rhino.updateLayer {operationId,layerId,expectedFingerprint,argbColor?,visible?,locked?} — presentation only; rename/re-parent are NOT available (they rewrite descendant paths and break GH name filters). writeSet resource kind is rhinoLayer.
        - deleteRhinoLayer -> rhino.deleteLayer {operationId,layerId,expectedFingerprint} — only an empty leaf layer (no objects incl. hidden and block members, no children, not current); emptiness is re-proved at execution. writeSet resource kind is rhinoLayer.
        - saveRhinoLayerState -> rhino.layerState {operationId,action:save|restore|delete,name} — named layer states; save one BEFORE a layer sweep so the whole sweep is revertible without touching geometry. Declares no object/layer writes.
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
        - APPROVAL: destructive ops (delete/modify/transform/fixEndpointPair) on objects WITHOUT GPTino provenance stamps — the user's own geometry — are refused unless changeSet.approvalGrantId carries the id the user minted by approving on the panel's audit card. GPTino-created objects need no grant. Never invent a grant id and never author approved/sourceDocKey fields — the server injects them.
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
                    "Read an immutable snapshot. Parallel-safe; never acquires the writer lease. The response always includes the exact sessionId and target projectId required by ChangeSet. Use exact scopes before drafting a change.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            scopes = new
                            {
                                type = "array",
                                description = "Optional reads. Omit (empty) or include \"canvas\" for a full-document orientation read (all component resources + the whole canvas). Give ONLY targeted scopes — wireify:<component-guid>, wireify-messages:<component-guid>, rhino:<object-guid> — to inspect just those and skip the heavy full-document dump (use this on large definitions).",
                                items = new { type = "string" }
                            },
                            knownSnapshotId = NullableString("Return unchanged=true when this still identifies the current snapshot.")
                        },
                        additionalProperties = false
                    }),
                Function(
                    "component_catalog",
                    "Look up a component's type GUID in the installed Grasshopper catalog when you do not already know it. Skip this for well-known GUIDs (the script/slider/panel types in the gh-authoring skill); only search for unknown types, or when a create is rejected for an unknown GUID. Parallel-safe and read-only.",
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
                    "rhino_audit",
                    "Deterministic document-hygiene audit of the bound Rhino document. Detection is server " +
                    "code — never eyeball geometry yourself; call this and TRIAGE the findings. Kinds: " +
                    "nearMissEndpoints (open-curve endpoints almost meeting, gap in (tolerance, " +
                    "tolerance*bandFactor]), nearDuplicates (position-coincident curves/points SelDup cannot " +
                    "catch; which copy to keep is always the user's call — design-option stacks are " +
                    "intentional), purgeCandidates (unused block definitions, empty leaf layers, invalid " +
                    "objects — quarantine bad objects, never delete them). Every finding carries object " +
                    "fingerprints for CAS-pinned follow-up fixes; results name the tolerance and units used. " +
                    "Read-only and parallel-safe.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            kind = new
                            {
                                type = "string",
                                @enum = new[] { "nearMissEndpoints", "nearDuplicates", "purgeCandidates" },
                            },
                            tolerance = new { type = "number", description = "Override; default = document absolute tolerance." },
                            bandFactor = new { type = "number", description = "nearMissEndpoints band multiplier; default 10." },
                            limit = new { type = "integer", minimum = 1, maximum = 100, description = "Max findings; default 50." }
                        },
                        required = new[] { "kind" },
                        additionalProperties = false
                    }),
                Function(
                    "rhino_layers",
                    "Read the bound Rhino document's full layer table (path, parent, color, visibility, lock, " +
                    "object count including hidden and block members, whether it has children, per-layer " +
                    "fingerprint) plus the saved named layer states. Read-only. Use it before any layer work: " +
                    "the fingerprints are what layer updates and deletes must pin, and the object/children " +
                    "counts are what prove a layer is safely deletable. Save a named layer state before a " +
                    "layer sweep so the whole sweep can be reverted without touching geometry.",
                    new
                    {
                        type = "object",
                        properties = new { },
                        additionalProperties = false
                    }),
                Function(
                    "data_flow_read",
                    "Read the Rhino<->Grasshopper data-flow ledger for the session's bound GH document: every " +
                    "Rhino object its parameters reference (with per-object existence — a missing object means a " +
                    "broken reference silently emitting empty data) and every GPTino-stamped bake grouped by " +
                    "source document and family. Read-only. Consult it before deleting or purging Rhino objects: " +
                    "never remove a referenced object SILENTLY — name the parameter that breaks and ask first. " +
                    "If the user then explicitly confirms despite the breakage, proceed: the human's informed " +
                    "decision wins over the guard. If a writer session is active this returns writerActive=true " +
                    "immediately instead of queueing.",
                    new
                    {
                        type = "object",
                        properties = new { },
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
                    "arrange_layout",
                    "Tidy the canvas: the server computes a clean left-to-right dataflow layout (inputs on the left, " +
                    "script stages flowing rightward, outputs on the right, stacked top-to-bottom, groups kept together) " +
                    "from the wire topology and real component sizes, then moves the components. You pass only the objectIds " +
                    "you authored (seedComponentIds); the whole connected dataflow cluster they belong to is arranged, and " +
                    "every coordinate is server-owned — you never compute positions or fingerprints. It is a single canvas.move " +
                    "under the hood (single-writer, rollback-safe) and a no-op when the cluster is already tidy. Call this ONCE " +
                    "as the final step after an authoring chain commits.",
                    new
                    {
                        type = "object",
                        properties = new
                        {
                            seedComponentIds = new
                            {
                                type = "array",
                                items = new { type = "string", format = "uuid" },
                                description = "objectIds of components you authored; the connected cluster around them is tidied."
                            },
                            wait = new { type = "boolean", description = "Block briefly for the terminal result; default true." }
                        },
                        required = new[] { "seedComponentIds" },
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
            createdAt = new { type = "string", format = "date-time" },
            approvalGrantId = new
            {
                type = "string",
                description = "Panel-issued user approval id. Required ONLY when a destructive op " +
                    "(delete/modify/transform/fixEndpointPair) targets an object without GPTino " +
                    "provenance stamps — i.e. the user's own geometry. The user mints it by " +
                    "approving on the audit card; never invent one. Never author approved or " +
                    "sourceDocKey fields yourself — the server injects them."
            }
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
                "executePython", "readRuntimeMessages", "createRhinoPrimitive", "transformRhinoObject",
                "referenceRhinoObjects", "fixRhinoEndpointPair", "purgeTableEntries",
                "moveObjectsToLayer", "updateRhinoLayerProperties", "deleteRhinoLayer",
                "saveRhinoLayerState", "ensureRhinoLayer"),
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
                "rhinoObject", "rhinoObjectGeometry", "rhinoObjectAttributes",
                "rhinoLayer", "rhinoLayerTable", "rhinoBlockDefinition", "rhinoDimensionStyle",
                "rhinoMaterial", "rhinoLinetype"),
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
                "objectExists", "objectAbsent",
                "outputCountInRange", "geometryClosed", "areaInRange",
                "dataTreeBranchCountInRange", "volumeInRange", "boundingBoxInRange"),
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
