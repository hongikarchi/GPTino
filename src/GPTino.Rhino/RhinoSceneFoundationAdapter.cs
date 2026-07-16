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

            var attributes = ParseAttributes(request.AttributesJson, existing?.Attributes);
            try
            {
                attributes.SetUserString(LogicalEntityKey, request.LogicalEntityId);
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
