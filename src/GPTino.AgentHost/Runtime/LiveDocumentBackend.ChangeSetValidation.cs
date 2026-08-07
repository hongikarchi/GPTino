using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Codex;
using GPTino.AgentHost.Data;
using GPTino.AgentHost.Hosting;
using GPTino.AgentHost.Security;
using GPTino.BridgeContract;
using GPTino.Contracts;
using GPTino.CanvasSceneAdapter;
using GPTino.Core;
using GPTino.History;
using GPTino.ScriptAdapter;

namespace GPTino.AgentHost.Runtime;

// ChangeSet validation: structural rules, expectation coverage/alignment, default predicates, wire-id canonicalization, and the canonical JSON request hash.
public sealed partial class LiveDocumentBackend
{
    private void ValidateChangeSet(ChangeSet changeSet, SessionRecord session)
    {
        var projectId = CurrentTarget?.ProjectId ?? _options.ProjectId;
        if (changeSet.ChangeSetId == Guid.Empty)
        {
            throw new InvalidOperationException("ChangeSetId is required.");
        }
        if (changeSet.ProjectId != projectId)
        {
            throw new InvalidOperationException("ChangeSet belongs to another project.");
        }
        if (changeSet.SessionId != session.Id)
        {
            throw new InvalidOperationException("ChangeSet session does not match the calling Codex thread.");
        }
        if (changeSet.Operations is null || changeSet.Operations.Count == 0)
        {
            throw new InvalidOperationException("ChangeSet must contain at least one typed operation.");
        }
        if (changeSet.ReadSet is null || changeSet.WriteSet is null ||
            changeSet.Dependencies is null || changeSet.AcceptancePredicates is null ||
            changeSet.RollbackBeforeImages is null)
        {
            throw new InvalidOperationException("ChangeSet collections cannot be null.");
        }
        if (changeSet.Operations.Any(operation => OperationSemantics.IsWrite(operation.Kind)) &&
            changeSet.AcceptancePredicates.Count == 0)
        {
            throw new InvalidOperationException(
                "A live write ChangeSet requires at least one explicit acceptance predicate.");
        }
        if (changeSet.Operations.Any(operation =>
                string.IsNullOrWhiteSpace(operation.OperationId) ||
                operation.Reads is null || operation.Writes is null))
        {
            throw new InvalidOperationException("Every typed operation requires an id and resource sets.");
        }
        if (changeSet.Operations.Select(operation => operation.OperationId).Distinct().Count() !=
            changeSet.Operations.Count)
        {
            throw new InvalidOperationException("Typed operation ids must be unique within a ChangeSet.");
        }
        if (changeSet.Operations.Any(operation => !string.IsNullOrWhiteSpace(operation.PayloadSha256)))
        {
            throw new InvalidOperationException(
                "payloadSha256 is reserved broker-owned metadata and must be omitted from submissions.");
        }
        foreach (var predicate in changeSet.AcceptancePredicates)
        {
            ValidateAcceptancePredicate(predicate);
        }
    }

    /// <summary>
    /// Attaches the standard acceptance predicate per write kind when the model declared none:
    /// creates/bakes verify the object exists, deletes verify absence, wires verify presence or
    /// absence, and everything else (values, moves, script writes) verifies runtimeErrorAbsent.
    /// Explicit model-declared predicates are left untouched.
    /// </summary>
    private static ChangeSet ApplyDefaultPredicates(ChangeSet changeSet)
    {
        if (changeSet.AcceptancePredicates is not { Count: 0 } ||
            changeSet.Operations is null ||
            !changeSet.Operations.Any(operation => OperationSemantics.IsWrite(operation.Kind)))
        {
            return changeSet;
        }

        var predicates = new List<VerificationPredicate>();
        var runtimeErrorAbsent = false;
        foreach (var operation in changeSet.Operations.Where(item => OperationSemantics.IsWrite(item.Kind)))
        {
            var added = operation.Kind switch
            {
                OperationKind.CreateComponent or
                OperationKind.ReferenceRhinoObjects or
                OperationKind.CreateRhinoPrimitive or
                OperationKind.CreateRhinoObject or
                OperationKind.BakeGeometry =>
                    TryAddDefaultObjectPredicate(predicates, operation, PredicateKind.ObjectExists),
                OperationKind.DeleteComponent or
                OperationKind.DeleteRhinoObject =>
                    TryAddDefaultObjectPredicate(predicates, operation, PredicateKind.ObjectAbsent),
                OperationKind.ConnectWire =>
                    TryAddDefaultWirePredicate(predicates, operation, PredicateKind.WireExists),
                OperationKind.DisconnectWire =>
                    TryAddDefaultWirePredicate(predicates, operation, PredicateKind.WireAbsent),
                _ => false
            };
            runtimeErrorAbsent |= !added;
        }
        if (runtimeErrorAbsent || predicates.Count == 0)
        {
            predicates.Add(new VerificationPredicate(
                "gptino:default runtimeErrorAbsent",
                PredicateKind.RuntimeErrorAbsent,
                null,
                null));
        }
        return changeSet with { AcceptancePredicates = predicates };
    }

