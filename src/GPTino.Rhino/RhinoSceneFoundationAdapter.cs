// SPDX-License-Identifier: Apache-2.0
// Behavioral reimplementation informed by Cordyceps; see THIRD_PARTY_NOTICES.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GPTino.BridgeContract;
using GPTino.CordycepsAdapter;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Runtime;

namespace GPTino.Rhino;

/// <summary>
/// Undo-aware Rhino scene adapter using RhinoCommon's native CommonObject JSON format.
/// It never falls back to ActiveDoc and preserves object IDs on replacement.
/// </summary>
public sealed class RhinoSceneFoundationAdapter : DocumentBoundRhinoSceneAdapter<global::Rhino.RhinoDoc>
{
    private const string LogicalEntityKey = "GPTino.LogicalEntityId";
    // Provenance stamp: the durable GH docKey whose job produced this object. Server-injected
    // (never model-supplied); legacy objects without it stay honestly unattributed.
    private const string SourceDocKeyKey = "GPTino.SourceDocKey";
    // Stamped by the bake_manager skill (family identity for replace/append re-bakes).
    private const string BakeFamilyKey = "gptino_bake_family";
    /// <summary>Bridge failure code for the human-wins refusal; see RequireProvenanceOrApproval.</summary>
    public const string ApprovalRequiredCode = "approval_required";

    public RhinoSceneFoundationAdapter(ExplicitRhinoDocumentResolver resolver)
        : base(resolver)
    {
    }

    protected override Task<RhinoSceneListResult> ListObjectsCoreAsync(
        global::Rhino.RhinoDoc document,
        RhinoListObjectsRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateListRequest(request);

        var matches = new List<RhinoSceneObjectSummary>(request.Limit + 1);
        foreach (var rhinoObject in document.Objects
                     .OrderBy(item => item.Id.ToString("D"), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.ObjectId.HasValue && rhinoObject.Id != request.ObjectId.Value)
            {
                continue;
            }

            var attributes = rhinoObject.Attributes;
            var layer = attributes.LayerIndex >= 0 && attributes.LayerIndex < document.Layers.Count
                ? document.Layers[attributes.LayerIndex]
                : null;
            var logicalEntityId = attributes.GetUserString(LogicalEntityKey) ?? string.Empty;
            var name = attributes.Name ?? string.Empty;
            var geometryType = rhinoObject.Geometry.ObjectType.ToString();
            var selected = rhinoObject.IsSelected(checkSubObjects: false) != 0;

            if (request.LayerId.HasValue && layer?.Id != request.LayerId.Value ||
                request.LayerFullPath is not null &&
                !string.Equals(layer?.FullPath, request.LayerFullPath, StringComparison.OrdinalIgnoreCase) ||
                request.Name is not null &&
                !string.Equals(name, request.Name, StringComparison.OrdinalIgnoreCase) ||
                request.NameContains is not null &&
                !name.Contains(request.NameContains, StringComparison.OrdinalIgnoreCase) ||
                request.GeometryType is not null &&
                !string.Equals(geometryType, request.GeometryType, StringComparison.OrdinalIgnoreCase) ||
                request.LogicalEntityId is not null &&
                !string.Equals(logicalEntityId, request.LogicalEntityId, StringComparison.Ordinal) ||
                request.Selected.HasValue && selected != request.Selected.Value)
            {
                continue;
            }

            var state = ToState(rhinoObject);
            matches.Add(new RhinoSceneObjectSummary(
                rhinoObject.Id,
                logicalEntityId,
                name,
                geometryType,
                layer?.Id ?? Guid.Empty,
                layer?.FullPath ?? string.Empty,
                selected,
                ToBounds(rhinoObject.Geometry.GetBoundingBox(accurate: false)),
                state.Fingerprint));
            if (matches.Count > request.Limit)
            {
                break;
            }
        }

        var truncated = matches.Count > request.Limit;
        if (truncated)
        {
            matches.RemoveAt(matches.Count - 1);
        }

        var bounds = UnionBounds(matches.Select(item => item.Bounds));
        var fingerprint = Hash(
            $"{CanonicalQuery(request)}\n{truncated}\n" +
            string.Join("\n", matches.Select(item => $"{item.ObjectId:D}:{item.Fingerprint}")));
        return Task.FromResult(new RhinoSceneListResult(
            request.Limit,
            matches.Count,
            truncated,
            bounds,
            matches,
            fingerprint));
    }

