// SPDX-License-Identifier: Apache-2.0
// Behavioral reimplementation informed by Cordyceps; see THIRD_PARTY_NOTICES.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using GPTino.CordycepsAdapter;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace GPTino.Grasshopper;

/// <summary>
/// Document-bound adapter for deterministic canvas inspection, catalog search, creation,
/// deletion, movement, wiring, and group membership updates with Rhino/Grasshopper undo support.
/// </summary>
public sealed class GrasshopperCanvasFoundationAdapter : DocumentBoundCanvasAdapter<GH_Document>
{
    public GrasshopperCanvasFoundationAdapter(ExplicitGrasshopperDocumentResolver resolver)
        : base(resolver)
    {
    }

    protected override Task<CanvasSnapshot> CaptureSnapshotCoreAsync(
        GH_Document document,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parameterOwners = BuildParameterOwners(document);
        var objects = document.Objects
            .Select(documentObject => ToObjectState(documentObject, parameterOwners))
            .OrderBy(state => state.ObjectId)
            .ToArray();
        var wires = parameterOwners.Keys
            .SelectMany(target => target.Sources.Select(source => new WireState(
                parameterOwners.TryGetValue(source, out var sourceOwner) ? sourceOwner : source.InstanceGuid,
                source.InstanceGuid,
                parameterOwners[target],
                target.InstanceGuid)))
            .OrderBy(wire => wire.SourceObjectId)
            .ThenBy(wire => wire.SourceParameterId)
            .ThenBy(wire => wire.TargetObjectId)
            .ThenBy(wire => wire.TargetParameterId)
            .ToArray();
        var groups = document.Objects
            .OfType<GH_Group>()
            .Select(group => new GroupState(
                group.InstanceGuid,
                group.NickName,
                group.ObjectIDs.OrderBy(id => id).ToArray(),
                group.Colour.ToArgb()))
            .OrderBy(group => group.GroupId)
            .ToArray();
        var fingerprint = ComputeDocumentFingerprint(objects, wires, groups);
        return Task.FromResult(new CanvasSnapshot(
            document.DocumentID,
            fingerprint,
            objects,
            wires,
            groups));
    }

    protected override Task<CanvasObjectState> InspectObjectCoreAsync(
        GH_Document document,
        Guid objectId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var documentObject = document.FindObject(objectId, true)
            ?? throw new KeyNotFoundException($"Grasshopper object {objectId:D} was not found.");
        return Task.FromResult(ToObjectState(documentObject, BuildParameterOwners(document)));
    }

    protected override Task<CanvasOutputInspection> InspectOutputsCoreAsync(
        GH_Document document,
        InspectCanvasOutputsRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (request.ObjectId == Guid.Empty)
        {
            throw new InvalidOperationException("ObjectId is required for output inspection.");
        }

        var documentObject = document.FindObject(request.ObjectId, true)
            ?? throw new KeyNotFoundException(
                $"Grasshopper object {request.ObjectId:D} was not found.");
        if (documentObject is not IGH_Component component)
        {
            throw new NotSupportedException(
                $"Grasshopper object {request.ObjectId:D} does not expose component outputs.");
        }

        var outputs = component.Params.Output
            .Select(parameter => InspectOutputParameter(parameter, cancellationToken))
            .ToArray();
        var canonical = JsonSerializer.Serialize(outputs);
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{document.DocumentID:N}|{request.ObjectId:N}|{canonical}")))
            .ToLowerInvariant();
        return Task.FromResult(new CanvasOutputInspection(
            document.DocumentID,
            request.ObjectId,
            outputs,
            fingerprint));
    }

    protected override Task<ComponentCatalogSearchResult> SearchComponentCatalogCoreAsync(
        GH_Document document,
        ComponentCatalogSearchRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (request.Limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Limit,
                "Component catalog limit must be between 1 and 100.");
        }