    private static bool TryAddDefaultObjectPredicate(
        List<VerificationPredicate> predicates,
        TypedOperation operation,
        PredicateKind kind)
    {
        var resource = operation.Writes.FirstOrDefault(item => item.Kind is
            ResourceKind.GrasshopperComponent or
            ResourceKind.GrasshopperGroup or
            ResourceKind.RhinoObject);
        if (resource is null)
        {
            return false;
        }
        predicates.Add(new VerificationPredicate(
            $"gptino:default {kind.ToString().ToLowerInvariant()} {operation.OperationId}",
            kind,
            resource,
            null));
        return true;
    }

    private static bool TryAddDefaultWirePredicate(
        List<VerificationPredicate> predicates,
        TypedOperation operation,
        PredicateKind kind)
    {
        var resource = operation.Writes.FirstOrDefault(item =>
            item.Kind == ResourceKind.GrasshopperWire);
        if (resource is null)
        {
            return false;
        }
        predicates.Add(new VerificationPredicate(
            $"gptino:default {kind.ToString().ToLowerInvariant()} {operation.OperationId}",
            kind,
            resource,
            null));
        return true;
    }

    private static void ValidateAcceptancePredicate(VerificationPredicate predicate)
    {
        if (string.IsNullOrWhiteSpace(predicate.Name))
        {
            throw new InvalidOperationException("Acceptance predicate names cannot be empty.");
        }
        switch (predicate.Kind)
        {
            case PredicateKind.RuntimeErrorAbsent:
                if (predicate.Resource is not null || predicate.ExpectedValue is not null)
                {
                    throw new InvalidOperationException(
                        "RuntimeErrorAbsent does not accept a resource or expectedValue.");
                }
                return;
            case PredicateKind.FingerprintEquals:
                if (predicate.Resource is null || string.IsNullOrWhiteSpace(predicate.ExpectedValue))
                {
                    throw new InvalidOperationException(
                        "FingerprintEquals requires a resource and expectedValue.");
                }
                return;
            case PredicateKind.WireExists:
            case PredicateKind.WireAbsent:
                if (predicate.Resource?.Kind != ResourceKind.GrasshopperWire ||
                    predicate.ExpectedValue is not null)
                {
                    throw new InvalidOperationException(
                        $"{predicate.Kind} requires a GrasshopperWire resource and no expectedValue.");
                }
                return;
            case PredicateKind.ObjectExists:
            case PredicateKind.ObjectAbsent:
                if (predicate.Resource is null || predicate.ExpectedValue is not null ||
                    predicate.Resource.Kind is not (
                        ResourceKind.GrasshopperComponent or ResourceKind.GrasshopperGroup or
                        ResourceKind.RhinoObject))
                {
                    throw new InvalidOperationException(
                        $"{predicate.Kind} requires a supported object resource and no expectedValue.");
                }
                return;
            case PredicateKind.OutputCountInRange:
                if (predicate.Resource?.Kind != ResourceKind.GrasshopperComponent ||
                    !TryParseOutputCountRange(predicate.ExpectedValue, out _))
                {
                    throw new InvalidOperationException(
                        "OutputCountInRange requires a grasshopperComponent resource and expectedValue " +
                        "\"outputName:min:max\" (max may be \"*\").");
                }
                return;
            case PredicateKind.AreaInRange:
            case PredicateKind.DataTreeBranchCountInRange:
            case PredicateKind.VolumeInRange:
                if (predicate.Resource?.Kind != ResourceKind.GrasshopperComponent ||
                    !TryParseNumericOutputRange(predicate.ExpectedValue, out _, out _, out _))
                {
                    throw new InvalidOperationException(
                        $"{predicate.Kind} requires a grasshopperComponent resource and expectedValue " +
                        "\"outputName:min:max\" (max may be \"*\").");
                }
                return;
            case PredicateKind.BoundingBoxInRange:
                if (predicate.Resource?.Kind != ResourceKind.GrasshopperComponent ||
                    !TryParseBoundingBoxRange(predicate.ExpectedValue, out _, out _, out _, out _))
                {
                    throw new InvalidOperationException(
                        "BoundingBoxInRange requires a grasshopperComponent resource and expectedValue " +
                        "\"outputName:axis:min:max\" (axis = x|y|z|diagonal, max may be \"*\").");
                }
                return;
            case PredicateKind.GeometryClosed:
                if (predicate.Resource?.Kind != ResourceKind.GrasshopperComponent ||
                    string.IsNullOrWhiteSpace(predicate.ExpectedValue))
                {
                    throw new InvalidOperationException(
                        "GeometryClosed requires a grasshopperComponent resource and expectedValue = the output name.");
                }
                return;
            default:
                throw new InvalidOperationException(
                    $"Acceptance predicate kind '{predicate.Kind}' is reserved and unsupported.");
        }
    }