    protected override Task<StampedObjectsResult> ListStampedObjectsCoreAsync(
        global::Rhino.RhinoDoc document,
        CancellationToken cancellationToken)
    {
        // Bake-ledger census: every object carrying a GPTino stamp, grouped by
        // (source docKey, bake family). Deterministic ordering (group key, then object id) so the
        // fingerprint is stable across identical documents. Object id lists are capped per group —
        // the ledger needs counts and samples, not a full dump of a 10k-object bake.
        const int MaxIdsPerGroup = 50;
        var groups = new Dictionary<(string? SourceDocKey, string? Family), List<Guid>>();
        var counts = new Dictionary<(string? SourceDocKey, string? Family), int>();
        var totalStamped = 0;
        // The census counts what EXISTS, not what is visible: the default enumerator skips hidden
        // objects, which would shrink bake counts (and churn the fingerprint) the moment a user
        // hides a baked family while iterating. Block-definition members stay excluded — instance
        // containers are the countable objects.
        var enumeratorSettings = new global::Rhino.DocObjects.ObjectEnumeratorSettings
        {
            ActiveObjects = true,
            HiddenObjects = true,
            LockedObjects = true,
            DeletedObjects = false,
        };
        foreach (var rhinoObject in document.Objects.GetObjectList(enumeratorSettings)
                     .OrderBy(item => item.Id.ToString("D"), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attributes = rhinoObject.Attributes;
            var logicalId = attributes.GetUserString(LogicalEntityKey);
            var family = attributes.GetUserString(BakeFamilyKey);
            if (string.IsNullOrEmpty(logicalId) && string.IsNullOrEmpty(family))
            {
                continue;
            }
            totalStamped++;
            var sourceDocKey = attributes.GetUserString(SourceDocKeyKey);
            var key = (
                string.IsNullOrEmpty(sourceDocKey) ? null : sourceDocKey.ToLowerInvariant(),
                string.IsNullOrEmpty(family) ? null : family);
            counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
            if (!groups.TryGetValue(key, out var ids))
            {
                groups[key] = ids = new List<Guid>();
            }
            if (ids.Count < MaxIdsPerGroup)
            {
                ids.Add(rhinoObject.Id);
            }
        }

        var ordered = groups
            .OrderBy(pair => pair.Key.SourceDocKey ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Family ?? string.Empty, StringComparer.Ordinal)
            .Select(pair => new StampedObjectGroup(
                pair.Key.SourceDocKey,
                pair.Key.Family,
                counts[pair.Key],
                pair.Value))
            .ToArray();
        var fingerprint = Hash(
            "stampedObjects\n" + string.Join(
                "\n",
                ordered.Select(group =>
                    $"{group.SourceDocKey}|{group.BakeFamily}|{group.Count}|{string.Join(",", group.ObjectIds.Select(id => id.ToString("D")))}")));
        return Task.FromResult(new StampedObjectsResult(totalStamped, ordered, fingerprint));
    }

    protected override Task<RhinoAuditResult> AuditCoreAsync(
        global::Rhino.RhinoDoc document,
        RhinoAuditRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var limit = Math.Clamp(request.Limit, 1, 100);
        var docTolerance = document.ModelAbsoluteTolerance;
        // The SAME tolerance value flows into every measure and any later fix predicate — a
        // mm-doc heuristic silently becoming absurd in a meters doc is the audit's failure mode.
        var tolerance = request.Tolerance is > 0 ? request.Tolerance.Value : docTolerance;
        var units = document.ModelUnitSystem.ToString();
        var kind = (request.Kind ?? string.Empty).Trim();
        double? bandUsed = null;
        (List<RhinoAuditFinding> Findings, int Scanned, bool Truncated) outcome;
        switch (kind)
        {
            case "nearMissEndpoints":
            {
                // Clamped: an unbounded band degenerates the RTree search into all-pairs
                // enumeration on the UI thread.
                var bandFactor = request.BandFactor is > 1 ? Math.Min(request.BandFactor.Value, 100.0) : 10.0;
                bandUsed = tolerance * bandFactor;
                outcome = AuditNearMissEndpoints(document, tolerance, bandUsed.Value, limit, cancellationToken);
                break;
            }
            case "nearDuplicates":
                outcome = AuditNearDuplicates(document, tolerance, limit, cancellationToken);
                break;
            case "purgeCandidates":
                outcome = AuditPurgeCandidates(document, limit, cancellationToken);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown audit kind '{request.Kind}'. Use nearMissEndpoints|nearDuplicates|purgeCandidates.");
        }
        var fingerprint = Hash(
            $"audit|{kind}|{tolerance:R}|" +
            string.Join("\n", outcome.Findings.Select(finding => $"{finding.FindingId}|{finding.Measure}")));
        return Task.FromResult(new RhinoAuditResult(
            kind,
            docTolerance,
            units,
            tolerance,
            bandUsed,
            outcome.Scanned,
            outcome.Findings,
            outcome.Truncated,
            fingerprint));
    }

    private static ObjectEnumeratorSettings AuditEnumerator(
        ObjectType? typeFilter = null,
        bool includeDefinitionMembers = false) => new()
    {
        // Audits count what exists, not what is visible — hidden and locked included. Block
        // definition members are opt-in: the layer census needs them (a layer holding only block
        // member geometry is NOT empty), while geometry analyses stay top-level.
        ActiveObjects = true,
        HiddenObjects = true,
        LockedObjects = true,
        DeletedObjects = false,
        IdefObjects = includeDefinitionMembers,
        ObjectTypeFilter = typeFilter ?? ObjectType.AnyObject,
    };

    // Open-curve endpoints that ALMOST meet: gap in (tolerance, band]. Detection is endpoint-to-
    // endpoint via RTree; T-junctions (endpoint near a curve's interior) are a separate future
    // kind. Same-object pairs are skipped — an almost-closed curve is a different defect class.
    private (List<RhinoAuditFinding> Findings, int Scanned, bool Truncated) AuditNearMissEndpoints(
        global::Rhino.RhinoDoc document,
        double tolerance,
        double band,
        int limit,
        CancellationToken cancellationToken)
    {
        const int MaxCurves = 8000;
        var endpoints = new List<(Guid Id, int End, Point3d Point)>();
        var objectsById = new Dictionary<Guid, RhinoObject>();
        var scanned = 0;
        var truncated = false;
        foreach (var rhinoObject in document.Objects.GetObjectList(AuditEnumerator(ObjectType.Curve))
                     .OrderBy(item => item.Id.ToString("D"), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rhinoObject.Geometry is not Curve curve || curve.IsClosed)
            {
                continue;
            }
            if (++scanned > MaxCurves)
            {
                truncated = true;
                break;
            }
            objectsById[rhinoObject.Id] = rhinoObject;
            endpoints.Add((rhinoObject.Id, 0, curve.PointAtStart));
            endpoints.Add((rhinoObject.Id, 1, curve.PointAtEnd));
        }

        var tree = new RTree();
        for (var index = 0; index < endpoints.Count; index++)
        {
            tree.Insert(endpoints[index].Point, index);
        }
        // Dense endpoint clusters (or a wide band) can still explode the hit count; the pair
        // budget keeps the UI-thread cost bounded and reports Truncated instead of freezing.
        const int MaxPairChecks = 20000;
        var pairChecks = 0;
        var pairs = new Dictionary<string, (Guid A, int EndA, Guid B, int EndB, double Gap)>(StringComparer.Ordinal);
        for (var index = 0; index < endpoints.Count && !truncated; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (idA, endA, pointA) = endpoints[index];
            var hits = new List<int>();
            tree.Search(new Sphere(pointA, band), (_, args) => hits.Add(args.Id));
            foreach (var hit in hits)
            {
                if (++pairChecks > MaxPairChecks)
                {
                    truncated = true;
                    break;
                }
                if (hit <= index)
                {
                    continue;
                }
                var (idB, endB, pointB) = endpoints[hit];
                if (idA == idB)
                {
                    continue;
                }
                var gap = pointA.DistanceTo(pointB);
                if (gap <= tolerance || gap > band)
                {
                    continue;
                }
                var key = string.CompareOrdinal(idA.ToString("D"), idB.ToString("D")) <= 0
                    ? $"{idA:D}|{endA}|{idB:D}|{endB}"
                    : $"{idB:D}|{endB}|{idA:D}|{endA}";
                if (!pairs.ContainsKey(key))
                {
                    pairs[key] = (idA, endA, idB, endB, gap);
                }
            }
        }

        var findings = pairs.Values
            .OrderBy(pair => pair.Gap)
            .ThenBy(pair => pair.A)
            .ThenBy(pair => pair.B)
            .Take(limit + 1)
            .Select(pair => new RhinoAuditFinding(
                Hash($"nearMiss|{pair.A:D}|{pair.EndA}|{pair.B:D}|{pair.EndB}")[..16],
                "nearMissEndpoints",
                new[] { pair.A, pair.B },
                new[] { ToState(objectsById[pair.A]).Fingerprint, ToState(objectsById[pair.B]).Fingerprint },
                pair.Gap,
                $"Curve endpoints {pair.Gap:G4} apart (doc tolerance {tolerance:G4}): " +
                $"end {pair.EndA} of {pair.A:D} vs end {pair.EndB} of {pair.B:D}.",
                new[] { "setEndPoint" },
                new[] { pair.EndA, pair.EndB }))
            .ToList();
        if (findings.Count > limit)
        {
            findings.RemoveAt(findings.Count - 1);
            truncated = true;
        }
        return (findings, scanned, truncated);
    }

    // Position-coincident near-duplicates SelDup cannot catch (SelDup requires exact matches).
    // Scope: curves and points — deterministic deviation math exists for both. Transform-invariant
    // (rotated/mirrored) duplicate detection is explicitly out of scope. Deletion is ALWAYS a
    // human triage: bake_manager's append mode stacks design options on purpose.
    private (List<RhinoAuditFinding> Findings, int Scanned, bool Truncated) AuditNearDuplicates(
        global::Rhino.RhinoDoc document,
        double tolerance,
        int limit,
        CancellationToken cancellationToken)
    {
        const int MaxObjects = 6000;
        const int MaxPairChecks = 4000;
        var items = new List<(Guid Id, RhinoObject Obj, Point3d Center, double Diagonal)>();
        var scanned = 0;
        var truncated = false;
        foreach (var rhinoObject in document.Objects.GetObjectList(AuditEnumerator(ObjectType.Curve | ObjectType.Point))
                     .OrderBy(item => item.Id.ToString("D"), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rhinoObject.Geometry is null)
            {
                continue;
            }
            if (++scanned > MaxObjects)
            {
                truncated = true;
                break;
            }
            // Accurate boxes are mandatory here: the estimated (control-hull) box of a NURBS
            // rebuild overshoots by the control-polygon sagitta — orders of magnitude beyond the
            // tolerance-scale gates below — silently filtering out exactly the
            // different-representation coincidences this analyzer exists to catch.
            var box = rhinoObject.Geometry.GetBoundingBox(accurate: true);
            items.Add((rhinoObject.Id, rhinoObject, box.Center, box.Diagonal.Length));
        }

        var tree = new RTree();
        for (var index = 0; index < items.Count; index++)
        {
            tree.Insert(items[index].Center, index);
        }
        var pairChecks = 0;
        var duplicates = new Dictionary<string, (Guid A, Guid B, double Measure)>(StringComparer.Ordinal);
        for (var index = 0; index < items.Count && !truncated; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (idA, objectA, centerA, diagonalA) = items[index];
            var hits = new List<int>();
            tree.Search(new Sphere(centerA, Math.Max(tolerance * 2, 1e-9)), (_, args) => hits.Add(args.Id));
            foreach (var hit in hits)
            {
                if (hit <= index)
                {
                    continue;
                }
                var (idB, objectB, _, diagonalB) = items[hit];
                if (objectA.Geometry.ObjectType != objectB.Geometry.ObjectType ||
                    Math.Abs(diagonalA - diagonalB) > tolerance * 4)
                {
                    continue;
                }
                if (++pairChecks > MaxPairChecks)
                {
                    truncated = true;
                    break;
                }
                double? measure = null;
                if (objectA.Geometry is global::Rhino.Geometry.Point pointA &&
                    objectB.Geometry is global::Rhino.Geometry.Point pointB)
                {
                    var distance = pointA.Location.DistanceTo(pointB.Location);
                    if (distance <= tolerance)
                    {
                        measure = distance;
                    }
                }
                else if (objectA.Geometry is Curve curveA && objectB.Geometry is Curve curveB &&
                    Curve.GetDistancesBetweenCurves(
                        curveA, curveB, tolerance,
                        out var maxDistance, out _, out _, out _, out _, out _) &&
                    maxDistance <= tolerance)
                {
                    measure = maxDistance;
                }
                if (measure is null)
                {
                    continue;
                }
                var key = string.CompareOrdinal(idA.ToString("D"), idB.ToString("D")) <= 0
                    ? $"{idA:D}|{idB:D}"
                    : $"{idB:D}|{idA:D}";
                if (!duplicates.ContainsKey(key))
                {
                    duplicates[key] = (idA, idB, measure.Value);
                }
            }
        }

        var objectsById = items.ToDictionary(item => item.Id, item => item.Obj);
        var findings = duplicates.Values
            .OrderBy(pair => pair.Measure)
            .ThenBy(pair => pair.A)
            .ThenBy(pair => pair.B)
            .Take(limit + 1)
            .Select(pair => new RhinoAuditFinding(
                Hash($"nearDup|{pair.A:D}|{pair.B:D}")[..16],
                "nearDuplicates",
                new[] { pair.A, pair.B },
                new[] { ToState(objectsById[pair.A]).Fingerprint, ToState(objectsById[pair.B]).Fingerprint },
                pair.Measure,
                $"Position-coincident duplicates (max deviation {pair.Measure:G4} ≤ tolerance {tolerance:G4}): " +
                $"{pair.A:D} and {pair.B:D}. Which copy to keep is a human decision.",
                new[] { "deleteOneDuplicate" }))
            .ToList();
        if (findings.Count > limit)
        {
            findings.RemoveAt(findings.Count - 1);
            truncated = true;
        }
        return (findings, scanned, truncated);
    }

    // Junk census: unused block definitions (no references anywhere — not placed in the document
    // and not nested inside another definition), empty leaf layers (no objects including hidden
    // AND block-definition members, no children, not current), and invalid objects. Bad objects
    // propose QUARANTINE, never deletion — they are often repairable. Each subkind gets its own
    // finding budget so a junk-heavy category cannot starve the others.
    private (List<RhinoAuditFinding> Findings, int Scanned, bool Truncated) AuditPurgeCandidates(
        global::Rhino.RhinoDoc document,
        int limit,
        CancellationToken cancellationToken)
    {
        var scanned = 0;
        var truncated = false;
        var unusedBlocks = new List<RhinoAuditFinding>();
        var emptyLayers = new List<RhinoAuditFinding>();
        var badObjects = new List<RhinoAuditFinding>();

        foreach (var definition in document.InstanceDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (definition is null || definition.IsDeleted)
            {
                continue;
            }
            scanned++;
            // InUse(1) sees top-level + nested references IN THE DOCUMENT only; InUse(2) sees
            // references inside other definitions. Without the second check, an unplaced Root
            // nesting Child would flag BOTH in one pass, and purging Child first would corrupt
            // Root. With it, chains genuinely surface root-first.
            if (definition.InUse(1) || definition.InUse(2))
            {
                continue;
            }
            if (unusedBlocks.Count > limit)
            {
                truncated = true;
                break;
            }
            unusedBlocks.Add(new RhinoAuditFinding(
                Hash($"unusedBlock|{definition.Id:D}")[..16],
                "unusedBlockDefinition",
                new[] { definition.Id },
                Array.Empty<string>(),
                null,
                $"Block definition '{definition.Name}' has no references anywhere (not placed, not " +
                $"nested in another definition); {definition.ObjectCount} member object(s).",
                new[] { "purgeBlockDefinition" }));
        }

        // Layer census must include block-definition members: a block-library layer holding only
        // member geometry is IN USE, not empty.
        var layersWithObjects = new HashSet<int>();
        foreach (var rhinoObject in document.Objects.GetObjectList(
                     AuditEnumerator(includeDefinitionMembers: true)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            layersWithObjects.Add(rhinoObject.Attributes.LayerIndex);
        }
        var parentIds = new HashSet<Guid>();
        foreach (var layer in document.Layers)
        {
            if (layer is not null && !layer.IsDeleted && layer.ParentLayerId != Guid.Empty)
            {
                parentIds.Add(layer.ParentLayerId);
            }
        }
        foreach (var layer in document.Layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (layer is null || layer.IsDeleted)
            {
                continue;
            }
            scanned++;
            if (layersWithObjects.Contains(layer.Index) ||
                parentIds.Contains(layer.Id) ||
                layer.Index == document.Layers.CurrentLayerIndex)
            {
                continue;
            }
            if (emptyLayers.Count > limit)
            {
                truncated = true;
                break;
            }
            emptyLayers.Add(new RhinoAuditFinding(
                Hash($"emptyLayer|{layer.Id:D}")[..16],
                "emptyLayer",
                new[] { layer.Id },
                new[] { LayerFingerprint(layer) },
                null,
                $"Layer '{layer.FullPath}' is an empty leaf (no objects — including hidden and " +
                "block members — and no children).",
                new[] { "deleteLayer" }));
        }

        const int MaxValidityChecks = 8000;
        var validityChecks = 0;
        foreach (var rhinoObject in document.Objects.GetObjectList(AuditEnumerator())
                     .OrderBy(item => item.Id.ToString("D"), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scanned++;
            if (++validityChecks > MaxValidityChecks)
            {
                truncated = true;
                break;
            }
            if (rhinoObject.Geometry is null || rhinoObject.Geometry.IsValidWithLog(out var log))
            {
                continue;
            }
            if (badObjects.Count > limit)
            {
                truncated = true;
                break;
            }
            var reason = (log ?? string.Empty).Split('\n').FirstOrDefault()?.Trim();
            badObjects.Add(new RhinoAuditFinding(
                Hash($"badObject|{rhinoObject.Id:D}")[..16],
                "badObject",
                new[] { rhinoObject.Id },
                new[] { BadObjectFingerprint(rhinoObject) },
                null,
                $"Invalid geometry ({rhinoObject.Geometry.ObjectType}): " +
                $"{(string.IsNullOrEmpty(reason) ? "IsValidWithLog failed" : reason)} " +
                "— quarantine, do not delete (often repairable).",
                new[] { "quarantineToLayer" }));
        }

        var ordered = badObjects.Concat(emptyLayers).Concat(unusedBlocks)
            .OrderBy(finding => finding.Kind, StringComparer.Ordinal)
            .ThenBy(finding => finding.FindingId, StringComparer.Ordinal)
            .Take(limit + 1)
            .ToList();
        if (ordered.Count > limit)
        {
            ordered.RemoveAt(ordered.Count - 1);
            truncated = true;
        }
        return (ordered, scanned, truncated);
    }

    // ToState serializes geometry via ToJSON, which can throw on the very invalid geometry this
    // subkind reports; the bad-object fingerprint therefore hashes identity + attributes only.
    private static string BadObjectFingerprint(RhinoObject rhinoObject)
    {
        try
        {
            return ToState(rhinoObject).Fingerprint;
        }
        catch
        {
            var attributesJson = rhinoObject.Attributes.ToJSON(new SerializationOptions());
            return Hash($"badObject|{rhinoObject.Id:D}\n{attributesJson}");
        }
    }

    protected override Task<RhinoSceneObjectState> InspectObjectCoreAsync(
        global::Rhino.RhinoDoc document,
        Guid objectId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rhinoObject = document.Objects.FindId(objectId)
            ?? throw new KeyNotFoundException($"Rhino object {objectId:D} was not found.");
        return Task.FromResult(ToState(rhinoObject));
    }

    protected override Task<RhinoSceneMutationResult> CreatePrimitiveCoreAsync(
        global::Rhino.RhinoDoc document,
        CreateRhinoPrimitiveRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        RequireOperationId(request.OperationId);
        if (request.ObjectId == Guid.Empty)
        {
            throw new InvalidOperationException("ObjectId is required for primitive creation.");
        }
        if (string.IsNullOrWhiteSpace(request.LogicalEntityId))
        {
            throw new InvalidOperationException("LogicalEntityId is required for primitive creation.");
        }
        if (document.Objects.FindId(request.ObjectId) is not null)
        {
            throw new InvalidOperationException($"Rhino object {request.ObjectId:D} already exists.");
        }
        EnsureLogicalEntityAvailable(document, request.LogicalEntityId, exceptObjectId: null);

        using var geometry = CreatePrimitiveGeometry(request);
        if (!geometry.IsValid)
        {
            throw new InvalidOperationException($"The {request.Kind} primitive is not valid Rhino geometry.");
        }
        var attributes = CreatePrimitiveAttributes(document, request);

        var undo = document.BeginUndoRecord($"GPTino: {request.OperationId}");
        if (undo == 0)
        {
            throw new InvalidOperationException("Rhino could not start an undo record for primitive creation.");
        }
        var addedId = Guid.Empty;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            addedId = document.Objects.Add(geometry, attributes);
            if (addedId == Guid.Empty)
            {
                throw new InvalidOperationException("Rhino rejected the primitive geometry.");
            }
            if (addedId != request.ObjectId)
            {
                throw new InvalidOperationException(
                    $"Rhino returned object {addedId:D} instead of requested identity {request.ObjectId:D}.");
            }

            var afterObject = document.Objects.FindId(request.ObjectId)
                ?? throw new InvalidOperationException("Rhino object disappeared after primitive creation.");
            var after = ToState(afterObject);
            document.Views.Redraw();
            var diagnostics = new[]
            {
                new BridgeDiagnostic(
                    BridgeDiagnosticSeverity.Information,
                    "rhino_primitive_created",
                    $"Created {request.Kind} primitive as object {request.ObjectId:D}.",
                    request.ObjectId),
            };
            return Task.FromResult(new RhinoSceneMutationResult(
                request.OperationId,
                Changed: true,
                BeforeFingerprint: null,
                after.Fingerprint,
                request.ObjectId,
                after,
                diagnostics));
        }
        catch (Exception mutationFailure) when (addedId != Guid.Empty)
        {
            var rolledBack = document.Objects.FindId(addedId) is null ||
                document.Objects.Delete(addedId, quiet: true);
            if (!rolledBack || document.Objects.FindId(addedId) is not null)
            {
                throw new AggregateException(
                    $"Primitive creation failed and object {addedId:D} could not be rolled back; use Rhino Undo.",
                    mutationFailure);
            }
            throw;
        }
        finally
        {
            if (undo != 0)
            {
                document.EndUndoRecord(undo);
            }
        }
    }

    protected override Task<RhinoUpsertValidationResult> ValidateUpsertObjectCoreAsync(
        global::Rhino.RhinoDoc document,
        UpsertRhinoObjectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var prepared = PrepareUpsert(document, request);
        return Task.FromResult(new RhinoUpsertValidationResult(
            request.OperationId,
            request.ObjectId,
            prepared.Geometry.ObjectType.ToString(),
            prepared.Existing is not null,
            prepared.Before?.Fingerprint,
            IsValid: true));
    }

    protected override Task<RhinoSceneMutationResult> UpsertObjectCoreAsync(
        global::Rhino.RhinoDoc document,
        UpsertRhinoObjectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var prepared = PrepareUpsert(document, request);
        var geometry = prepared.Geometry;
        var existing = prepared.Existing;
        var before = prepared.Before;
        var attributes = prepared.Attributes;
        var undo = document.BeginUndoRecord($"GPTino: {request.OperationId}");
        if (undo == 0)
        {
            throw new InvalidOperationException("Rhino could not start an undo record for object upsert.");
        }
        try
        {
            Guid objectId;
            if (existing is null)
            {
                objectId = document.Objects.Add(geometry, attributes);
                if (objectId == Guid.Empty)
                {
                    throw new InvalidOperationException("Rhino rejected the new geometry object.");
                }
                if (objectId != request.ObjectId)
                {
                    document.Objects.Delete(objectId, quiet: true);
                    throw new InvalidOperationException(
                        "Rhino could not preserve the requested ObjectId; the unexpected object was removed.");
                }
            }
            else
            {
                objectId = existing.Id;
                using var oldGeometry = existing.Geometry.Duplicate();
                using var oldAttributes = existing.Attributes.Duplicate();
                var geometryReplaced = false;
                try
                {
                    if (!document.Objects.Replace(objectId, geometry, ignoreModes: false))
                    {
                        throw new InvalidOperationException($"Rhino could not replace object {objectId:D}.");
                    }
                    geometryReplaced = true;
                    if (!document.Objects.ModifyAttributes(objectId, attributes, quiet: true))
                    {
                        throw new InvalidOperationException(
                            $"Rhino could not update attributes for {objectId:D}.");
                    }
                }
                catch (Exception mutationFailure) when (geometryReplaced)
                {
                    var restoredGeometry = document.Objects.Replace(
                        objectId,
                        oldGeometry,
                        ignoreModes: true);
                    var restoredAttributes = document.Objects.ModifyAttributes(
                        objectId,
                        oldAttributes,
                        quiet: true);
                    if (!restoredGeometry || !restoredAttributes)
                    {
                        throw new AggregateException(
                            $"Rhino object {objectId:D} update failed and rollback was incomplete; use Rhino Undo.",
                            mutationFailure);
                    }
                    throw;
                }
            }

            var afterObject = document.Objects.FindId(objectId)
                ?? throw new InvalidOperationException("Rhino object disappeared after upsert.");
            if (afterObject.Id != objectId || afterObject.Id != request.ObjectId)
            {
                throw new InvalidOperationException("Rhino object identity changed during upsert.");
            }
            var after = ToState(afterObject);
            document.Views.Redraw();
            return Task.FromResult(new RhinoSceneMutationResult(
                request.OperationId,
                before is null || !string.Equals(before.Fingerprint, after.Fingerprint, StringComparison.Ordinal),
                before?.Fingerprint,
                after.Fingerprint,
                objectId,
                after));
        }
        finally
        {
            document.EndUndoRecord(undo);
        }
    }

    /// <summary>
    /// The human-wins default-deny: CAS fingerprints prove "unchanged since inspected", never
    /// "user consents". Objects without a GPTino provenance stamp are the user's own geometry —
    /// destroying or mutating them requires a server-injected approval (minted when the user
    /// approves the change on the panel), not just a fingerprint.
    /// </summary>
    private static void RequireProvenanceOrApproval(RhinoObject existing, bool approved, string verb)
    {
        if (approved)
        {
            return;
        }
        var attributes = existing.Attributes;
        var hasProvenance =
            !string.IsNullOrEmpty(attributes.GetUserString(LogicalEntityKey)) ||
            !string.IsNullOrEmpty(attributes.GetUserString(BakeFamilyKey));
        if (!hasProvenance)
        {
            // Typed code, not a bare exception: the refusal happens BEFORE any document change, so
            // the executor can classify it as a deterministic failure instead of the
            // "outcome unknown -> recoveryRequired" bucket every mid-write fault lands in.
            throw new BridgeProtocolException(
                ApprovalRequiredCode,
                $"Rhino object {existing.Id:D} was not created by GPTino; {verb} it requires the " +
                "user's explicit approval. Present the change (naming this object) and resubmit " +
                "with the approval grant the panel issues. No change was applied.");
        }
    }

    protected override Task<RhinoSceneMutationResult> DeleteObjectCoreAsync(
        global::Rhino.RhinoDoc document,
        DeleteRhinoObjectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOperationId(request.OperationId);
        if (request.ObjectId == Guid.Empty || string.IsNullOrWhiteSpace(request.ExpectedFingerprint))
        {
            throw new InvalidOperationException("ObjectId and ExpectedFingerprint are required for deletion.");
        }
        var existing = document.Objects.FindId(request.ObjectId)
            ?? throw new KeyNotFoundException($"Rhino object {request.ObjectId:D} was not found.");
        var before = ToState(existing);
        if (!string.Equals(before.Fingerprint, request.ExpectedFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Rhino object changed after the request snapshot.");
        }
        RequireProvenanceOrApproval(existing, request.Approved, "deleting");

        var undo = document.BeginUndoRecord($"GPTino: {request.OperationId}");
        try
        {
            if (!document.Objects.Delete(request.ObjectId, quiet: true))
            {
                throw new InvalidOperationException($"Rhino could not delete object {request.ObjectId:D}.");
            }
            document.Views.Redraw();
            return Task.FromResult(new RhinoSceneMutationResult(
                request.OperationId,
                Changed: true,
                before.Fingerprint,
                AfterFingerprint: null,
                request.ObjectId));
        }
        finally
        {
            if (undo != 0)
            {
                document.EndUndoRecord(undo);
            }
        }
    }

    protected override Task<RhinoSceneMutationResult> EnsureLayerCoreAsync(
        global::Rhino.RhinoDoc document,
        EnsureRhinoLayerRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOperationId(request.OperationId);
        if (string.IsNullOrWhiteSpace(request.FullPath))
        {
            throw new InvalidOperationException("Layer full path is required.");
        }

        var normalizedPath = request.FullPath.Trim();
        var byPath = document.Layers.FindByFullPath(normalizedPath, -1);
        var byId = request.LayerId == Guid.Empty
            ? -1
            : document.Layers.Find(request.LayerId, ignoreDeletedLayers: false, notFoundReturnValue: -1);
        if (byId >= 0 && byPath >= 0 && byId != byPath)
        {
            throw new InvalidOperationException(
                $"LayerId {request.LayerId:D} and path '{normalizedPath}' identify different layers.");
        }
        if (request.LayerId != Guid.Empty && byId < 0 && byPath >= 0)
        {
            throw new InvalidOperationException(
                $"Layer path '{normalizedPath}' already exists with another identity.");
        }
        if (byId >= 0 &&
            !string.Equals(document.Layers[byId].FullPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "EnsureLayer does not rename or re-parent an existing layer without a fingerprinted operation.");
        }

        var existing = byId >= 0 ? byId : byPath;
        var before = existing >= 0 ? LayerFingerprint(document.Layers[existing]) : null;
        var leafName = normalizedPath.Split(new[] { "::" }, StringSplitOptions.None)[^1].Trim();
        if (string.IsNullOrWhiteSpace(leafName))
        {
            throw new InvalidOperationException("Layer leaf name is required.");
        }

        var parentLayerId = request.ParentLayerId.GetValueOrDefault();
        if (parentLayerId != Guid.Empty &&
            document.Layers.Find(parentLayerId, ignoreDeletedLayers: false, notFoundReturnValue: -1) < 0)
        {
            throw new KeyNotFoundException($"Parent layer {parentLayerId:D} was not found.");
        }
        if (existing >= 0 && document.Layers[existing].ParentLayerId != parentLayerId)
        {
            throw new InvalidOperationException(
                "EnsureLayer does not re-parent an existing layer without a fingerprinted operation.");
        }

        var layer = existing >= 0
            ? CommonObject.FromJSON(document.Layers[existing].ToJSON(new SerializationOptions())) as Layer
                ?? throw new InvalidOperationException("Could not clone the existing Rhino layer.")
            : new Layer();
        layer.Name = leafName;
        layer.Color = System.Drawing.Color.FromArgb(request.ArgbColor);
        layer.ParentLayerId = parentLayerId;
        if (existing < 0 && request.LayerId != Guid.Empty)
        {
            layer.Id = request.LayerId;
        }

        var undo = document.BeginUndoRecord($"GPTino: {request.OperationId}");
        try
        {
            var index = existing >= 0
                ? document.Layers.Modify(layer, existing, quiet: true) ? existing : -1
                : document.Layers.Add(layer);
            if (index < 0)
            {
                throw new InvalidOperationException($"Rhino could not ensure layer '{normalizedPath}'.");
            }
            var actual = document.Layers[index];
            if (request.LayerId != Guid.Empty && actual.Id != request.LayerId)
            {
                if (existing < 0)
                {
                    document.Layers.Delete(actual.Id, quiet: true);
                }
                throw new InvalidOperationException(
                    "Rhino could not preserve the requested LayerId; the unexpected layer was removed.");
            }
            var after = LayerFingerprint(actual);
            return Task.FromResult(new RhinoSceneMutationResult(
                request.OperationId,
                !string.Equals(before, after, StringComparison.Ordinal),
                before,
                after,
                actual.Id));
        }
        finally
        {
            if (undo != 0)
            {
                document.EndUndoRecord(undo);
            }
        }
    }

    // Heals one audited near-miss pair: the ANCHOR curve is referenced (fingerprint-verified,
    // never modified); the MOVE curve's chosen endpoint is set onto the anchor's endpoint. The
    // fix is verified before any write — the modified duplicate must be valid and land within
    // Tolerance — so a failed strategy changes nothing. SetStartPoint/SetEndPoint is not
    // implemented for every curve type (and can silently NURBS-ify arcs), so unsupported types
    // fail loudly instead of approximating.
    protected override Task<RhinoSceneMutationResult> FixEndpointPairCoreAsync(
        global::Rhino.RhinoDoc document,
        FixEndpointPairRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        RequireOperationId(request.OperationId);
        if (request.AnchorObjectId == Guid.Empty || request.MoveObjectId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.ExpectedAnchorFingerprint) ||
            string.IsNullOrWhiteSpace(request.ExpectedFingerprint))
        {
            throw new InvalidOperationException(
                "Anchor/move object ids and both expected fingerprints are required for an endpoint fix.");
        }
        if (request.AnchorObjectId == request.MoveObjectId)
        {
            throw new InvalidOperationException("Anchor and move objects must differ.");
        }
        if (request.AnchorEnd is not (0 or 1) || request.MoveEnd is not (0 or 1))
        {
            throw new InvalidOperationException("Endpoint indices must be 0 (start) or 1 (end).");
        }
        var tolerance = request.Tolerance > 0 ? request.Tolerance : document.ModelAbsoluteTolerance;

        var anchorObject = document.Objects.FindId(request.AnchorObjectId)
            ?? throw new KeyNotFoundException($"Rhino object {request.AnchorObjectId:D} was not found.");
        var moveObject = document.Objects.FindId(request.MoveObjectId)
            ?? throw new KeyNotFoundException($"Rhino object {request.MoveObjectId:D} was not found.");
        var anchorState = ToState(anchorObject);
        if (!string.Equals(anchorState.Fingerprint, request.ExpectedAnchorFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Anchor Rhino object changed after the request snapshot.");
        }
        var before = ToState(moveObject);
        if (!string.Equals(before.Fingerprint, request.ExpectedFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Rhino object changed after the request snapshot.");
        }
        RequireProvenanceOrApproval(moveObject, request.Approved, "editing");

        if (anchorObject.Geometry is not Curve anchorCurve || moveObject.Geometry is not Curve moveCurve)
        {
            throw new InvalidOperationException("Endpoint fixes require two curve objects.");
        }
        var anchorPoint = request.AnchorEnd == 0 ? anchorCurve.PointAtStart : anchorCurve.PointAtEnd;

        var healed = moveCurve.DuplicateCurve()
            ?? throw new InvalidOperationException("Rhino could not duplicate the curve to heal.");
        try
        {
            var moved = request.MoveEnd == 0
                ? healed.SetStartPoint(anchorPoint)
                : healed.SetEndPoint(anchorPoint);
            if (!moved)
            {
                throw new InvalidOperationException(
                    $"This curve type ({moveCurve.GetType().Name}) does not support endpoint editing; " +
                    "rebuild it as a NURBS curve first or choose the other curve as the move target.");
            }
            var resultingPoint = request.MoveEnd == 0 ? healed.PointAtStart : healed.PointAtEnd;
            var resultingGap = resultingPoint.DistanceTo(anchorPoint);
            if (!healed.IsValid || resultingGap > tolerance)
            {
                throw new InvalidOperationException(
                    $"Endpoint edit did not converge (resulting gap {resultingGap:G4} > tolerance {tolerance:G4}); " +
                    "no change was applied.");
            }

            var undo = document.BeginUndoRecord($"GPTino: {request.OperationId}");
            if (undo == 0)
            {
                throw new InvalidOperationException("Rhino could not start an undo record for the endpoint fix.");
            }
            try
            {
                // Guid-based Replace overload (like TransformObjectCoreAsync) — the ObjRef
                // overload would leave a native CRhinoObjRef to the finalizer.
                if (!document.Objects.Replace(moveObject.Id, healed))
                {
                    throw new InvalidOperationException(
                        $"Rhino could not replace curve {request.MoveObjectId:D} with the healed geometry.");
                }
                var afterObject = document.Objects.FindId(request.MoveObjectId)
                    ?? throw new InvalidOperationException("Rhino object disappeared after the endpoint fix.");
                var after = ToState(afterObject);
                document.Views.Redraw();
                return Task.FromResult(new RhinoSceneMutationResult(
                    request.OperationId,
                    Changed: true,
                    before.Fingerprint,
                    after.Fingerprint,
                    request.MoveObjectId,
                    after,
                    new[]
                    {
                        new BridgeDiagnostic(
                            BridgeDiagnosticSeverity.Information,
                            "endpoint_fix_verified",
                            $"Endpoint gap closed to {resultingGap:G4} (tolerance {tolerance:G4}).")
                    }));
            }
            finally
            {
                // An orphaned open record would swallow the user's later edits into this undo step
                // AND make every subsequent GPTino mutation hard-fail on BeginUndoRecord == 0.
                document.EndUndoRecord(undo);
            }
        }
        finally
        {
            healed.Dispose();
        }
    }

    protected override Task<RhinoSceneMutationResult> TransformObjectCoreAsync(
        global::Rhino.RhinoDoc document,
        TransformRhinoObjectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        RequireOperationId(request.OperationId);
        if (request.ObjectId == Guid.Empty || string.IsNullOrWhiteSpace(request.ExpectedFingerprint))
        {
            throw new InvalidOperationException(
                "ObjectId and ExpectedFingerprint are required for a Rhino transform.");
        }

        var existing = document.Objects.FindId(request.ObjectId)
            ?? throw new KeyNotFoundException($"Rhino object {request.ObjectId:D} was not found.");
        var before = ToState(existing);
        if (!string.Equals(before.Fingerprint, request.ExpectedFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Rhino object changed after the request snapshot.");
        }
        RequireProvenanceOrApproval(existing, request.Approved, "transforming");

        var transform = CreateTransform(request.Matrix);
        using var originalGeometry = existing.Geometry.Duplicate();
        using var transformedGeometry = existing.Geometry.Duplicate();
        if (!transformedGeometry.Transform(transform) || !transformedGeometry.IsValid)
        {
            throw new InvalidOperationException(
                $"Rhino could not apply the requested transform to object {request.ObjectId:D}.");
        }

        var undo = document.BeginUndoRecord($"GPTino: {request.OperationId}");
        if (undo == 0)
        {
            throw new InvalidOperationException("Rhino could not start an undo record for transform.");
        }
        var replaced = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!document.Objects.Replace(request.ObjectId, transformedGeometry, ignoreModes: false))
            {
                throw new InvalidOperationException($"Rhino could not transform object {request.ObjectId:D}.");
            }
            replaced = true;

            var afterObject = document.Objects.FindId(request.ObjectId)
                ?? throw new InvalidOperationException("Rhino object disappeared after transform.");
            if (afterObject.Id != request.ObjectId)
            {
                throw new InvalidOperationException("Rhino object identity changed during transform.");
            }
            var after = ToState(afterObject);
            document.Views.Redraw();
            var changed = !string.Equals(before.Fingerprint, after.Fingerprint, StringComparison.Ordinal);
            var diagnostics = new[]
            {
                new BridgeDiagnostic(
                    BridgeDiagnosticSeverity.Information,
                    changed ? "rhino_object_transformed" : "rhino_transform_no_change",
                    changed
                        ? $"Transformed Rhino object {request.ObjectId:D}."
                        : $"Transform left Rhino object {request.ObjectId:D} unchanged.",
                    request.ObjectId),
            };
            return Task.FromResult(new RhinoSceneMutationResult(
                request.OperationId,
                changed,
                before.Fingerprint,
                after.Fingerprint,
                request.ObjectId,
                after,
                diagnostics));
        }
        catch (Exception mutationFailure) when (replaced)
        {
            var geometryRestored = document.Objects.Replace(
                request.ObjectId,
                originalGeometry,
                ignoreModes: true);
            var restored = document.Objects.FindId(request.ObjectId);
            var fingerprintRestored = restored is not null &&
                string.Equals(ToState(restored).Fingerprint, before.Fingerprint, StringComparison.Ordinal);
            if (!geometryRestored || !fingerprintRestored)
            {
                throw new AggregateException(
                    $"Transform failed and object {request.ObjectId:D} rollback was incomplete; use Rhino Undo.",
                    mutationFailure);
            }
            throw;
        }
        finally
        {
            if (undo != 0)
            {
                document.EndUndoRecord(undo);
            }
        }
    }

    private static GeometryBase CreatePrimitiveGeometry(CreateRhinoPrimitiveRequest request)
    {
        var suppliedDefinitionCount = new object?[]
        {
            request.Point,
            request.Line,
            request.Polyline,
            request.Circle,
            request.Box,
            request.Sphere,
        }.Count(item => item is not null);
        if (suppliedDefinitionCount != 1)
        {
            throw new InvalidOperationException(
                "Exactly one primitive definition matching Kind must be supplied.");
        }

        return request.Kind switch
        {
            RhinoPrimitiveKind.Point when request.Point is not null =>
                new Point(ToPoint3d(request.Point.Location, "point.location")),
            RhinoPrimitiveKind.Line when request.Line is not null =>
                CreateLine(request.Line),
            RhinoPrimitiveKind.Polyline when request.Polyline is not null =>
                CreatePolyline(request.Polyline),
            RhinoPrimitiveKind.Circle when request.Circle is not null =>
                CreateCircle(request.Circle),
            RhinoPrimitiveKind.Box when request.Box is not null =>
                CreateBox(request.Box),
            RhinoPrimitiveKind.Sphere when request.Sphere is not null =>
                CreateSphere(request.Sphere),
            _ => throw new InvalidOperationException(
                $"Primitive definition does not match Kind '{request.Kind}'."),
        };
    }

    private static LineCurve CreateLine(RhinoLinePrimitive definition)
    {
        var from = ToPoint3d(definition.From, "line.from");
        var to = ToPoint3d(definition.To, "line.to");
        if (from.DistanceToSquared(to) <=
            global::Rhino.RhinoMath.ZeroTolerance * global::Rhino.RhinoMath.ZeroTolerance)
        {
            throw new InvalidOperationException("Line endpoints must be distinct.");
        }
        return new LineCurve(from, to);
    }

    private static PolylineCurve CreatePolyline(RhinoPolylinePrimitive definition)
    {
        ArgumentNullException.ThrowIfNull(definition.Vertices);
        var minimumCount = definition.Closed ? 3 : 2;
        if (definition.Vertices.Count < minimumCount || definition.Vertices.Count > 10_000)
        {
            throw new InvalidOperationException(
                $"Polyline requires {minimumCount} to 10000 input vertices.");
        }

        var vertices = definition.Vertices
            .Select((point, index) => ToPoint3d(point, $"polyline.vertices[{index}]"))
            .ToList();
        if (definition.Closed && vertices[0].DistanceToSquared(vertices[^1]) >
            global::Rhino.RhinoMath.ZeroTolerance * global::Rhino.RhinoMath.ZeroTolerance)
        {
            vertices.Add(vertices[0]);
        }
        return new PolylineCurve(vertices);
    }

    private static NurbsCurve CreateCircle(RhinoCirclePrimitive definition)
    {
        var center = ToPoint3d(definition.Center, "circle.center");
        var normal = ToVector3d(definition.Normal, "circle.normal");
        RequirePositiveFinite(definition.Radius, "circle.radius");
        if (!normal.Unitize())
        {
            throw new InvalidOperationException("Circle normal must be non-zero.");
        }
        var plane = new Plane(center, normal);
        if (!plane.IsValid)
        {
            throw new InvalidOperationException("Circle plane is invalid.");
        }
        return new Circle(plane, definition.Radius).ToNurbsCurve();
    }

    private static Brep CreateBox(RhinoBoxPrimitive definition)
    {
        var minimum = ToPoint3d(definition.Minimum, "box.minimum");
        var maximum = ToPoint3d(definition.Maximum, "box.maximum");
        if (maximum.X <= minimum.X || maximum.Y <= minimum.Y || maximum.Z <= minimum.Z)
        {
            throw new InvalidOperationException(
                "Box maximum components must each be greater than minimum components.");
        }
        var box = new Box(new BoundingBox(minimum, maximum));
        return box.ToBrep();
    }

    private static Brep CreateSphere(RhinoSpherePrimitive definition)
    {
        var center = ToPoint3d(definition.Center, "sphere.center");
        RequirePositiveFinite(definition.Radius, "sphere.radius");
        return new Sphere(center, definition.Radius).ToBrep();
    }

    private static ObjectAttributes CreatePrimitiveAttributes(
        global::Rhino.RhinoDoc document,
        CreateRhinoPrimitiveRequest request)
    {
        var requestedAttributes = request.Attributes;
        var attributes = new ObjectAttributes
        {
            ObjectId = request.ObjectId,
            Name = requestedAttributes?.Name ?? string.Empty,
        };
        if (requestedAttributes?.Name is { Length: > 1024 })
        {
            throw new InvalidOperationException("Primitive object name must be at most 1024 characters.");
        }

        if (requestedAttributes?.LayerId is Guid layerId)
        {
            if (layerId == Guid.Empty)
            {
                throw new InvalidOperationException("Primitive LayerId cannot be empty.");
            }
            var layerIndex = document.Layers.Find(
                layerId,
                ignoreDeletedLayers: false,
                notFoundReturnValue: -1);
            if (layerIndex < 0)
            {
                throw new KeyNotFoundException($"Rhino layer {layerId:D} was not found.");
            }
            attributes.LayerIndex = layerIndex;
        }
        else
        {
            attributes.LayerIndex = document.Layers.CurrentLayerIndex;
        }

        if (requestedAttributes?.ArgbColor is int argbColor)
        {
            attributes.ObjectColor = System.Drawing.Color.FromArgb(argbColor);
            attributes.ColorSource = ObjectColorSource.ColorFromObject;
        }
        attributes.SetUserString(LogicalEntityKey, request.LogicalEntityId);
        if (!string.IsNullOrWhiteSpace(request.SourceDocKey))
        {
            attributes.SetUserString(SourceDocKeyKey, request.SourceDocKey);
        }
        return attributes;
    }

    private static void EnsureLogicalEntityAvailable(
        global::Rhino.RhinoDoc document,
        string logicalEntityId,
        Guid? exceptObjectId)
    {
        var collision = document.Objects.FirstOrDefault(candidate =>
            candidate.Id != exceptObjectId &&
            string.Equals(
                candidate.Attributes.GetUserString(LogicalEntityKey),
                logicalEntityId,
                StringComparison.Ordinal));
        if (collision is not null)
        {
            throw new InvalidOperationException(
                $"Logical entity '{logicalEntityId}' is already bound to Rhino object {collision.Id:D}.");
        }
    }

    private static Transform CreateTransform(RhinoTransformMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        var values = new[]
        {
            matrix.M00, matrix.M01, matrix.M02, matrix.M03,
            matrix.M10, matrix.M11, matrix.M12, matrix.M13,
            matrix.M20, matrix.M21, matrix.M22, matrix.M23,
            matrix.M30, matrix.M31, matrix.M32, matrix.M33,
        };
        if (values.Any(value => !double.IsFinite(value)))
        {
            throw new InvalidOperationException("Transform matrix components must be finite.");
        }
        const double affineTolerance = 1e-12;
        if (Math.Abs(matrix.M30) > affineTolerance ||
            Math.Abs(matrix.M31) > affineTolerance ||
            Math.Abs(matrix.M32) > affineTolerance ||
            Math.Abs(matrix.M33 - 1.0) > affineTolerance)
        {
            throw new InvalidOperationException(
                "Transform matrix must be affine with final row [0, 0, 0, 1].");
        }

        var linearDeterminant =
            matrix.M00 * (matrix.M11 * matrix.M22 - matrix.M12 * matrix.M21) -
            matrix.M01 * (matrix.M10 * matrix.M22 - matrix.M12 * matrix.M20) +
            matrix.M02 * (matrix.M10 * matrix.M21 - matrix.M11 * matrix.M20);
        if (Math.Abs(linearDeterminant) <= 1e-12)
        {
            throw new InvalidOperationException("Transform matrix must be non-singular.");
        }

        var transform = Transform.Identity;
        transform.M00 = matrix.M00;
        transform.M01 = matrix.M01;
        transform.M02 = matrix.M02;
        transform.M03 = matrix.M03;
        transform.M10 = matrix.M10;
        transform.M11 = matrix.M11;
        transform.M12 = matrix.M12;
        transform.M13 = matrix.M13;
        transform.M20 = matrix.M20;
        transform.M21 = matrix.M21;
        transform.M22 = matrix.M22;
        transform.M23 = matrix.M23;
        transform.M30 = matrix.M30;
        transform.M31 = matrix.M31;
        transform.M32 = matrix.M32;
        transform.M33 = matrix.M33;
        if (!transform.IsValid)
        {
            throw new InvalidOperationException("Transform matrix is not valid in RhinoCommon.");
        }
        return transform;
    }

    private static global::Rhino.Geometry.Point3d ToPoint3d(RhinoPoint3d point, string field)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z))
        {
            throw new InvalidOperationException($"{field} coordinates must be finite.");
        }
        return new global::Rhino.Geometry.Point3d(point.X, point.Y, point.Z);
    }