        var query = request.Query?.Trim() ?? string.Empty;
        var matches = global::Grasshopper.Instances.ComponentServer.ObjectProxies
            .Where(proxy => proxy.Guid != Guid.Empty && (request.IncludeObsolete || !proxy.Obsolete))
            .Select(proxy => new CatalogCandidate(proxy, CatalogScore(proxy, query)))
            .Where(candidate => candidate.Score is not null)
            .GroupBy(candidate => candidate.Proxy.Guid)
            .Select(group => group
                .OrderBy(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Proxy.Desc.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Proxy.Desc.Category, StringComparer.OrdinalIgnoreCase)
                .First())
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Proxy.Obsolete)
            .ThenBy(candidate => candidate.Proxy.Desc.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Proxy.Desc.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Proxy.Desc.SubCategory, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Proxy.Guid)
            .Take(request.Limit)
            .Select(candidate => new CanvasComponentCatalogItem(
                candidate.Proxy.Guid,
                candidate.Proxy.Desc.Name ?? string.Empty,
                candidate.Proxy.Desc.NickName ?? string.Empty,
                candidate.Proxy.Desc.Category ?? string.Empty,
                candidate.Proxy.Desc.SubCategory ?? string.Empty,
                candidate.Proxy.Desc.Description ?? string.Empty,
                candidate.Proxy.Exposure.ToString(),
                candidate.Proxy.Obsolete))
            .ToArray();

        return Task.FromResult(new ComponentCatalogSearchResult(
            document.DocumentID,
            query,
            request.Limit,
            matches));
    }

    protected override Task<CanvasMutationResult> CreateObjectCoreAsync(
        GH_Document document,
        CreateCanvasObjectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOperationId(request.OperationId);
        if (request.ComponentTypeId == Guid.Empty)
        {
            throw new InvalidOperationException("ComponentTypeId is required.");
        }
        RequireFinite(request.Pivot, "Pivot");
        if (request.ObjectId != Guid.Empty && document.FindObject(request.ObjectId, true) is { } existing)
        {
            var state = ToObjectState(existing, BuildParameterOwners(document));
            if (state.ComponentTypeId != request.ComponentTypeId)
            {
                throw new InvalidOperationException(
                    $"Object {request.ObjectId:D} already exists with another component type.");
            }
            return Task.FromResult(new CanvasMutationResult(
                request.OperationId,
                Changed: false,
                state.Fingerprint,
                state.Fingerprint,
                new[] { request.ObjectId }));
        }

        var documentObject = global::Grasshopper.Instances.ComponentServer.EmitObject(request.ComponentTypeId)
            ?? throw new KeyNotFoundException(
                $"Grasshopper component type {request.ComponentTypeId:D} is not installed.");
        if (request.ObjectId != Guid.Empty)
        {
            documentObject.NewInstanceGuid(request.ObjectId);
            if (documentObject.InstanceGuid != request.ObjectId)
            {
                throw new InvalidOperationException("Grasshopper did not accept the requested object identity.");
            }
        }
        if (!string.IsNullOrWhiteSpace(request.NickName))
        {
            documentObject.NickName = request.NickName.Trim();
        }
        documentObject.Attributes.Pivot = new System.Drawing.PointF(request.Pivot.X, request.Pivot.Y);
        document.UndoUtil.RecordAddObjectEvent($"GPTino: {request.OperationId}", documentObject);
        if (!document.AddObject(documentObject, update: true))
        {
            throw new InvalidOperationException("Grasshopper rejected the new canvas object.");
        }
        if (request.ObjectId != Guid.Empty && documentObject.InstanceGuid != request.ObjectId)
        {
            document.RemoveObject(documentObject, update: false);
            throw new InvalidOperationException(
                "Grasshopper changed the requested object identity while adding it; the object was removed.");
        }
        var after = ToObjectState(documentObject, BuildParameterOwners(document));
        return Task.FromResult(new CanvasMutationResult(
            request.OperationId,
            Changed: true,
            string.Empty,
            after.Fingerprint,
            new[] { documentObject.InstanceGuid }));
    }

    protected override Task<CanvasMutationResult> DeleteObjectCoreAsync(
        GH_Document document,
        DeleteCanvasObjectRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOperationId(request.OperationId);
        if (request.ObjectId == Guid.Empty || string.IsNullOrWhiteSpace(request.ExpectedFingerprint))
        {
            throw new InvalidOperationException("ObjectId and ExpectedFingerprint are required for deletion.");
        }
        var documentObject = document.FindObject(request.ObjectId, true)
            ?? throw new KeyNotFoundException($"Grasshopper object {request.ObjectId:D} was not found.");
        var before = ToObjectState(documentObject, BuildParameterOwners(document)).Fingerprint;
        if (!string.Equals(before, request.ExpectedFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Canvas object changed after the request snapshot.");
        }

        document.UndoUtil.RecordRemoveObjectEvent($"GPTino: {request.OperationId}", documentObject);
        if (!document.RemoveObject(documentObject, true))
        {
            throw new InvalidOperationException($"Grasshopper could not remove object {request.ObjectId:D}.");
        }
        return Task.FromResult(new CanvasMutationResult(
            request.OperationId,
            Changed: true,
            before,
            string.Empty,
            new[] { request.ObjectId }));
    }

    protected override async Task<CanvasMutationResult> MoveObjectsCoreAsync(
        GH_Document document,
        MoveCanvasObjectsRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOperationId(request.OperationId);
        ArgumentNullException.ThrowIfNull(request.Pivots);
        ArgumentNullException.ThrowIfNull(request.ExpectedFingerprints);
        if (request.Pivots.Count == 0)
        {
            throw new InvalidOperationException("At least one object pivot is required.");
        }
        if (request.Pivots.Keys.Any(id => id == Guid.Empty) ||
            request.Pivots.Count != request.ExpectedFingerprints.Count ||
            request.Pivots.Keys.Any(id => !request.ExpectedFingerprints.ContainsKey(id)) ||
            request.ExpectedFingerprints.Keys.Any(id => !request.Pivots.ContainsKey(id)))
        {
            throw new InvalidOperationException(
                "Pivots and ExpectedFingerprints must contain the same non-empty object IDs.");
        }

        var beforeSnapshot = await CaptureSnapshotCoreAsync(document, cancellationToken).ConfigureAwait(false);
        // Resolve and fingerprint the complete batch before recording undo or changing a pivot.
        // A stale final item therefore cannot leave the earlier items partially moved.
        var prepared = request.Pivots
            .Select(pair =>
            {
                RequireFinite(pair.Value, $"Pivot for {pair.Key:D}");
                var documentObject = document.FindObject(pair.Key, true)
                    ?? throw new KeyNotFoundException($"Grasshopper object {pair.Key:D} was not found.");
                var state = ToObjectState(documentObject, BuildParameterOwners(document));
                var expected = request.ExpectedFingerprints[pair.Key];
                if (string.IsNullOrWhiteSpace(expected) ||
                    !string.Equals(state.Fingerprint, expected, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Canvas object {pair.Key:D} changed after the request snapshot.");
                }
                return new PreparedMove(
                    pair.Key,
                    documentObject,
                    documentObject.Attributes.Pivot,
                    new System.Drawing.PointF(pair.Value.X, pair.Value.Y));
            })
            .ToArray();
        var changes = prepared
            .Where(item => item.DocumentObject.Attributes.Pivot != item.Pivot)
            .ToArray();
        if (changes.Length > 0)
        {
            document.UndoUtil.RecordPivotEvent(
                $"GPTino: {request.OperationId}",
                changes.Select(item => item.DocumentObject).ToArray());
            try
            {
                foreach (var change in changes)
                {
                    change.DocumentObject.Attributes.Pivot = change.Pivot;
                }
            }
            catch (Exception mutationFailure)
            {
                try
                {
                    foreach (var change in changes)
                    {
                        change.DocumentObject.Attributes.Pivot = change.OriginalPivot;
                    }
                }
                catch (Exception rollbackFailure)
                {
                    throw new AggregateException(
                        "Canvas move failed and its in-place rollback also failed; use Grasshopper Undo.",
                        mutationFailure,
                        rollbackFailure);
                }
                throw;
            }
        }

        var afterSnapshot = await CaptureSnapshotCoreAsync(document, cancellationToken).ConfigureAwait(false);
        return new CanvasMutationResult(
            request.OperationId,
            changes.Length > 0,
            beforeSnapshot.DocumentFingerprint,
            afterSnapshot.DocumentFingerprint,
            changes.Select(item => item.ObjectId).ToArray());
    }

    protected override Task<CanvasMutationResult> SetNumberSliderValueCoreAsync(
        GH_Document document,
        SetNumberSliderValueRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOperationId(request.OperationId);
        if (request.ObjectId == Guid.Empty || string.IsNullOrWhiteSpace(request.ExpectedFingerprint) ||
            request.Minimum >= request.Maximum || request.Value < request.Minimum ||
            request.Value > request.Maximum || request.DecimalPlaces is < 0 or > 12 ||
            !IsRepresentableAtPrecision(request.Value, request.DecimalPlaces) ||
            !IsRepresentableAtPrecision(request.Minimum, request.DecimalPlaces) ||
            !IsRepresentableAtPrecision(request.Maximum, request.DecimalPlaces))
        {
            throw new InvalidOperationException("The Number Slider value request is invalid.");
        }

        var slider = document.FindObject(request.ObjectId, true) as GH_NumberSlider
            ?? throw new InvalidOperationException(
                $"Grasshopper object {request.ObjectId:D} is not a Number Slider.");
        var beforeState = ToObjectState(slider, BuildParameterOwners(document));
        if (!string.Equals(beforeState.Fingerprint, request.ExpectedFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Number Slider changed after the request snapshot.");
        }

        var oldMinimum = slider.Slider.Minimum;
        var oldMaximum = slider.Slider.Maximum;
        var oldValue = slider.CurrentValue;
        var oldDecimalPlaces = slider.Slider.DecimalPlaces;
        if (oldMinimum == request.Minimum && oldMaximum == request.Maximum &&
            oldValue == request.Value && oldDecimalPlaces == request.DecimalPlaces)
        {
            return Task.FromResult(new CanvasMutationResult(
                request.OperationId,
                Changed: false,
                beforeState.Fingerprint,
                beforeState.Fingerprint,
                [request.ObjectId]));
        }

        document.UndoUtil.RecordGenericObjectEvent($"GPTino: {request.OperationId}", slider);
        try
        {
            SetSliderRangeAndValue(
                slider,
                request.Minimum,
                request.Maximum,
                request.Value,
                request.DecimalPlaces);
            slider.ExpireSolution(true);
            if (slider.Slider.Minimum != request.Minimum ||
                slider.Slider.Maximum != request.Maximum ||
                slider.CurrentValue != request.Value ||
                slider.Slider.DecimalPlaces != request.DecimalPlaces)
            {
                throw new InvalidOperationException(
                    "Grasshopper did not apply the Number Slider state exactly as requested.");
            }
        }
        catch (Exception mutationFailure)
        {
            try
            {
                SetSliderRangeAndValue(slider, oldMinimum, oldMaximum, oldValue, oldDecimalPlaces);
                slider.ExpireSolution(true);
            }
            catch (Exception rollbackFailure)
            {
                throw new AggregateException(
                    "Number Slider mutation failed and its in-place rollback also failed; use Grasshopper Undo.",
                    mutationFailure,
                    rollbackFailure);
            }
            throw;
        }

        var afterState = ToObjectState(slider, BuildParameterOwners(document));
        return Task.FromResult(new CanvasMutationResult(
            request.OperationId,
            Changed: true,
            beforeState.Fingerprint,
            afterState.Fingerprint,
            [request.ObjectId]));
    }

    protected override async Task<CanvasMutationResult> SetWireCoreAsync(
        GH_Document document,
        SetWireRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOperationId(request.OperationId);
        if (request.Wire.SourceObjectId == Guid.Empty ||
            request.Wire.SourceParameterId == Guid.Empty ||
            request.Wire.TargetObjectId == Guid.Empty ||
            request.Wire.TargetParameterId == Guid.Empty)
        {
            throw new InvalidOperationException("Wire object and parameter IDs are required.");
        }
        var beforeSnapshot = await CaptureSnapshotCoreAsync(document, cancellationToken).ConfigureAwait(false);
        var source = ResolveParameter(
            document,
            request.Wire.SourceObjectId,
            request.Wire.SourceParameterId,
            source: true);
        var target = ResolveParameter(
            document,
            request.Wire.TargetObjectId,
            request.Wire.TargetParameterId,
            source: false);
        var connected = target.Sources.Any(candidate => candidate.InstanceGuid == source.InstanceGuid);
        var changed = false;

        if (request.Action == WireAction.Connect && !connected)
        {
            if (request.RejectCycles && WouldCreateCycle(
                    document,
                    request.Wire.SourceObjectId,
                    request.Wire.TargetObjectId))
            {
                throw new InvalidOperationException("Wire would create a Grasshopper dependency cycle.");
            }

            document.UndoUtil.RecordWireEvent($"GPTino: {request.OperationId}", target);
            target.AddSource(source);
            changed = true;
        }
        else if (request.Action == WireAction.Disconnect && connected)
        {
            document.UndoUtil.RecordWireEvent($"GPTino: {request.OperationId}", target);
            target.RemoveSource(source);
            changed = true;
        }

        if (changed)
        {
            document.NewSolution(false);
        }

        var afterSnapshot = await CaptureSnapshotCoreAsync(document, cancellationToken).ConfigureAwait(false);
        return new CanvasMutationResult(
            request.OperationId,
            changed,
            beforeSnapshot.DocumentFingerprint,
            afterSnapshot.DocumentFingerprint,
            new[] { request.Wire.SourceObjectId, request.Wire.TargetObjectId });
    }

    protected override Task<CanvasMutationResult> SetGroupCoreAsync(
        GH_Document document,
        SetGroupRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireOperationId(request.OperationId);
        ArgumentNullException.ThrowIfNull(request.ObjectIds);
        if (request.GroupId == Guid.Empty)
        {
            throw new InvalidOperationException("GroupId is required so group identity can be verified.");
        }
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidOperationException("Group name is required.");
        }
        var objectIds = request.ObjectIds.Distinct().ToArray();
        if (objectIds.Any(id => id == Guid.Empty || id == request.GroupId))
        {
            throw new InvalidOperationException("A group cannot contain an empty ID or itself.");
        }
        foreach (var objectId in objectIds)
        {
            _ = document.FindObject(objectId, true)
                ?? throw new KeyNotFoundException($"Grasshopper object {objectId:D} was not found.");
        }

        var objectAtGroupId = document.FindObject(request.GroupId, true);
        if (objectAtGroupId is not null && objectAtGroupId is not GH_Group)
        {
            throw new InvalidOperationException(
                $"Canvas object {request.GroupId:D} already exists and is not a group.");
        }
        var group = objectAtGroupId as GH_Group;
        var before = group is null ? string.Empty : GroupFingerprint(group);
        if (group is null)
        {
            group = new GH_Group();
            group.NewInstanceGuid(request.GroupId);
            if (group.InstanceGuid != request.GroupId)
            {
                throw new InvalidOperationException("Grasshopper did not accept the requested group identity.");
            }
            document.UndoUtil.RecordAddObjectEvent($"GPTino: {request.OperationId}", group);
            if (!document.AddObject(group, update: false) || group.InstanceGuid != request.GroupId)
            {
                if (document.FindObject(group.InstanceGuid, true) is not null)
                {
                    document.RemoveObject(group, update: false);
                }
                throw new InvalidOperationException("Grasshopper could not create the requested group safely.");
            }
        }
        else
        {
            document.UndoUtil.RecordGenericObjectEvent($"GPTino: {request.OperationId}", group);
        }

        group.NickName = request.Name;
        group.Colour = System.Drawing.Color.FromArgb(request.ArgbColor);
        foreach (var objectId in group.ObjectIDs.ToArray())
        {
            group.RemoveObject(objectId);
        }
        foreach (var objectId in objectIds)
        {
            group.AddObject(objectId);
        }
        group.Attributes.ExpireLayout();
        var after = GroupFingerprint(group);
        return Task.FromResult(new CanvasMutationResult(
            request.OperationId,
            !string.Equals(before, after, StringComparison.Ordinal),
            before,
            after,
            objectIds.Append(group.InstanceGuid).ToArray()));
    }

    private static CanvasObjectState ToObjectState(
        IGH_DocumentObject documentObject,
        IReadOnlyDictionary<IGH_Param, Guid> parameterOwners)
    {
        var pivot = documentObject.Attributes.Pivot;
        var bounds = documentObject.Attributes.Bounds;
        var inputs = ParametersFor(documentObject, CanvasParameterDirection.Input)
            .Select(parameter => ToParameterState(
                documentObject.InstanceGuid,
                parameter,
                CanvasParameterDirection.Input,
                parameterOwners))
            .OrderBy(parameter => parameter.ParameterId)
            .ToArray();
        var outputs = ParametersFor(documentObject, CanvasParameterDirection.Output)
            .Select(parameter => ToParameterState(
                documentObject.InstanceGuid,
                parameter,
                CanvasParameterDirection.Output,
                parameterOwners))
            .OrderBy(parameter => parameter.ParameterId)
            .ToArray();
        var sockets = string.Join('|', inputs.Concat(outputs).Select(parameter =>
            $"{parameter.Direction}:{parameter.ParameterId:N}:{parameter.Name}:{parameter.NickName}:" +
            $"{parameter.TypeName}:{parameter.TypeHint}:{parameter.Access}:{parameter.Optional}:" +
            string.Join(',', parameter.CurrentSources.Select(source =>
                $"{source.OwnerObjectId:N}/{source.ParameterId:N}"))));
        var valueJson = documentObject is GH_NumberSlider slider
            ? JsonSerializer.Serialize(new
            {
                kind = "numberSlider",
                value = slider.CurrentValue,
                minimum = slider.Slider.Minimum,
                maximum = slider.Slider.Maximum,
                decimalPlaces = slider.Slider.DecimalPlaces
            })
            : null;
        var fingerprintSource = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{documentObject.InstanceGuid:N}|{documentObject.ComponentGuid:N}|{pivot.X:R}|{pivot.Y:R}|{bounds.Width:R}|{bounds.Height:R}|{documentObject.NickName}|{sockets}|{valueJson}");
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource))).ToLowerInvariant();
        return new CanvasObjectState(
            documentObject.InstanceGuid,
            documentObject.ComponentGuid,
            documentObject.NickName,
            new CanvasPoint(pivot.X, pivot.Y),
            new CanvasSize(bounds.Width, bounds.Height),
            fingerprint)
        {
            Inputs = inputs,
            Outputs = outputs,
            ValueJson = valueJson,
        };
    }

    private static void SetSliderRangeAndValue(
        GH_NumberSlider slider,
        decimal minimum,
        decimal maximum,
        decimal value,
        int decimalPlaces)
    {
        slider.Slider.Minimum = Math.Min(slider.Slider.Minimum, Math.Min(minimum, value));
        slider.Slider.Maximum = Math.Max(slider.Slider.Maximum, Math.Max(maximum, value));
        slider.Slider.Minimum = minimum;
        slider.Slider.Maximum = maximum;
        slider.Slider.DecimalPlaces = decimalPlaces;
        slider.SetSliderValue(value);
    }

    private static bool IsRepresentableAtPrecision(decimal value, int decimalPlaces) =>
        decimal.Round(value, decimalPlaces) == value;

    private static CanvasOutputParameterInspection InspectOutputParameter(
        IGH_Param parameter,
        CancellationToken cancellationToken)
    {
        var count = 0;
        var typeNames = new HashSet<string>(StringComparer.Ordinal);
        Rhino.Geometry.BoundingBox? bounds = null;
        foreach (var goo in parameter.VolatileData.AllData(true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
            if (goo is null)
            {
                typeNames.Add("null");
                continue;
            }

            typeNames.Add(goo.GetType().FullName ?? goo.GetType().Name);
            if (goo.ScriptVariable() is not Rhino.Geometry.GeometryBase geometry)
            {
                continue;
            }
            var candidate = geometry.GetBoundingBox(accurate: true);
            if (!candidate.IsValid)
            {
                continue;
            }
            if (bounds is null)
            {
                bounds = candidate;
            }
            else
            {
                var union = bounds.Value;
                union.Union(candidate);
                bounds = union;
            }
        }

        return new CanvasOutputParameterInspection(
            parameter.InstanceGuid,
            parameter.Name ?? string.Empty,
            parameter.NickName ?? string.Empty,
            count,
            typeNames.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            bounds is { } value ? ToCanvasBounds(value) : null);
    }

    private static CanvasBoundingBox3d ToCanvasBounds(Rhino.Geometry.BoundingBox bounds) =>
        new(
            new CanvasPoint3d(bounds.Min.X, bounds.Min.Y, bounds.Min.Z),
            new CanvasPoint3d(bounds.Max.X, bounds.Max.Y, bounds.Max.Z),
            new CanvasPoint3d(
                bounds.Max.X - bounds.Min.X,
                bounds.Max.Y - bounds.Min.Y,
                bounds.Max.Z - bounds.Min.Z));

    private static IReadOnlyDictionary<IGH_Param, Guid> BuildParameterOwners(GH_Document document) =>
        EnumerateParameters(document)
            .ToDictionary(
                pair => pair.Parameter,
                pair => pair.OwnerId,
                ParameterReferenceComparer.Instance);

    private static IEnumerable<IGH_Param> ParametersFor(
        IGH_DocumentObject documentObject,
        CanvasParameterDirection direction) => documentObject switch
        {
            IGH_Component component when direction == CanvasParameterDirection.Input =>
                component.Params.Input,
            IGH_Component component => component.Params.Output,
            IGH_Param standalone => new[] { standalone },
            _ => Enumerable.Empty<IGH_Param>(),
        };

    private static CanvasParameterState ToParameterState(
        Guid ownerObjectId,
        IGH_Param parameter,
        CanvasParameterDirection direction,
        IReadOnlyDictionary<IGH_Param, Guid> parameterOwners)
    {
        var sources = parameter.Sources
            .Select(source => new CanvasParameterEndpoint(
                parameterOwners.TryGetValue(source, out var sourceOwner)
                    ? sourceOwner
                    : source.InstanceGuid,
                source.InstanceGuid))
            .OrderBy(source => source.OwnerObjectId)
            .ThenBy(source => source.ParameterId)
            .ToArray();
        return new CanvasParameterState(
            ownerObjectId,
            parameter.InstanceGuid,
            parameter.Name ?? string.Empty,
            parameter.NickName ?? string.Empty,
            direction,
            parameter.TypeName ?? parameter.GetType().FullName ?? parameter.GetType().Name,
            ReadTypeHint(parameter),
            parameter.Access switch
            {
                GH_ParamAccess.list => CanvasParameterAccess.List,
                GH_ParamAccess.tree => CanvasParameterAccess.Tree,
                _ => CanvasParameterAccess.Item,
            },
            parameter.Optional,
            sources);
    }

    private static string? ReadTypeHint(IGH_Param parameter)
    {
        try
        {
            var scriptParameter = parameter.GetType().GetInterfaces().FirstOrDefault(type =>
                string.Equals(
                    type.FullName,
                    "RhinoCodePlatform.GH.IScriptParameter",
                    StringComparison.Ordinal));
            if (scriptParameter is not null)
            {
                var converter = scriptParameter.GetProperty("Converter")?.GetValue(parameter);
                if (converter is null)
                {
                    return "object";
                }

                var converterTypeName = converter.GetType().GetProperty("TypeName")
                    ?.GetValue(converter)?.ToString();
                if (!string.IsNullOrWhiteSpace(converterTypeName))
                {
                    return converterTypeName;
                }

                var target = converter.GetType().GetProperty("TargetType")?.GetValue(converter);
                if (target is Type targetType)
                {
                    return targetType.FullName ?? targetType.Name;
                }
            }

            var legacyHint = parameter.GetType().GetProperty("TypeHint")?.GetValue(parameter);
            if (legacyHint is null)
            {
                return null;
            }

            return legacyHint.GetType().GetProperty("TypeName")?.GetValue(legacyHint)?.ToString()
                ?? legacyHint.ToString();
        }
        catch
        {
            // Third-party parameter metadata must not make canvas inspection unavailable.
            return null;
        }
    }

    private static int? CatalogScore(IGH_ObjectProxy proxy, string query)
    {
        if (query.Length == 0)
        {
            return 5;
        }

        var description = proxy.Desc;
        if (EqualsQuery(description.Name, query))
        {
            return 0;
        }
        if (EqualsQuery(description.NickName, query))
        {
            return 1;
        }
        if (StartsWithQuery(description.Name, query) || StartsWithQuery(description.NickName, query))
        {
            return 2;
        }
        if (ContainsQuery(description.Name, query) || ContainsQuery(description.NickName, query))
        {
            return 3;
        }
        if (EqualsQuery(description.Category, query) ||
            EqualsQuery(description.SubCategory, query))
        {
            return 4;
        }

        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var searchable = string.Join(' ', new[]
        {
            description.Name,
            description.NickName,
            description.Category,
            description.SubCategory,
            description.Description,
        });
        return tokens.All(token => ContainsQuery(searchable, token)) ? 5 : null;
    }

    private static bool EqualsQuery(string? value, string query) =>
        string.Equals(value, query, StringComparison.OrdinalIgnoreCase);

    private static bool StartsWithQuery(string? value, string query) =>
        value?.StartsWith(query, StringComparison.OrdinalIgnoreCase) == true;

    private static bool ContainsQuery(string? value, string query) =>
        value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;

    private static IEnumerable<(Guid OwnerId, IGH_Param Parameter)> EnumerateParameters(
        GH_Document document)
    {
        foreach (var documentObject in document.Objects)
        {
            if (documentObject is IGH_Param standaloneParameter)
            {
                yield return (documentObject.InstanceGuid, standaloneParameter);
            }

            if (documentObject is not IGH_Component component)
            {
                continue;
            }

            foreach (var parameter in component.Params.Input)
            {
                yield return (documentObject.InstanceGuid, parameter);
            }

            foreach (var parameter in component.Params.Output)
            {
                yield return (documentObject.InstanceGuid, parameter);
            }
        }
    }

    private static IGH_Param ResolveParameter(
        GH_Document document,
        Guid ownerId,
        Guid parameterId,
        bool source)
    {
        var owner = document.FindObject(ownerId, true)
            ?? throw new KeyNotFoundException($"Grasshopper object {ownerId:D} was not found.");
        IGH_Param? parameter = owner switch
        {
            IGH_Component component when source => component.Params.Output
                .FirstOrDefault(item => item.InstanceGuid == parameterId),
            IGH_Component component => component.Params.Input
                .FirstOrDefault(item => item.InstanceGuid == parameterId),
            IGH_Param standalone when standalone.InstanceGuid == parameterId => standalone,
            _ => null,
        };
        return parameter ?? throw new KeyNotFoundException(
            $"Grasshopper {(source ? "source" : "target")} parameter {parameterId:D} " +
            $"on object {ownerId:D} was not found in the required direction.");
    }

    private static bool WouldCreateCycle(
        GH_Document document,
        Guid sourceObjectId,
        Guid targetObjectId)
    {
        if (sourceObjectId == targetObjectId)
        {
            return true;
        }

        var ownerByParameter = EnumerateParameters(document)
            .ToDictionary(item => item.Parameter.InstanceGuid, item => item.OwnerId);
        var visited = new HashSet<Guid>();
        var pending = new Stack<Guid>();
        pending.Push(sourceObjectId);
        while (pending.Count > 0)
        {
            var currentId = pending.Pop();
            if (!visited.Add(currentId))
            {
                continue;
            }

            if (currentId == targetObjectId)
            {
                return true;
            }

            var currentObject = document.FindObject(currentId, true);
            var inputs = currentObject switch
            {
                IGH_Component component => component.Params.Input.AsEnumerable(),
                IGH_Param parameter => new[] { parameter },
                _ => Enumerable.Empty<IGH_Param>()
            };
            foreach (var upstream in inputs.SelectMany(input => input.Sources))
            {
                if (ownerByParameter.TryGetValue(upstream.InstanceGuid, out var upstreamOwner))
                {
                    pending.Push(upstreamOwner);
                }
            }
        }

        return false;
    }

    private static string GroupFingerprint(GH_Group group)
    {
        var value = $"{group.InstanceGuid:N}|{group.NickName}|{group.Colour.ToArgb()}|" +
            string.Join(',', group.ObjectIDs.OrderBy(id => id));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private static string ComputeDocumentFingerprint(
        IReadOnlyList<CanvasObjectState> objects,
        IReadOnlyList<WireState> wires,
        IReadOnlyList<GroupState> groups)
    {
        var builder = new StringBuilder();
        foreach (var item in objects)
        {
            builder.Append(item.ObjectId.ToString("N")).Append(':').AppendLine(item.Fingerprint);
        }

        foreach (var wire in wires)
        {
            builder.AppendLine(FormattableString.Invariant(
                $"{wire.SourceObjectId:N}/{wire.SourceParameterId:N}>{wire.TargetObjectId:N}/{wire.TargetParameterId:N}"));
        }

        foreach (var group in groups)
        {
            builder.Append(group.GroupId.ToString("N")).Append(':').Append(group.Name).Append(':')
                .Append(group.ArgbColor).Append(':')
                .AppendLine(string.Join(',', group.ObjectIds.OrderBy(id => id)));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant();
    }

    private sealed class ParameterReferenceComparer : IEqualityComparer<IGH_Param>
    {
        public static ParameterReferenceComparer Instance { get; } = new();

        public bool Equals(IGH_Param? x, IGH_Param? y) => ReferenceEquals(x, y);

        public int GetHashCode(IGH_Param obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private static void RequireOperationId(string operationId)
    {
        if (string.IsNullOrWhiteSpace(operationId))
        {
            throw new InvalidOperationException("OperationId is required.");
        }
    }

    private static void RequireFinite(CanvasPoint point, string name)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
        {
            throw new InvalidOperationException($"{name} coordinates must be finite.");
        }
    }

    private sealed record PreparedMove(
        Guid ObjectId,
        IGH_DocumentObject DocumentObject,
        System.Drawing.PointF OriginalPivot,
        System.Drawing.PointF Pivot);

    private sealed record CatalogCandidate(
        IGH_ObjectProxy Proxy,
        int? Score);
}