    private static void ValidateExpectationCoverage(
        ChangeSet changeSet,
        IReadOnlyList<PreparedOperation> prepared,
        Guid? grasshopperDocumentId,
        Guid projectId)
    {
        foreach (var expectation in changeSet.ReadSet.Concat(changeSet.WriteSet))
        {
            ValidateResourceAddress(expectation.Resource, grasshopperDocumentId, projectId);
            if (string.IsNullOrWhiteSpace(expectation.ExpectedFingerprint))
            {
                throw new InvalidOperationException("Resource expectations require a fingerprint.");
            }
        }
        if (changeSet.ReadSet.Any(expectation => expectation.ExpectsAbsence))
        {
            throw new InvalidOperationException(
                $"'{ResourceExpectation.AbsentFingerprint}' is not valid in readSet.");
        }
        RejectAmbiguousExpectations(changeSet.ReadSet, "readSet");
        RejectAmbiguousExpectations(changeSet.WriteSet, "writeSet");

        foreach (var preparedOperation in prepared)
        {
            var operation = preparedOperation.Operation;
            foreach (var resource in operation.Reads)
            {
                ValidateResourceAddress(resource, grasshopperDocumentId, projectId);
                var expectation = FindExpectation(changeSet.ReadSet, resource);
                if (expectation is null || expectation.ExpectsAbsence)
                {
                    throw new InvalidOperationException(
                        $"Operation '{operation.OperationId}' read '{resource.Kind}:{resource.Id}:{resource.Field}' " +
                        "requires an actual fingerprint in readSet.");
                }
            }
            foreach (var resource in operation.Writes)
            {
                ValidateResourceAddress(resource, grasshopperDocumentId, projectId);
                if (FindExpectation(changeSet.WriteSet, resource) is null)
                {
                    throw new InvalidOperationException(
                        $"Operation '{operation.OperationId}' write '{resource.Kind}:{resource.Id}:{resource.Field}' " +
                        "requires an optimistic expectation in writeSet.");
                }
            }
            if (OperationSemantics.IsWrite(operation.Kind) && operation.Writes.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Write operation '{operation.OperationId}' must declare at least one write resource.");
            }
            if (!OperationSemantics.IsWrite(operation.Kind) && operation.Writes.Count != 0)
            {
                throw new InvalidOperationException(
                    $"Read operation '{operation.OperationId}' cannot declare write resources.");
            }
            ValidatePayloadExpectationAlignment(changeSet, preparedOperation);
        }

        RejectUnusedExpectations(changeSet, prepared);
        RejectOverlappingOperationWrites(prepared);
        RejectInterleavedPythonFingerprintSequences(prepared);

        foreach (var predicate in changeSet.AcceptancePredicates.Where(item => item.Resource is not null))
        {
            ValidateResourceAddress(predicate.Resource!, grasshopperDocumentId, projectId);
            if (!prepared.SelectMany(item => item.Operation.Reads.Concat(item.Operation.Writes))
                    .Any(resource => ExactDomainOverlaps(resource, predicate.Resource!)))
            {
                throw new InvalidOperationException(
                    $"Acceptance predicate '{predicate.Name}' targets a resource not declared by any operation.");
            }
        }
        foreach (var beforeImage in changeSet.RollbackBeforeImages)
        {
            ValidateResourceAddress(beforeImage.Resource, grasshopperDocumentId, projectId);
            if (string.IsNullOrWhiteSpace(beforeImage.ArtifactReference) ||
                string.IsNullOrWhiteSpace(beforeImage.Fingerprint) ||
                !prepared.SelectMany(item => item.Operation.Writes)
                    .Any(resource => ExactDomainOverlaps(resource, beforeImage.Resource)))
            {
                throw new InvalidOperationException(
                    "Rollback before images require a declared write resource, artifact reference, and fingerprint.");
            }
        }

        foreach (var expectation in changeSet.WriteSet.Where(item => item.ExpectsAbsence))
        {
            var creator = prepared.FirstOrDefault(item =>
                TryGetCreatedResource(item, out var created) &&
                ExactDomainOverlaps(created, expectation.Resource));
            if (creator is null)
            {
                throw new InvalidOperationException(
                    $"'{ResourceExpectation.AbsentFingerprint}' is allowed only for an exact createComponent, " +
                    "createRhinoPrimitive, createRhinoObject, bakeGeometry, connectWire, or new setGroup target.");
            }
        }

        foreach (var preparedOperation in prepared.Where(item => item.Operation.Kind is
                     OperationKind.CreateComponent or OperationKind.ReferenceRhinoObjects or
                     OperationKind.CreateRhinoPrimitive or
                     OperationKind.CreateRhinoObject or OperationKind.BakeGeometry or
                     OperationKind.ConnectWire))
        {
            if (!TryGetCreatedResource(preparedOperation, out var created) ||
                !changeSet.WriteSet.Any(expectation =>
                    expectation.ExpectsAbsence &&
                    ExactDomainOverlaps(expectation.Resource, created)))
            {
                throw new InvalidOperationException(
                    $"Create operation '{preparedOperation.Operation.OperationId}' requires writeSet " +
                    $"expectation '{ResourceExpectation.AbsentFingerprint}' for its exact target.");
            }
        }
        RejectConflictingRhinoLogicalEntityClaims(prepared);
    }