    private static Vector3d ToVector3d(RhinoVector3d vector, string field)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (!double.IsFinite(vector.X) || !double.IsFinite(vector.Y) || !double.IsFinite(vector.Z))
        {
            throw new InvalidOperationException($"{field} components must be finite.");
        }
        return new Vector3d(vector.X, vector.Y, vector.Z);
    }

    private static void RequirePositiveFinite(double value, string field)
    {
        if (!double.IsFinite(value) || value <= global::Rhino.RhinoMath.ZeroTolerance)
        {
            throw new InvalidOperationException($"{field} must be finite and positive.");
        }
    }

    private static void ValidateListRequest(RhinoListObjectsRequest request)
    {
        if (request.Limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Rhino list Limit must be between 1 and 500.");
        }
        if (request.ObjectId == Guid.Empty || request.LayerId == Guid.Empty)
        {
            throw new InvalidOperationException("Rhino list ID filters cannot be empty GUIDs.");
        }
        if (request.LayerFullPath is not null && string.IsNullOrWhiteSpace(request.LayerFullPath) ||
            request.NameContains is not null && string.IsNullOrEmpty(request.NameContains) ||
            request.GeometryType is not null && string.IsNullOrWhiteSpace(request.GeometryType))
        {
            throw new InvalidOperationException("Rhino list text filters cannot be blank.");
        }
    }

    private static string CanonicalQuery(RhinoListObjectsRequest request) =>
        JsonSerializer.Serialize(request, BridgeProtocol.JsonOptions);

    private static RhinoBoundingBoxSummary? ToBounds(BoundingBox bounds)
    {
        if (!bounds.IsValid)
        {
            return null;
        }
        return new RhinoBoundingBoxSummary(
            new RhinoPoint3d(bounds.Min.X, bounds.Min.Y, bounds.Min.Z),
            new RhinoPoint3d(bounds.Max.X, bounds.Max.Y, bounds.Max.Z),
            new RhinoPoint3d(bounds.Center.X, bounds.Center.Y, bounds.Center.Z),
            new RhinoVector3d(
                bounds.Max.X - bounds.Min.X,
                bounds.Max.Y - bounds.Min.Y,
                bounds.Max.Z - bounds.Min.Z));
    }

    private static RhinoBoundingBoxSummary? UnionBounds(
        IEnumerable<RhinoBoundingBoxSummary?> bounds)
    {
        var valid = bounds.Where(item => item is not null).Select(item => item!).ToArray();
        if (valid.Length == 0)
        {
            return null;
        }
        var minimum = new RhinoPoint3d(
            valid.Min(item => item.Minimum.X),
            valid.Min(item => item.Minimum.Y),
            valid.Min(item => item.Minimum.Z));
        var maximum = new RhinoPoint3d(
            valid.Max(item => item.Maximum.X),
            valid.Max(item => item.Maximum.Y),
            valid.Max(item => item.Maximum.Z));
        return new RhinoBoundingBoxSummary(
            minimum,
            maximum,
            new RhinoPoint3d(
                (minimum.X + maximum.X) / 2.0,
                (minimum.Y + maximum.Y) / 2.0,
                (minimum.Z + maximum.Z) / 2.0),
            new RhinoVector3d(
                maximum.X - minimum.X,
                maximum.Y - minimum.Y,
                maximum.Z - minimum.Z));
    }

    private static PreparedRhinoUpsert PrepareUpsert(
        global::Rhino.RhinoDoc document,
        UpsertRhinoObjectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireOperationId(request.OperationId);
        if (request.ObjectId == Guid.Empty)
        {
            throw new InvalidOperationException("ObjectId is required for a managed Rhino object.");
        }
        if (string.IsNullOrWhiteSpace(request.LogicalEntityId))
        {
            throw new InvalidOperationException("LogicalEntityId is required for a managed Rhino object.");
        }
        if (string.IsNullOrWhiteSpace(request.GeometryType))
        {
            throw new InvalidOperationException("GeometryType is required.");
        }

        var decodedGeometry = CommonObject.FromJSON(request.GeometryJson);
        if (decodedGeometry is not GeometryBase geometry)
        {
            decodedGeometry?.Dispose();
            throw new InvalidOperationException("GeometryJson is not a Rhino GeometryBase JSON payload.");
        }
        try
        {
            if (!geometry.IsValidWithLog(out var geometryLog))
            {
                throw new InvalidOperationException(
                    "GeometryJson decoded to invalid Rhino geometry" +
                    (string.IsNullOrWhiteSpace(geometryLog) ? "." : $": {geometryLog}"));
            }
            var actualGeometryType = geometry.ObjectType.ToString();
            if (!string.Equals(
                    actualGeometryType,
                    request.GeometryType,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"GeometryType '{request.GeometryType}' does not match payload type '{actualGeometryType}'.");
            }

            var existing = document.Objects.FindId(request.ObjectId);
            var before = existing is null ? null : ToState(existing);
            if (before is not null && !string.IsNullOrWhiteSpace(before.LogicalEntityId) &&
                !string.Equals(before.LogicalEntityId, request.LogicalEntityId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Upsert cannot reassign an existing Rhino object to another logical entity.");
            }
            var logicalCollision = document.Objects.FirstOrDefault(candidate =>
                candidate.Id != existing?.Id &&
                string.Equals(
                    candidate.Attributes.GetUserString(LogicalEntityKey),
                    request.LogicalEntityId,
                    StringComparison.Ordinal));
            if (logicalCollision is not null)
            {
                throw new InvalidOperationException(
                    $"Logical entity '{request.LogicalEntityId}' is already bound to Rhino object " +
                    $"{logicalCollision.Id:D}.");
            }
            if (before is null && !string.IsNullOrWhiteSpace(request.ExpectedFingerprint))
            {
                throw new InvalidOperationException(
                    "ExpectedFingerprint was supplied, but the requested Rhino object does not exist.");
            }
            if (before is not null &&
                (string.IsNullOrWhiteSpace(request.ExpectedFingerprint) ||
                 !string.Equals(before.Fingerprint, request.ExpectedFingerprint, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Rhino object changed after the request snapshot.");
            }
            if (existing is not null)
            {
                // Creates are always allowed; REPLACING an existing object destroys what the user
                // may have made — same default-deny as delete/transform.
                RequireProvenanceOrApproval(existing, request.Approved, "modifying");
            }

            var attributes = ParseAttributes(request.AttributesJson, existing?.Attributes);
            try
            {
                attributes.SetUserString(LogicalEntityKey, request.LogicalEntityId);
                if (!string.IsNullOrWhiteSpace(request.SourceDocKey))
                {
                    attributes.SetUserString(SourceDocKeyKey, request.SourceDocKey);
                }
                attributes.ObjectId = existing?.Id ?? request.ObjectId;
                return new PreparedRhinoUpsert(existing, before, geometry, attributes);
            }
            catch
            {
                attributes.Dispose();
                throw;
            }
        }
        catch
        {
            geometry.Dispose();
            throw;
        }
    }

    private static ObjectAttributes ParseAttributes(
        string json,
        ObjectAttributes? fallback)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return fallback?.Duplicate() ?? new ObjectAttributes();
        }
        var decoded = CommonObject.FromJSON(json);
        if (decoded is ObjectAttributes attributes)
        {
            return attributes;
        }
        decoded?.Dispose();
        throw new InvalidOperationException(
            "AttributesJson is not a Rhino ObjectAttributes JSON payload.");
    }

    private static RhinoSceneObjectState ToState(RhinoObject rhinoObject)
    {
        var options = new SerializationOptions();
        var geometryJson = rhinoObject.Geometry.ToJSON(options);
        var attributesJson = rhinoObject.Attributes.ToJSON(options);
        var logicalId = rhinoObject.Attributes.GetUserString(LogicalEntityKey) ?? string.Empty;
        var fingerprint = Hash($"{rhinoObject.Id:D}\n{logicalId}\n{geometryJson}\n{attributesJson}");
        return new RhinoSceneObjectState(
            rhinoObject.Id,
            logicalId,
            rhinoObject.Geometry.ObjectType.ToString(),
            geometryJson,
            attributesJson,
            fingerprint);
    }

    private static string LayerFingerprint(Layer layer) => Hash(
        $"{layer.Id:D}\n{layer.FullPath}\n{layer.ParentLayerId:D}\n{layer.Color.ToArgb()}");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void RequireOperationId(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new InvalidOperationException("OperationId is required.");
        }
    }

    private sealed class PreparedRhinoUpsert : IDisposable
    {
        public PreparedRhinoUpsert(
            RhinoObject? existing,
            RhinoSceneObjectState? before,
            GeometryBase geometry,
            ObjectAttributes attributes)
        {
            Existing = existing;
            Before = before;
            Geometry = geometry;
            Attributes = attributes;
        }

        public RhinoObject? Existing { get; }

        public RhinoSceneObjectState? Before { get; }

        public GeometryBase Geometry { get; }

        public ObjectAttributes Attributes { get; }

        public void Dispose()
        {
            Attributes.Dispose();
            Geometry.Dispose();
        }
    }
}
