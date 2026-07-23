
PAYLOADS — first call artifact_write with one JSON object per operation (exactly {"bridgeOperation":"...","arguments":{...}}), then set payloadArtifact to that session-relative path. Property and enum names are camelCase.
bridgeOperation mapping:
- moveComponent/setLayout -> canvas.move {operationId,pivots:{guid:{x,y}},expectedFingerprints:{guid:sha256}}
- setValue -> canvas.setNumberSlider {operationId,objectId,expectedFingerprint,value,minimum,maximum,decimalPlaces} (Number Slider only)
- connectWire/disconnectWire -> canvas.setWire {operationId,wire:{sourceObjectId,sourceParameterId,targetObjectId,targetParameterId},action:connect|disconnect,rejectCycles:true}
- createComponent -> canvas.create {operationId,objectId,componentTypeId,pivot:{x,y},nickName}
- deleteComponent -> canvas.delete {operationId,objectId,expectedFingerprint}
- setGroup -> canvas.setGroup {operationId,groupId,name,objectIds,argbColor}
- updatePythonSource -> python.setSource {operationId,componentId,expectedSourceSha256,source,runtime:cpython3|ironPython2,expireSolution}
- setComponentIo -> python.setSchema {operationId,componentId,inputs,outputs,preserveIncidentWires}. Appends sockets only (removal unsupported): list every existing socket in order, then appended ones. Socket UUIDs are server-managed and reconciled by position — you control name, access, and typeHint only. Scalars fed by sliders stay generic (coerce in-script); any socket carrying GEOMETRY between components needs the geometry type hint (point3d, vector3d, line, curve, plane, mesh, brep, surface, geometry, ...) on BOTH ends or the receiver gets an untyped/Guid value.
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
- Python source/I/O/value writes share whole-component state: one ChangeSet writes exactly one Python component, contiguously, with no other writes mixed in.
- Canvas points are exactly x/y; Rhino points/vectors exactly x/y/z. Rhino geometryJson must be native RhinoCommon JSON matching geometryType; attributesJson is native ObjectAttributes JSON or "" for defaults. Distinct Rhino object IDs in one ChangeSet use distinct case-sensitive logicalEntityId values.
BOOKKEEPING (server-owned):
- Set expectedSnapshotId='gptino:auto', baseSnapshotRevision=-1, and existing-resource writeSet/readSet expectedFingerprint='gptino:auto' — the server fills them from this session's own last write; a genuine foreign change still Blocks.
- Creates (createComponent, createRhinoPrimitive, createRhinoObject, bakeGeometry, connectWire, a new setGroup) use writeSet expectedFingerprint='gptino:absent'.
- Value/geometry payload+writeSet fingerprints (setNumberSlider, move, delete, rhino transform/upsert) must be the concrete value, not gptino:auto; payload fingerprints for existing resources must exactly match writeSet. For createRhinoObject/bakeGeometry only, payload arguments.expectedFingerprint is null.
- acceptancePredicates may be [] — the server attaches the standard set (creates/bakes objectExists, deletes objectAbsent, wires wireExists/wireAbsent, everything else runtimeErrorAbsent).