    private static void RejectConflictingRhinoLogicalEntityClaims(
        IReadOnlyList<PreparedOperation> prepared)
    {
        var claims = new Dictionary<string, (string OperationId, Guid ObjectId, string Role)>(
            StringComparer.Ordinal);
        foreach (var item in prepared.Where(item => item.Operation.Kind is
                     OperationKind.CreateRhinoPrimitive or OperationKind.CreateRhinoObject or
                     OperationKind.BakeGeometry or OperationKind.ModifyRhinoObject or
                     OperationKind.UpdateRhinoAttributes))
        {
            var operation = item.Operation;
            var objectId = RequireArgumentGuid(item.Arguments, "objectId", operation.OperationId);
            var logicalEntityId = RequireArgumentString(
                item.Arguments,
                "logicalEntityId",
                operation.OperationId);
            var role = operation.Kind is
                OperationKind.CreateRhinoPrimitive or OperationKind.CreateRhinoObject or
                OperationKind.BakeGeometry
                ? "create"
                : "existing";
            if (claims.TryGetValue(logicalEntityId, out var prior) && prior.ObjectId != objectId)
            {
                throw new InvalidOperationException(
                    $"Rhino logical entity '{logicalEntityId}' is claimed by both " +
                    $"'{prior.OperationId}' ({prior.Role}, {prior.ObjectId:D}) and " +
                    $"'{operation.OperationId}' ({role}, {objectId:D}) in one ChangeSet.");
            }
            claims[logicalEntityId] = (operation.OperationId, objectId, role);
        }
    }

    private static void ValidatePayloadExpectationAlignment(
        ChangeSet changeSet,
        PreparedOperation prepared)
    {
        var operation = prepared.Operation;
        var arguments = prepared.Arguments;
        switch (prepared.BridgeOperation)
        {
            case "canvas.setNumberSlider":
                RequirePayloadFingerprint(
                    changeSet,
                    operation,
                    TargetResource(operation, arguments, ResourceKind.GrasshopperComponentValue),
                    RequireArgumentString(arguments, "expectedFingerprint", operation.OperationId));
                return;

            case "canvas.move":
                foreach (var item in arguments.GetProperty("expectedFingerprints").EnumerateObject())
                {
                    if (!Guid.TryParse(item.Name, out var componentId) ||
                        item.Value.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(item.Value.GetString()))
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' has an invalid component fingerprint entry.");
                    }
                    RequirePayloadFingerprint(
                        changeSet,
                        operation,
                        new ResourceAddress(
                            ResourceKind.GrasshopperComponentLayout,
                            componentId.ToString("D")),
                        item.Value.GetString()!);
                }
                return;

            case "canvas.delete":
                RequirePayloadFingerprint(
                    changeSet,
                    operation,
                    TargetResource(operation, arguments, ResourceKind.GrasshopperComponent),
                    RequireArgumentString(arguments, "expectedFingerprint", operation.OperationId));
                return;

            case "rhino.transform":
            case "rhino.delete":
                RequirePayloadFingerprint(
                    changeSet,
                    operation,
                    TargetResource(operation, arguments, ResourceKind.RhinoObject),
                    RequireArgumentString(arguments, "expectedFingerprint", operation.OperationId));
                return;

            case "rhino.moveObjectsToLayer":
                // Every moved object carries its own fingerprint, and each must match that
                // object's writeSet expectation — a batch does not get to be vaguer than N
                // single-object writes.
                foreach (var item in arguments.GetProperty("items").EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object ||
                        !item.TryGetProperty("objectId", out var itemId) ||
                        !Guid.TryParse(itemId.GetString(), out var movedId) ||
                        !item.TryGetProperty("expectedFingerprint", out var itemFingerprint) ||
                        itemFingerprint.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' has an invalid layer-move item.");
                    }
                    RequirePayloadFingerprint(
                        changeSet,
                        operation,
                        new ResourceAddress(ResourceKind.RhinoObject, movedId.ToString("D")),
                        itemFingerprint.GetString()!);
                }
                return;

            case "rhino.updateLayer":
            case "rhino.deleteLayer":
                RequirePayloadFingerprint(
                    changeSet,
                    operation,
                    new ResourceAddress(
                        ResourceKind.RhinoLayer,
                        RequireArgumentGuid(arguments, "layerId", operation.OperationId).ToString("D")),
                    RequireArgumentString(arguments, "expectedFingerprint", operation.OperationId));
                return;

            case "rhino.fixEndpointPair":
                // The write expectation must pin the MOVE object at its audited fingerprint; the
                // adapter re-verifies both fingerprints at execution.
                RequirePayloadFingerprint(
                    changeSet,
                    operation,
                    new ResourceAddress(
                        ResourceKind.RhinoObject,
                        RequireArgumentGuid(arguments, "moveObjectId", operation.OperationId).ToString("D")),
                    RequireArgumentString(arguments, "expectedFingerprint", operation.OperationId));
                // The ANCHOR must have a declared readSet expectation, and a concrete declared
                // fingerprint must match the payload's — the same submit-time teaching the move
                // side gets, instead of a late execution failure. A gptino:auto declaration is
                // allowed (the server resolves it; the adapter still verifies the concrete value).
                var anchorResource = new ResourceAddress(
                    ResourceKind.RhinoObject,
                    RequireArgumentGuid(arguments, "anchorObjectId", operation.OperationId).ToString("D"));
                var anchorExpectation = FindExpectation(changeSet.ReadSet, anchorResource)
                    ?? throw new InvalidOperationException(
                        $"Operation '{operation.OperationId}' requires a readSet expectation for its " +
                        "endpoint-fix anchor (the audited anchor fingerprint).");
                var anchorPayload = RequireArgumentString(
                    arguments, "expectedAnchorFingerprint", operation.OperationId);
                // (fall through to the shared anchor check below)
                if (!string.Equals(
                        anchorExpectation.ExpectedFingerprint,
                        ResourceExpectation.AutoFingerprint,
                        StringComparison.Ordinal) &&
                    !string.Equals(anchorExpectation.ExpectedFingerprint, anchorPayload, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Operation '{operation.OperationId}' anchor fingerprint does not match its " +
                        "declared readSet expectation; use the audited fingerprint in both.");
                }
                return;

            case "rhino.upsert":
                var resource = TargetResource(operation, arguments, ResourceKind.RhinoObject);
                var expectation = FindExpectation(changeSet.WriteSet, resource)
                    ?? throw new InvalidOperationException(
                        $"Operation '{operation.OperationId}' requires an exact Rhino write expectation.");
                var expected = arguments.GetProperty("expectedFingerprint");
                if (operation.Kind is OperationKind.CreateRhinoObject or OperationKind.BakeGeometry)
                {
                    if (!expectation.ExpectsAbsence || expected.ValueKind != JsonValueKind.Null)
                    {
                        throw new InvalidOperationException(
                            $"Exact Rhino create '{operation.OperationId}' requires writeSet " +
                            $"'{ResourceExpectation.AbsentFingerprint}' and a null payload expectedFingerprint.");
                    }
                    return;
                }
                RequirePayloadFingerprint(
                    changeSet,
                    operation,
                    resource,
                    RequireArgumentString(arguments, "expectedFingerprint", operation.OperationId));
                return;
        }
    }

    private static ResourceAddress TargetResource(
        TypedOperation operation,
        JsonElement arguments,
        ResourceKind kind) =>
        new(
            kind,
            RequireArgumentGuid(arguments, "objectId", operation.OperationId).ToString("D"));

    private static void RequirePayloadFingerprint(
        ChangeSet changeSet,
        TypedOperation operation,
        ResourceAddress resource,
        string payloadFingerprint)
    {
        if (string.Equals(payloadFingerprint, ResourceExpectation.AutoFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' cannot use gptino:auto: value and geometry writes " +
                "(setNumberSlider, move, delete, rhino transform/upsert) carry the fingerprint in the payload " +
                "and need the concrete value from the previous commit. Auto is for Python source/schema/value " +
                "and wire writeSet expectations only.");
        }
        var expectation = FindExpectation(changeSet.WriteSet, resource);
        if (expectation is null || expectation.ExpectsAbsence ||
            !string.Equals(
                expectation.ExpectedFingerprint,
                payloadFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' payload fingerprint does not match its exact writeSet expectation.");
        }
    }

    private static void RejectUnusedExpectations(
        ChangeSet changeSet,
        IReadOnlyList<PreparedOperation> prepared)
    {
        var reads = prepared.SelectMany(item => item.Operation.Reads).ToArray();
        var writes = prepared.SelectMany(item => item.Operation.Writes).ToArray();
        if (changeSet.ReadSet.Any(expectation =>
                !reads.Any(resource => ExactDomainOverlaps(resource, expectation.Resource))))
        {
            throw new InvalidOperationException(
                "readSet contains a resource not declared by any operation read.");
        }
        if (changeSet.WriteSet.Any(expectation =>
                !writes.Any(resource => ExactDomainOverlaps(resource, expectation.Resource))))
        {
            throw new InvalidOperationException(
                "writeSet contains a resource not declared by any operation write.");
        }
    }

    private static void RejectOverlappingOperationWrites(IReadOnlyList<PreparedOperation> prepared)
    {
        for (var left = 0; left < prepared.Count; left++)
        {
            for (var right = left + 1; right < prepared.Count; right++)
            {
                var overlap = prepared[left].Operation.Writes.FirstOrDefault(leftResource =>
                    prepared[right].Operation.Writes.Any(rightResource =>
                        ConflictDetector.Overlaps(leftResource, rightResource)));
                if (overlap is not null)
                {
                    throw new InvalidOperationException(
                        $"Operations '{prepared[left].Operation.OperationId}' and " +
                        $"'{prepared[right].Operation.OperationId}' both write " +
                        $"'{overlap.Kind}:{overlap.Id}'. Combine them into one typed operation.");
                }
            }
        }
    }

    private static void RejectInterleavedPythonFingerprintSequences(
        IReadOnlyList<PreparedOperation> prepared)
    {
        var indexedWrites = prepared
            .Select((item, index) => new { Item = item, Index = index, Resource = PythonStateWrite(item.Operation) })
            .Where(item => item.Resource is not null)
            .ToArray();
        if (indexedWrites.Length == 0)
        {
            return;
        }
        var componentIds = indexedWrites
            .Select(item => item.Resource!.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (componentIds.Length != 1 || prepared.Any(item =>
                OperationSemantics.IsWrite(item.Operation.Kind) &&
                PythonStateWrite(item.Operation) is null))
        {
            throw new InvalidOperationException(
                "A ChangeSet with Python source/I/O/value writes may mutate exactly one Python component and cannot contain other writes.");
        }
        foreach (var group in indexedWrites.GroupBy(item => item.Resource!.Id, StringComparer.Ordinal))
        {
            if (group.Count() < 2)
            {
                continue;
            }
            var first = group.Min(item => item.Index);
            var last = group.Max(item => item.Index);
            if (prepared.Skip(first).Take(last - first + 1).Any(item =>
                    PythonStateWrite(item.Operation)?.Id != group.Key))
            {
                throw new InvalidOperationException(
                    $"Script mutations for component '{group.Key}' must be contiguous within a ChangeSet.");
            }
        }
    }

    /// <summary>
    /// Script-content operations are the ones whose Error diagnostics describe the SCRIPT (compile
    /// or runtime failures) rather than the operation: the write itself landed deterministically.
    /// Keyed on OperationKind — the typed contract surface — not on bridge op names or plugin
    /// diagnostic codes. Covers the whole python-state family: the script adapter emits Error
    /// diagnostics only from component runtime messages (script content), while operation-level
    /// failures arrive as thrown bridge errors and still abort. Live round R3 confirmed compile
    /// errors surface on setComponentIo responses (the schema write triggers the solve).
    /// </summary>
    private static bool IsScriptContentOperation(OperationKind kind) =>
        kind is OperationKind.UpdatePythonSource or
            OperationKind.ExecutePython or
            OperationKind.SetComponentIo or
            OperationKind.ConvertSocket;

    private static ResourceAddress? PythonStateWrite(TypedOperation operation)
    {
        if (operation.Kind is not (
                OperationKind.UpdatePythonSource or
                OperationKind.SetComponentIo or
                OperationKind.ConvertSocket or
                OperationKind.ExecutePython))
        {
            return null;
        }
        return operation.Writes.SingleOrDefault(resource => resource.Kind is
            ResourceKind.GrasshopperComponentSource or
            ResourceKind.GrasshopperComponentIo or
            ResourceKind.GrasshopperComponentValue);
    }

    private static void ValidateResourceAddress(
        ResourceAddress resource,
        Guid? grasshopperDocumentId,
        Guid projectId)
    {
        if (string.IsNullOrWhiteSpace(resource.Id) || resource.Field != "*")
        {
            throw new InvalidOperationException(
                "Resource addresses require a canonical id and the whole-domain '*' field.");
        }

        if (resource.Kind == ResourceKind.Document)
        {
            // The whole-document resource is scoped to the bound Grasshopper document (its runtime
            // DocumentID), not the ProjectId, which is Rhino-scoped and shared by sibling documents.
            if (grasshopperDocumentId is not { } documentId)
            {
                throw new InvalidOperationException(
                    "No Grasshopper document is open, so there is no document resource to address. " +
                    "Open a Grasshopper definition to work on the canvas.");
            }

            if (!string.Equals(resource.Id, documentId.ToString("D"), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Document resource ids must be the bound Grasshopper document UUID in D format.");
            }
            return;
        }

        if (resource.Kind == ResourceKind.GrasshopperWire)
        {
            if (!TryCanonicalizeWireId(resource.Id, out var canonical) ||
                !string.Equals(resource.Id, canonical, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Grasshopper wire resource ids must use canonical lowercase N-format endpoint UUIDs.");
            }
            return;
        }

        if (resource.Kind == ResourceKind.RhinoLayerTable)
        {
            // The Rhino layer table is addressed by the RHINO-scoped ProjectId. It used to borrow
            // the bound Grasshopper document id, which was wrong twice over: a Rhino layer table has
            // nothing to do with Grasshopper, and sibling .gh documents open on one Rhino file would
            // each name the same physical table by a different id — so two layer writes could not
            // see each other's CAS domain. Rhino-only work also has no .gh to borrow an id from.
            if (!string.Equals(resource.Id, projectId.ToString("D"), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "rhinoLayerTable resource ids must be the project id (the Rhino document's " +
                    "identity) in D format.");
            }
            return;
        }

        if (!Guid.TryParse(resource.Id, out var id) || id == Guid.Empty ||
            !string.Equals(resource.Id, id.ToString("D"), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Kind}' ids must be canonical lowercase D-format UUIDs.");
        }
    }

    private static bool TryCanonicalizeWireId(string value, out string canonical)
    {
        canonical = string.Empty;
        var endpoints = value.Split('>', StringSplitOptions.None);
        if (endpoints.Length != 2)
        {
            return false;
        }
        var source = endpoints[0].Split('/', StringSplitOptions.None);
        var target = endpoints[1].Split('/', StringSplitOptions.None);
        if (source.Length != 2 || target.Length != 2 ||
            !Guid.TryParseExact(source[0], "N", out var sourceObject) ||
            !Guid.TryParseExact(source[1], "N", out var sourceParameter) ||
            !Guid.TryParseExact(target[0], "N", out var targetObject) ||
            !Guid.TryParseExact(target[1], "N", out var targetParameter) ||
            sourceObject == Guid.Empty || sourceParameter == Guid.Empty ||
            targetObject == Guid.Empty || targetParameter == Guid.Empty)
        {
            return false;
        }
        canonical = FormattableString.Invariant(
            $"{sourceObject:N}/{sourceParameter:N}>{targetObject:N}/{targetParameter:N}");
        return true;
    }

    private static void RejectAmbiguousExpectations(
        IReadOnlyList<ResourceExpectation> expectations,
        string collectionName)
    {
        for (var left = 0; left < expectations.Count; left++)
        {
            for (var right = left + 1; right < expectations.Count; right++)
            {
                if (ConflictDetector.Overlaps(
                        expectations[left].Resource,
                        expectations[right].Resource))
                {
                    throw new InvalidOperationException(
                        $"{collectionName} contains overlapping expectations for " +
                        $"'{expectations[left].Resource.Kind}:{expectations[left].Resource.Id}'.");
                }
            }
        }
    }

    private static ResourceExpectation? FindExpectation(
        IReadOnlyList<ResourceExpectation> expectations,
        ResourceAddress resource) =>
        expectations.SingleOrDefault(item => ExactDomainOverlaps(item.Resource, resource));

    private static bool ExactDomainOverlaps(ResourceAddress left, ResourceAddress right) =>
        left.Kind == right.Kind &&
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        (left.Field == "*" || right.Field == "*" ||
         string.Equals(left.Field, right.Field, StringComparison.Ordinal));

    private static bool TryGetCreatedResource(
        PreparedOperation prepared,
        out ResourceAddress resource)
    {
        var arguments = prepared.Arguments;
        switch (prepared.Operation.Kind)
        {
            case OperationKind.CreateComponent:
            case OperationKind.ReferenceRhinoObjects:
                resource = new ResourceAddress(
                    ResourceKind.GrasshopperComponent,
                    RequireArgumentGuid(arguments, "objectId", prepared.Operation.OperationId).ToString("D"));
                return true;
            case OperationKind.CreateRhinoPrimitive:
            case OperationKind.CreateRhinoObject:
            case OperationKind.BakeGeometry:
                resource = new ResourceAddress(
                    ResourceKind.RhinoObject,
                    RequireArgumentGuid(arguments, "objectId", prepared.Operation.OperationId).ToString("D"));
                return true;
            case OperationKind.EnsureRhinoLayer:
                // ensureLayer is create-or-update by path: a brand-new layer declares its intended
                // id with the absent sentinel, an existing one declares its concrete fingerprint.
                resource = new ResourceAddress(
                    ResourceKind.RhinoLayer,
                    RequireArgumentGuid(arguments, "layerId", prepared.Operation.OperationId).ToString("D"));
                return true;
            case OperationKind.ConnectWire:
                var wire = arguments.GetProperty("wire");
                if (wire.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException(
                        $"Operation '{prepared.Operation.OperationId}' wire must be an object.");
                }
                var sourceObject = RequireArgumentGuid(wire, "sourceObjectId", prepared.Operation.OperationId);
                var sourceParameter = RequireArgumentGuid(wire, "sourceParameterId", prepared.Operation.OperationId);
                var targetObject = RequireArgumentGuid(wire, "targetObjectId", prepared.Operation.OperationId);
                var targetParameter = RequireArgumentGuid(wire, "targetParameterId", prepared.Operation.OperationId);
                resource = new ResourceAddress(
                    ResourceKind.GrasshopperWire,
                    FormattableString.Invariant(
                        $"{sourceObject:N}/{sourceParameter:N}>{targetObject:N}/{targetParameter:N}"));
                return true;
            case OperationKind.SetGroup:
                resource = new ResourceAddress(
                    ResourceKind.GrasshopperGroup,
                    RequireArgumentGuid(arguments, "groupId", prepared.Operation.OperationId).ToString("D"));
                return true;
            default:
                resource = null!;
                return false;
        }
    }

    private static string RequiredString(JsonElement arguments, string property)
    {
        if (!arguments.TryGetProperty(property, out var element) ||
            string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new InvalidOperationException($"'{property}' is required.");
        }
        return element.GetString()!.Trim();
    }

    private static string ComputeAcceptedRequestHash(
        ChangeSet changeSet,
        string expectedSnapshotId,
        string summary,
        IReadOnlyList<PreparedOperation> prepared)
    {
        var payloads = prepared.Select(item =>
        {
            using var document = JsonDocument.Parse(item.FrozenPayload);
            return new
            {
                operationId = item.Operation.OperationId,
                sourceArtifact = item.Operation.PayloadArtifact,
                payload = document.RootElement.Clone()
            };
        }).ToArray();
        var acceptedRequest = JsonSerializer.SerializeToElement(
            new
            {
                expectedSnapshotId,
                summary,
                changeSet,
                payloads
            },
            BridgeProtocol.JsonOptions);
        return Sha256(CanonicalizeJson(acceptedRequest));
    }

    private static void RequireMatchingRequestHash(
        string storedHash,
        string requestHash,
        string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(storedHash) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(storedHash),
                Encoding.ASCII.GetBytes(requestHash)))
        {
            throw new InvalidOperationException(
                $"Idempotency key '{idempotencyKey}' is already bound to a different accepted request. " +
                "The original job is still tracked: call job_status with the jobId from the first " +
                "change_submit response instead of resubmitting with regenerated changeSetId/createdAt.");
        }
    }

    private static byte[] CanonicalizeJson(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonicalJson(writer, element);
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }
                writer.WriteEndArray();
                return;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                return;
            case JsonValueKind.Number:
                writer.WriteRawValue(
                    CanonicalizeNumber(element.GetRawText()),
                    skipInputValidation: false);
                return;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                return;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                return;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                return;
            default:
                throw new InvalidOperationException("Undefined JSON values cannot be canonicalized.");
        }
    }

    private static string CanonicalizeNumber(string raw)
    {
        if (raw.Length > MaximumCanonicalNumberCharacters)
        {
            throw new InvalidOperationException(
                $"JSON numbers cannot exceed {MaximumCanonicalNumberCharacters} characters.");
        }
        var negative = raw[0] == '-';
        var unsigned = negative ? raw[1..] : raw;
        var exponentIndex = unsigned.IndexOf('e');
        if (exponentIndex < 0)
        {
            exponentIndex = unsigned.IndexOf('E');
        }
        var mantissa = exponentIndex < 0 ? unsigned : unsigned[..exponentIndex];
        var decimalIndex = mantissa.IndexOf('.');
        var fractionalDigits = decimalIndex < 0 ? 0 : mantissa.Length - decimalIndex - 1;
        var digits = decimalIndex < 0
            ? mantissa
            : string.Concat(mantissa.AsSpan(0, decimalIndex), mantissa.AsSpan(decimalIndex + 1));
        digits = digits.TrimStart('0');
        if (digits.Length == 0)
        {
            return negative ? "-0" : "0";
        }

        var explicitExponent = exponentIndex < 0
            ? BigInteger.Zero
            : BigInteger.Parse(unsigned[(exponentIndex + 1)..], CultureInfo.InvariantCulture);
        var exponent = explicitExponent - fractionalDigits;
        var trailingZeros = digits.Length - digits.TrimEnd('0').Length;
        if (trailingZeros > 0)
        {
            digits = digits[..^trailingZeros];
            exponent += trailingZeros;
        }
        var scientificExponent = exponent + digits.Length - 1;
        var coefficient = digits.Length == 1
            ? digits
            : $"{digits[0]}.{digits[1..]}";
        var sign = negative ? "-" : string.Empty;
        return scientificExponent.IsZero
            ? $"{sign}{coefficient}"
            : $"{sign}{coefficient}e{scientificExponent.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string BuildSnapshotId(StateSnapshot state, string documentFingerprint) =>
        $"s{state.Revision}-{Sha256($"{state.Target.Identity}\n{documentFingerprint}")[..24]}";

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

}
