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
using GPTino.CordycepsAdapter;
using GPTino.Core;
using GPTino.History;
using GPTino.WireifyAdapter;

namespace GPTino.AgentHost.Runtime;

// Typed-operation routing and argument validation: owner/bridge-operation resolution and per-operation payload shape rules.
public sealed partial class LiveDocumentBackend
{
    private static BridgeAdapterOwner ResolveOwner(TypedOperation operation)
    {
        var expected = operation.Kind switch
        {
            OperationKind.UpdatePythonSource or OperationKind.SetComponentIo or
                OperationKind.ConvertSocket or OperationKind.ExecutePython or
                OperationKind.ReadRuntimeMessages => AdapterOwner.Wireify,
            _ when IsRhinoOperation(operation.Kind) => AdapterOwner.RhinoBridge,
            OperationKind.Read => operation.Owner,
            _ => AdapterOwner.Cordyceps
        };
        if (operation.Owner != expected)
        {
            throw new InvalidOperationException(
                $"Operation kind '{operation.Kind}' belongs to owner '{expected}', not '{operation.Owner}'.");
        }
        return operation.Owner switch
        {
            AdapterOwner.Wireify => BridgeAdapterOwner.Wireify,
            AdapterOwner.Cordyceps => BridgeAdapterOwner.CordycepsCanvas,
            AdapterOwner.RhinoBridge => BridgeAdapterOwner.CordycepsRhino,
            _ => throw new InvalidOperationException($"Unsupported adapter owner '{operation.Owner}'.")
        };
    }

    private static bool IsRhinoOperation(OperationKind kind) => kind is
        OperationKind.CreateRhinoPrimitive or OperationKind.TransformRhinoObject or
        OperationKind.CreateRhinoObject or OperationKind.ModifyRhinoObject or
        OperationKind.DeleteRhinoObject or OperationKind.BakeGeometry or
        OperationKind.UpdateRhinoAttributes or OperationKind.UpdateRhinoLayer or
        OperationKind.FixRhinoEndpointPair or OperationKind.PurgeTableEntries or
        OperationKind.MoveObjectsToLayer or OperationKind.UpdateRhinoLayerProperties or
        OperationKind.DeleteRhinoLayer or OperationKind.SaveRhinoLayerState or
        OperationKind.EnsureRhinoLayer;

    private static string ResolveBridgeOperation(TypedOperation operation, JsonElement payload)
    {
        var inferred = operation.Kind switch
        {
            OperationKind.MoveComponent or OperationKind.SetLayout => "canvas.move",
            OperationKind.SetValue => "canvas.setNumberSlider",
            OperationKind.ConnectWire or OperationKind.DisconnectWire => "canvas.setWire",
            OperationKind.CreateComponent => "canvas.create",
            OperationKind.ReferenceRhinoObjects => "canvas.referenceRhinoObjects",
            OperationKind.DeleteComponent => "canvas.delete",
            OperationKind.SetGroup => "canvas.setGroup",
            OperationKind.UpdatePythonSource => "python.setSource",
            OperationKind.SetComponentIo => "python.setSchema",
            OperationKind.ConvertSocket => "python.setTyping",
            OperationKind.ExecutePython => "python.execute",
            OperationKind.ReadRuntimeMessages => "python.runtimeMessages",
            OperationKind.CreateRhinoPrimitive => "rhino.createPrimitive",
            OperationKind.TransformRhinoObject => "rhino.transform",
            OperationKind.CreateRhinoObject or OperationKind.ModifyRhinoObject or
                OperationKind.BakeGeometry or OperationKind.UpdateRhinoAttributes => "rhino.upsert",
            OperationKind.DeleteRhinoObject => "rhino.delete",
            OperationKind.FixRhinoEndpointPair => "rhino.fixEndpointPair",
            OperationKind.PurgeTableEntries => "rhino.purgeTableEntries",
            OperationKind.MoveObjectsToLayer => "rhino.moveObjectsToLayer",
            OperationKind.UpdateRhinoLayerProperties => "rhino.updateLayer",
            OperationKind.DeleteRhinoLayer => "rhino.deleteLayer",
            OperationKind.SaveRhinoLayerState => "rhino.layerState",
            OperationKind.EnsureRhinoLayer => "rhino.ensureLayer",
            OperationKind.UpdateRhinoLayer => throw new InvalidOperationException(
                "UpdateRhinoLayer is reserved until deterministic layer inspection is available."),
            OperationKind.Read when operation.Owner == AdapterOwner.Wireify => "python.inspect",
            OperationKind.Read when operation.Owner == AdapterOwner.RhinoBridge => "rhino.inspect",
            OperationKind.Read => "canvas.inspect",
            _ => throw new InvalidOperationException(
                $"Operation kind '{operation.Kind}' has no safe bridge mapping.")
        };
        if (!payload.TryGetProperty("bridgeOperation", out var explicitElement) ||
            explicitElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(explicitElement.GetString()))
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' requires an explicit bridgeOperation.");
        }
        var explicitOperation = explicitElement.GetString();
        if (!string.Equals(explicitOperation, inferred, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Payload bridgeOperation '{explicitOperation}' does not match typed operation '{inferred}'.");
        }
        return inferred;
    }

    private static void ValidateOperationArguments(
        TypedOperation operation,
        string bridgeOperation,
        JsonElement arguments)
    {
        var required = bridgeOperation switch
        {
            "canvas.move" => new[] { "operationId", "pivots", "expectedFingerprints" },
            "canvas.setNumberSlider" => new[]
            {
                "operationId", "objectId", "expectedFingerprint", "value", "minimum", "maximum",
                "decimalPlaces"
            },
            "canvas.setWire" => new[] { "operationId", "wire", "action", "rejectCycles" },
            "canvas.create" => new[] { "operationId", "objectId", "componentTypeId", "pivot" },
            "canvas.referenceRhinoObjects" => new[] { "operationId", "objectId", "rhinoObjectIds", "paramType", "pivot" },
            "canvas.delete" => new[] { "operationId", "objectId", "expectedFingerprint" },
            "canvas.setGroup" => new[] { "operationId", "groupId", "name", "objectIds", "argbColor" },
            "python.setSource" => new[]
            {
                "operationId", "componentId", "expectedSourceSha256", "source", "runtime", "expireSolution"
            },
            "python.setSchema" => new[]
            {
                "operationId", "componentId", "inputs", "outputs", "preserveIncidentWires"
            },
            "python.setTyping" => new[]
            {
                "operationId", "componentId", "inputParameterId", "typeHint", "access"
            },
            "python.execute" => new[]
            {
                "operationId", "componentId", "expireUpstream", "recomputeDocument"
            },
            "python.runtimeMessages" or "python.inspect" => new[] { "componentId" },
            "canvas.inspect" or "rhino.inspect" => new[] { "objectId" },
            "rhino.createPrimitive" => new[]
            {
                "operationId", "objectId", "logicalEntityId", "kind"
            },
            "rhino.transform" => new[]
            {
                "operationId", "objectId", "expectedFingerprint", "matrix"
            },
            "rhino.upsert" => new[]
            {
                "operationId", "objectId", "logicalEntityId", "geometryType", "geometryJson",
                "attributesJson", "expectedFingerprint"
            },
            "rhino.delete" => new[] { "operationId", "objectId", "expectedFingerprint" },
            "rhino.fixEndpointPair" => new[]
            {
                "operationId", "anchorObjectId", "anchorEnd", "moveObjectId", "moveEnd",
                "expectedAnchorFingerprint", "expectedFingerprint", "tolerance"
            },
            "rhino.purgeTableEntries" => new[] { "operationId", "entries" },
            // layerId is required even for a brand-new layer: the caller picks the identity so the
            // writeSet can declare it with the absent sentinel before it exists.
            "rhino.ensureLayer" => new[] { "operationId", "layerId", "fullPath" },
            "rhino.moveObjectsToLayer" => new[] { "operationId", "items", "targetLayerId" },
            "rhino.updateLayer" => new[] { "operationId", "layerId", "expectedFingerprint" },
            "rhino.deleteLayer" => new[] { "operationId", "layerId", "expectedFingerprint" },
            "rhino.layerState" => new[] { "operationId", "action", "name" },
            _ => throw new InvalidOperationException(
                $"Bridge operation '{bridgeOperation}' is not supported by the preflight validator.")
        };
        foreach (var property in required)
        {
            var nullableCreateFingerprint =
                property == "expectedFingerprint" &&
                operation.Kind is OperationKind.CreateRhinoObject or OperationKind.BakeGeometry;
            if (!arguments.TryGetProperty(property, out var value) ||
                (value.ValueKind == JsonValueKind.Null && !nullableCreateFingerprint))
            {
                throw new InvalidOperationException(
                    $"Operation '{operation.OperationId}' payload is missing required argument '{property}'.");
            }
        }

        if (bridgeOperation == "rhino.upsert")
        {
            var expected = arguments.GetProperty("expectedFingerprint");
            var isCreate = operation.Kind is OperationKind.CreateRhinoObject or OperationKind.BakeGeometry;
            if (isCreate != (expected.ValueKind == JsonValueKind.Null))
            {
                throw new InvalidOperationException(
                    $"Operation '{operation.OperationId}' must use a null expectedFingerprint only for an exact Rhino create.");
            }
        }

        if (OperationSemantics.IsWrite(operation.Kind))
        {
            var payloadOperationId = RequireArgumentString(arguments, "operationId", operation.OperationId);
            if (!string.Equals(payloadOperationId, operation.OperationId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Typed operation id '{operation.OperationId}' does not match payload operationId '{payloadOperationId}'.");
            }
        }
        else if (arguments.TryGetProperty("operationId", out var optionalId) &&
            optionalId.ValueKind == JsonValueKind.String &&
            !string.Equals(optionalId.GetString(), operation.OperationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Typed operation id '{operation.OperationId}' does not match payload operationId '{optionalId.GetString()}'.");
        }

        foreach (var guidProperty in GuidArguments(bridgeOperation))
        {
            _ = RequireArgumentGuid(arguments, guidProperty, operation.OperationId);
        }
        ValidateDeserializableArguments(operation, bridgeOperation, arguments);
    }

    private static void ValidateDeserializableArguments(
        TypedOperation operation,
        string bridgeOperation,
        JsonElement arguments)
    {
        try
        {
            switch (bridgeOperation)
            {
                case "canvas.move":
                    ValidateCanvasPivotsShape(
                        arguments.GetProperty("pivots"),
                        operation.OperationId);
                    ValidateMoveArguments(
                        DeserializeArguments<MoveCanvasObjectsRequest>(arguments, operation.OperationId));
                    return;
                case "canvas.setNumberSlider":
                    var slider = DeserializeArguments<SetNumberSliderValueRequest>(
                        arguments,
                        operation.OperationId);
                    if (slider.ObjectId == Guid.Empty ||
                        string.IsNullOrWhiteSpace(slider.ExpectedFingerprint) ||
                        slider.Minimum >= slider.Maximum || slider.Value < slider.Minimum ||
                        slider.Value > slider.Maximum || slider.DecimalPlaces is < 0 or > 12 ||
                        decimal.Round(slider.Value, slider.DecimalPlaces) != slider.Value ||
                        decimal.Round(slider.Minimum, slider.DecimalPlaces) != slider.Minimum ||
                        decimal.Round(slider.Maximum, slider.DecimalPlaces) != slider.Maximum)
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' has an invalid Number Slider payload.");
                    }
                    return;
                case "canvas.setWire":
                    ValidateWireArguments(
                        DeserializeArguments<SetWireRequest>(arguments, operation.OperationId));
                    return;
                case "canvas.create":
                    ValidateCanvasCreateArguments(operation, arguments);
                    return;
                case "canvas.delete":
                    var delete = DeserializeArguments<DeleteCanvasObjectRequest>(arguments, operation.OperationId);
                    if (delete.ObjectId == Guid.Empty || string.IsNullOrWhiteSpace(delete.ExpectedFingerprint))
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' has an invalid canvas delete payload.");
                    }
                    return;
                case "canvas.setGroup":
                    var group = DeserializeArguments<SetGroupRequest>(arguments, operation.OperationId);
                    if (group.GroupId == Guid.Empty || string.IsNullOrWhiteSpace(group.Name) ||
                        group.ObjectIds is null || group.ObjectIds.Count == 0 ||
                        group.ObjectIds.Any(id => id == Guid.Empty) ||
                        group.ObjectIds.Distinct().Count() != group.ObjectIds.Count)
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' has an invalid canvas group payload.");
                    }
                    return;
                case "python.setSource":
                    var source = DeserializeArguments<SetPythonSourceRequest>(arguments, operation.OperationId);
                    if (source.ComponentId == Guid.Empty ||
                        string.IsNullOrWhiteSpace(source.ExpectedSourceSha256) || source.Source is null)
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' has an invalid Python source payload.");
                    }
                    return;
                case "python.setSchema":
                    ValidatePythonSchema(
                        DeserializeArguments<SetParameterSchemaRequest>(arguments, operation.OperationId),
                        operation.OperationId);
                    return;
                case "python.setTyping":
                    var typing = DeserializeArguments<SetInputTypingRequest>(arguments, operation.OperationId);
                    if (typing.ComponentId == Guid.Empty || typing.InputParameterId == Guid.Empty ||
                        string.IsNullOrWhiteSpace(typing.TypeHint))
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' has an invalid Python typing payload.");
                    }
                    return;
                case "python.execute":
                    if (DeserializeArguments<ExecutePythonComponentRequest>(arguments, operation.OperationId)
                        .ComponentId == Guid.Empty)
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' requires a Python component UUID.");
                    }
                    return;
                case "python.runtimeMessages":
                case "python.inspect":
                    RequireOnlyProperties(arguments, operation.OperationId, "componentId");
                    return;
                case "canvas.inspect":
                case "rhino.inspect":
                    RequireOnlyProperties(arguments, operation.OperationId, "objectId");
                    return;
                case "rhino.createPrimitive":
                    var primitive = DeserializeArguments<CreateRhinoPrimitiveRequest>(
                        arguments,
                        operation.OperationId);
                    ValidatePrimitiveCoordinateShapes(primitive, arguments, operation.OperationId);
                    ValidatePrimitiveArguments(primitive, operation.OperationId);
                    return;
                case "rhino.transform":
                    RequireOnlyProperties(
                        arguments.GetProperty("matrix"),
                        operation.OperationId,
                        "m00", "m01", "m02", "m03", "m10", "m11", "m12", "m13",
                        "m20", "m21", "m22", "m23", "m30", "m31", "m32", "m33");
                    ValidateTransformArguments(
                        DeserializeArguments<TransformRhinoObjectRequest>(arguments, operation.OperationId),
                        operation.OperationId);
                    return;
                case "rhino.upsert":
                    ValidateUpsertArguments(
                        DeserializeArguments<UpsertRhinoObjectRequest>(arguments, operation.OperationId),
                        operation.OperationId);
                    return;
                case "rhino.delete":
                    var rhinoDelete = DeserializeArguments<DeleteRhinoObjectRequest>(arguments, operation.OperationId);
                    RequireNotPreApproved(rhinoDelete.Approved, operation.OperationId);
                    if (rhinoDelete.ObjectId == Guid.Empty ||
                        string.IsNullOrWhiteSpace(rhinoDelete.ExpectedFingerprint))
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' has an invalid Rhino delete payload.");
                    }
                    return;
                case "rhino.fixEndpointPair":
                    ValidateFixEndpointPairArguments(
                        DeserializeArguments<FixEndpointPairRequest>(arguments, operation.OperationId),
                        operation.OperationId);
                    return;
                case "rhino.purgeTableEntries":
                    ValidatePurgeArguments(
                        DeserializeArguments<PurgeTableEntriesRequest>(arguments, operation.OperationId),
                        operation.OperationId);
                    return;
                case "rhino.moveObjectsToLayer":
                    ValidateMoveObjectsArguments(
                        DeserializeArguments<MoveObjectsToLayerRequest>(arguments, operation.OperationId),
                        operation.OperationId);
                    return;
                case "rhino.updateLayer":
                    var layerUpdate = DeserializeArguments<UpdateRhinoLayerRequest>(arguments, operation.OperationId);
                    if (layerUpdate.LayerId == Guid.Empty ||
                        string.IsNullOrWhiteSpace(layerUpdate.ExpectedFingerprint) ||
                        (layerUpdate.ArgbColor is null && layerUpdate.Visible is null && layerUpdate.Locked is null))
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' has an invalid Rhino layer-update payload " +
                            "(it must change at least one of color, visible, locked).");
                    }
                    return;
                case "rhino.deleteLayer":
                    var layerDelete = DeserializeArguments<DeleteRhinoLayerRequest>(arguments, operation.OperationId);
                    if (layerDelete.LayerId == Guid.Empty ||
                        string.IsNullOrWhiteSpace(layerDelete.ExpectedFingerprint))
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' has an invalid Rhino layer-delete payload.");
                    }
                    return;
                case "rhino.layerState":
                    var layerState = DeserializeArguments<RhinoLayerStateRequest>(arguments, operation.OperationId);
                    if (string.IsNullOrWhiteSpace(layerState.Name) ||
                        layerState.Action is not ("save" or "restore" or "delete"))
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' needs a layer-state name and action save|restore|delete.");
                    }
                    return;
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' payload does not match the typed bridge schema: " +
                exception.Message,
                exception);
        }
        catch (KeyNotFoundException exception)
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' payload is missing a required nested value.",
                exception);
        }
    }

    // canvas.create accepts either an explicit pivot:{x,y} (honored verbatim) OR the sentinel
    // pivot:"gptino:auto" with an optional sibling autoUpstream:[objectId,...] naming the
    // components/sliders that will feed the new one. The sentinel + autoUpstream cannot survive
    // strict CreateCanvasObjectRequest deserialization (BridgeProtocol.JsonOptions disallows
    // unmapped members and has no CanvasPoint case for a string), so the sentinel path is
    // hand-validated here; CanvasAutoPlacement.ResolveAutoPivots rewrites it into a concrete pivot
    // and strips autoUpstream just before bridge dispatch, so the adapter still sees today's shape.
    private static void ValidateCanvasCreateArguments(TypedOperation operation, JsonElement arguments)
    {
        var pivot = arguments.GetProperty("pivot");
        if (pivot.ValueKind == JsonValueKind.String)
        {
            if (!string.Equals(
                    pivot.GetString(),
                    CanvasAutoPlacement.AutoPivotSentinel,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Operation '{operation.OperationId}' pivot string must be " +
                    $"'{CanvasAutoPlacement.AutoPivotSentinel}' (server-computed placement) or an " +
                    "explicit {{x,y}} point.");
            }
            // objectId and componentTypeId are already enforced as non-empty UUIDs by GuidArguments
            // before this validator runs; only the optional autoUpstream needs shape checking here.
            if (arguments.TryGetProperty("autoUpstream", out var autoUpstream))
            {
                ValidateAutoUpstream(operation, autoUpstream);
            }
            return;
        }

        RequireOnlyProperties(pivot, operation.OperationId, "x", "y");
        if (arguments.TryGetProperty("autoUpstream", out _))
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' may declare autoUpstream only with pivot " +
                $"'{CanvasAutoPlacement.AutoPivotSentinel}'; an explicit {{x,y}} pivot owns its own " +
                "coordinates.");
        }
        var create = DeserializeArguments<CreateCanvasObjectRequest>(arguments, operation.OperationId);
        if (create.ObjectId == Guid.Empty || create.ComponentTypeId == Guid.Empty ||
            !float.IsFinite(create.Pivot.X) || !float.IsFinite(create.Pivot.Y))
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' has an invalid canvas create payload.");
        }
    }

    private static void ValidateAutoUpstream(TypedOperation operation, JsonElement autoUpstream)
    {
        if (autoUpstream.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' autoUpstream must be an array of object UUIDs.");
        }
        foreach (var element in autoUpstream.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(element.GetString(), out var id) || id == Guid.Empty)
            {
                throw new InvalidOperationException(
                    $"Operation '{operation.OperationId}' autoUpstream must contain non-empty object UUIDs.");
            }
        }
    }

    private static T DeserializeArguments<T>(JsonElement arguments, string operationId) =>
        arguments.Deserialize<T>(BridgeProtocol.JsonOptions)
        ?? throw new InvalidOperationException(
            $"Operation '{operationId}' payload deserialized to an empty request.");

    private static void ValidateMoveArguments(MoveCanvasObjectsRequest request)
    {
        if (request.Pivots is null || request.ExpectedFingerprints is null ||
            request.Pivots.Count == 0 ||
            !request.Pivots.Keys.ToHashSet().SetEquals(request.ExpectedFingerprints.Keys) ||
            request.Pivots.Any(item => item.Key == Guid.Empty ||
                !float.IsFinite(item.Value.X) || !float.IsFinite(item.Value.Y)) ||
            request.ExpectedFingerprints.Any(item =>
                item.Key == Guid.Empty || string.IsNullOrWhiteSpace(item.Value)))
        {
            throw new InvalidOperationException(
                $"Operation '{request.OperationId}' has invalid canvas move targets or fingerprints.");
        }
    }

    private static void ValidateCanvasPivotsShape(JsonElement pivots, string operationId)
    {
        if (pivots.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' pivots must be a component-to-point object.");
        }
        foreach (var pivot in pivots.EnumerateObject())
        {
            RequireOnlyProperties(pivot.Value, operationId, "x", "y");
        }
    }

    private static void ValidateWireArguments(SetWireRequest request)
    {
        if (request.Wire is null ||
            request.Wire.SourceObjectId == Guid.Empty || request.Wire.SourceParameterId == Guid.Empty ||
            request.Wire.TargetObjectId == Guid.Empty || request.Wire.TargetParameterId == Guid.Empty ||
            (request.Wire.SourceObjectId == request.Wire.TargetObjectId &&
             request.Wire.SourceParameterId == request.Wire.TargetParameterId))
        {
            throw new InvalidOperationException(
                $"Operation '{request.OperationId}' has invalid wire endpoints.");
        }
    }

    private static void ValidatePythonSchema(SetParameterSchemaRequest request, string operationId)
    {
        if (request.ComponentId == Guid.Empty || request.Inputs is null || request.Outputs is null)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' has an invalid Python parameter schema.");
        }
        // The model only owns each socket's name/access/typeHint. ParameterId, nickName, and
        // typeHint are server-normalized by the adapter (placeholder ids generated, nickName
        // defaults to name, typeHint defaults to object), so only names are validated here — and
        // the error names the offender instead of a blanket rejection.
        var parameters = request.Inputs.Concat(request.Outputs).ToArray();
        if (parameters.Any(parameter => parameter is null || string.IsNullOrWhiteSpace(parameter.Name)))
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' has a Python socket without a name; every input and " +
                "output needs a script variable name.");
        }
        var duplicateNames = parameters
            .GroupBy(parameter => parameter.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateNames.Length > 0)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' declares duplicate Python socket names: " +
                $"{string.Join(", ", duplicateNames)}. Socket variable names must be unique " +
                "across inputs and outputs.");
        }
        var explicitIds = parameters
            .Where(parameter => parameter.ParameterId != Guid.Empty)
            .Select(parameter => parameter.ParameterId)
            .ToArray();
        if (explicitIds.Distinct().Count() != explicitIds.Length)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' declares duplicate Python socket ids; omit " +
                "parameterId entirely (the server assigns and reconciles socket ids).");
        }
    }

    internal static void ValidatePrimitiveArguments(
        CreateRhinoPrimitiveRequest request,
        string operationId)
    {
        if (request.SourceDocKey is not null)
        {
            // Same anti-spoof rule as rhino.upsert: provenance is server-injected at execution.
            throw new InvalidOperationException(
                $"Operation '{operationId}' must not set sourceDocKey; provenance is stamped by the server.");
        }
        if (request.ObjectId == Guid.Empty || string.IsNullOrWhiteSpace(request.LogicalEntityId))
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' has an invalid Rhino primitive identity.");
        }
        var definitions = new object?[]
        {
            request.Point, request.Line, request.Polyline,
            request.Circle, request.Box, request.Sphere
        };
        if (definitions.Count(item => item is not null) != 1 ||
            request.Kind switch
            {
                RhinoPrimitiveKind.Point => request.Point is null,
                RhinoPrimitiveKind.Line => request.Line is null,
                RhinoPrimitiveKind.Polyline => request.Polyline is null,
                RhinoPrimitiveKind.Circle => request.Circle is null,
                RhinoPrimitiveKind.Box => request.Box is null,
                RhinoPrimitiveKind.Sphere => request.Sphere is null,
                _ => true
            })
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' must supply exactly one primitive definition matching kind.");
        }
        var points = request.Kind switch
        {
            RhinoPrimitiveKind.Point => new[] { request.Point!.Location },
            RhinoPrimitiveKind.Line => new[] { request.Line!.From, request.Line.To },
            RhinoPrimitiveKind.Polyline => request.Polyline!.Vertices?.ToArray() ?? [],
            RhinoPrimitiveKind.Circle => new[] { request.Circle!.Center },
            RhinoPrimitiveKind.Box => new[] { request.Box!.Minimum, request.Box.Maximum },
            RhinoPrimitiveKind.Sphere => new[] { request.Sphere!.Center },
            _ => []
        };
        if (points.Length == 0 || points.Any(point => point is null ||
                !double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z)) ||
            request.Polyline is { } polyline &&
                (polyline.Vertices is null || polyline.Vertices.Count < (polyline.Closed ? 3 : 2) ||
                 polyline.Vertices.Count > 10_000) ||
            request.Circle is { } circle &&
                (!double.IsFinite(circle.Radius) || circle.Radius <= 0 || circle.Normal is null ||
                 !double.IsFinite(circle.Normal.X) || !double.IsFinite(circle.Normal.Y) ||
                 !double.IsFinite(circle.Normal.Z) ||
                 (circle.Normal.X == 0 && circle.Normal.Y == 0 && circle.Normal.Z == 0)) ||
            request.Sphere is { } sphere &&
                (!double.IsFinite(sphere.Radius) || sphere.Radius <= 0) ||
            request.Box is { } box &&
                (box.Maximum.X <= box.Minimum.X || box.Maximum.Y <= box.Minimum.Y ||
                 box.Maximum.Z <= box.Minimum.Z))
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' has invalid Rhino primitive geometry.");
        }
    }

    private static void ValidatePrimitiveCoordinateShapes(
        CreateRhinoPrimitiveRequest request,
        JsonElement arguments,
        string operationId)
    {
        switch (request.Kind)
        {
            case RhinoPrimitiveKind.Point:
                RequirePoint3(
                    arguments.GetProperty("point").GetProperty("location"),
                    operationId);
                return;
            case RhinoPrimitiveKind.Line:
                var line = arguments.GetProperty("line");
                RequirePoint3(line.GetProperty("from"), operationId);
                RequirePoint3(line.GetProperty("to"), operationId);
                return;
            case RhinoPrimitiveKind.Polyline:
                var vertices = arguments.GetProperty("polyline").GetProperty("vertices");
                if (vertices.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException(
                        $"Operation '{operationId}' polyline vertices must be an array.");
                }
                foreach (var vertex in vertices.EnumerateArray())
                {
                    RequirePoint3(vertex, operationId);
                }
                return;
            case RhinoPrimitiveKind.Circle:
                var circle = arguments.GetProperty("circle");
                RequirePoint3(circle.GetProperty("center"), operationId);
                RequirePoint3(circle.GetProperty("normal"), operationId);
                return;
            case RhinoPrimitiveKind.Box:
                var box = arguments.GetProperty("box");
                RequirePoint3(box.GetProperty("minimum"), operationId);
                RequirePoint3(box.GetProperty("maximum"), operationId);
                return;
            case RhinoPrimitiveKind.Sphere:
                RequirePoint3(
                    arguments.GetProperty("sphere").GetProperty("center"),
                    operationId);
                return;
            default:
                throw new InvalidOperationException(
                    $"Operation '{operationId}' has an unsupported Rhino primitive kind.");
        }
    }

    private static void RequirePoint3(JsonElement value, string operationId) =>
        RequireOnlyProperties(value, operationId, "x", "y", "z");

    /// <summary>
    /// The Approved flag is server-injected at execution when a user approval grant covers the
    /// object; a model-authored payload carrying it would let the human-wins default-deny be
    /// bypassed by prompt alone. (Disallow no longer catches this — the member is mapped.)
    /// </summary>
    internal static void RequireNotPreApproved(bool approved, string operationId)
    {
        if (approved)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' must not set approved; user approval is granted through " +
                "the panel and injected by the server.");
        }
    }

    internal static void ValidatePurgeArguments(PurgeTableEntriesRequest request, string operationId)
    {
        if (request.Entries is null || request.Entries.Count == 0)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' must list at least one table entry to purge.");
        }
        foreach (var entry in request.Entries)
        {
            if (entry.Id == Guid.Empty ||
                (entry.Table ?? string.Empty).Trim().ToLowerInvariant()
                    is not ("block" or "dimstyle" or "linetype" or "material"))
            {
                throw new InvalidOperationException(
                    $"Operation '{operationId}' has an invalid purge entry; table must be " +
                    "block|dimStyle|linetype|material with a non-empty id.");
            }
        }
    }

    internal static void ValidateMoveObjectsArguments(MoveObjectsToLayerRequest request, string operationId)
    {
        RequireNotPreApproved(request.Approved, operationId);
        if (request.TargetLayerId == Guid.Empty || request.Items is null || request.Items.Count == 0)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' has an invalid layer-move payload.");
        }
        var seen = new HashSet<Guid>();
        foreach (var item in request.Items)
        {
            if (item.ObjectId == Guid.Empty || string.IsNullOrWhiteSpace(item.ExpectedFingerprint))
            {
                throw new InvalidOperationException(
                    $"Operation '{operationId}' layer-move items need an objectId and expectedFingerprint.");
            }
            if (!seen.Add(item.ObjectId))
            {
                throw new InvalidOperationException(
                    $"Operation '{operationId}' lists Rhino object {item.ObjectId:D} more than once.");
            }
        }
    }

    internal static void ValidateFixEndpointPairArguments(FixEndpointPairRequest request, string operationId)
    {
        RequireNotPreApproved(request.Approved, operationId);
        if (request.AnchorObjectId == Guid.Empty || request.MoveObjectId == Guid.Empty ||
            request.AnchorObjectId == request.MoveObjectId ||
            string.IsNullOrWhiteSpace(request.ExpectedAnchorFingerprint) ||
            string.IsNullOrWhiteSpace(request.ExpectedFingerprint) ||
            request.AnchorEnd is not (0 or 1) || request.MoveEnd is not (0 or 1) ||
            double.IsNaN(request.Tolerance) || double.IsInfinity(request.Tolerance) || request.Tolerance < 0)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' has an invalid Rhino endpoint-fix payload.");
        }
    }

    internal static void ValidateTransformArguments(
        TransformRhinoObjectRequest request,
        string operationId)
    {
        RequireNotPreApproved(request.Approved, operationId);
        if (request.ObjectId == Guid.Empty || string.IsNullOrWhiteSpace(request.ExpectedFingerprint) ||
            request.Matrix is null)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' has an invalid Rhino transform payload.");
        }
        var matrix = request.Matrix;
        var values = new[]
        {
            matrix.M00, matrix.M01, matrix.M02, matrix.M03,
            matrix.M10, matrix.M11, matrix.M12, matrix.M13,
            matrix.M20, matrix.M21, matrix.M22, matrix.M23,
            matrix.M30, matrix.M31, matrix.M32, matrix.M33
        };
        if (values.Any(value => !double.IsFinite(value)) ||
            matrix.M30 != 0 || matrix.M31 != 0 || matrix.M32 != 0 || matrix.M33 != 1)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' matrix must be a finite affine 4x4 transform.");
        }
    }

    internal static void ValidateUpsertArguments(UpsertRhinoObjectRequest request, string operationId)
    {
        RequireNotPreApproved(request.Approved, operationId);
        if (request.SourceDocKey is not null)
        {
            // Provenance is server-injected at execution; a model-authored payload carrying it
            // would let bake attribution be spoofed.
            throw new InvalidOperationException(
                $"Operation '{operationId}' must not set sourceDocKey; provenance is stamped by the server.");
        }
        if (request.ObjectId == Guid.Empty || string.IsNullOrWhiteSpace(request.LogicalEntityId) ||
            string.IsNullOrWhiteSpace(request.GeometryType) || string.IsNullOrWhiteSpace(request.GeometryJson) ||
            request.AttributesJson is null)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' has an invalid Rhino upsert payload.");
        }
        try
        {
            using var geometry = JsonDocument.Parse(request.GeometryJson);
            if (!string.IsNullOrWhiteSpace(request.AttributesJson))
            {
                using var attributes = JsonDocument.Parse(request.AttributesJson);
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' contains malformed Rhino JSON.",
                exception);
        }
    }

    private static void RequireOnlyProperties(
        JsonElement value,
        string operationId,
        params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.EnumerateObject().Select(item => item.Name)
                .OrderBy(item => item, StringComparer.Ordinal)
                .SequenceEqual(names.OrderBy(item => item, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' payload has missing or unsupported properties.");
        }
    }

    private static void ValidateOperationResourceAlignment(
        TypedOperation operation,
        string bridgeOperation,
        JsonElement arguments)
    {
        switch (bridgeOperation)
        {
            case "canvas.setNumberSlider":
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "objectId", operation.OperationId),
                    write: true,
                    ResourceKind.GrasshopperComponentValue);
                return;

            case "canvas.move":
                var pivotIds = ReadGuidPropertyNames(
                    arguments.GetProperty("pivots"),
                    operation.OperationId,
                    "pivots");
                var fingerprintIds = ReadGuidPropertyNames(
                    arguments.GetProperty("expectedFingerprints"),
                    operation.OperationId,
                    "expectedFingerprints");
                if (!pivotIds.SetEquals(fingerprintIds))
                {
                    throw new InvalidOperationException(
                        $"Operation '{operation.OperationId}' pivots and expectedFingerprints target different components.");
                }
                RequireExactDeclaredGuidTargets(
                    operation,
                    pivotIds,
                    write: true,
                    ResourceKind.GrasshopperComponentLayout);
                return;

            case "canvas.setWire":
                var wire = arguments.GetProperty("wire");
                var sourceObject = RequireArgumentGuid(wire, "sourceObjectId", operation.OperationId);
                var sourceParameter = RequireArgumentGuid(wire, "sourceParameterId", operation.OperationId);
                var targetObject = RequireArgumentGuid(wire, "targetObjectId", operation.OperationId);
                var targetParameter = RequireArgumentGuid(wire, "targetParameterId", operation.OperationId);
                var wireId = FormattableString.Invariant(
                    $"{sourceObject:N}/{sourceParameter:N}>{targetObject:N}/{targetParameter:N}");
                var expectedAction = operation.Kind == OperationKind.ConnectWire ? "connect" : "disconnect";
                if (!string.Equals(
                        RequireArgumentString(arguments, "action", operation.OperationId),
                        expectedAction,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Operation '{operation.OperationId}' wire action does not match typed kind '{operation.Kind}'.");
                }
                if (operation.Kind == OperationKind.ConnectWire &&
                    (!arguments.TryGetProperty("rejectCycles", out var rejectCycles) ||
                     rejectCycles.ValueKind != JsonValueKind.True))
                {
                    throw new InvalidOperationException(
                        $"Operation '{operation.OperationId}' must reject wire cycles.");
                }
                RequireExactDeclaredStringTarget(
                    operation,
                    wireId,
                    write: true,
                    ResourceKind.GrasshopperWire);
                return;

            case "canvas.create":
            case "canvas.delete":
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "objectId", operation.OperationId),
                    write: true,
                    ResourceKind.GrasshopperComponent);
                return;

            case "canvas.setGroup":
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "groupId", operation.OperationId),
                    write: true,
                    ResourceKind.GrasshopperGroup);
                return;

            case "python.setSource":
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "componentId", operation.OperationId),
                    write: true,
                    ResourceKind.GrasshopperComponentSource);
                return;
            case "python.setSchema":
            case "python.setTyping":
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "componentId", operation.OperationId),
                    write: true,
                    ResourceKind.GrasshopperComponentIo);
                return;
            case "python.execute":
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "componentId", operation.OperationId),
                    write: true,
                    ResourceKind.GrasshopperComponentValue);
                return;
            case "python.runtimeMessages":
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "componentId", operation.OperationId),
                    write: false,
                    ResourceKind.GrasshopperComponentValue);
                return;
            case "python.inspect":
                RequireSingleDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "componentId", operation.OperationId),
                    write: false,
                    ResourceKind.GrasshopperComponent,
                    ResourceKind.GrasshopperComponentSource,
                    ResourceKind.GrasshopperComponentIo,
                    ResourceKind.GrasshopperComponentValue);
                return;

            case "canvas.inspect":
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "objectId", operation.OperationId),
                    write: false,
                    ResourceKind.GrasshopperComponent);
                return;

            case "rhino.inspect":
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "objectId", operation.OperationId),
                    write: false,
                    ResourceKind.RhinoObject);
                return;

            case "rhino.createPrimitive":
            case "rhino.transform":
            case "rhino.upsert":
            case "rhino.delete":
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "objectId", operation.OperationId),
                    write: true,
                    ResourceKind.RhinoObject);
                return;

            case "rhino.fixEndpointPair":
                // The move object is the single declared write; the untouched anchor must still be
                // declared as a read so its fingerprint expectation guards the pair.
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "moveObjectId", operation.OperationId),
                    write: true,
                    ResourceKind.RhinoObject);
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "anchorObjectId", operation.OperationId),
                    write: false,
                    ResourceKind.RhinoObject);
                return;

            case "rhino.moveObjectsToLayer":
                // One operation, N object writes: every moved object must be declared, and every
                // declared write must be moved — the same exactness single-target ops get.
                RequireExactDeclaredGuidTargets(
                    operation,
                    ReadItemGuids(arguments, "items", "objectId", operation.OperationId).ToHashSet(),
                    write: true,
                    ResourceKind.RhinoObject);
                return;

            case "rhino.updateLayer":
            case "rhino.deleteLayer":
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "layerId", operation.OperationId),
                    write: true,
                    ResourceKind.RhinoLayer);
                return;

            case "rhino.purgeTableEntries":
                // One declared write per purged entry, in that entry's own table domain — a purge
                // is exactly as declared as any other destructive write.
                RequireExactDeclaredTableTargets(operation, arguments);
                return;

            case "rhino.layerState":
                // Save/restore/delete all touch the layer table as a whole; restore rewrites every
                // layer, so the table resource is the honest (and CAS-able) declaration.
                if (!operation.Writes.Any(resource => resource.Kind == ResourceKind.RhinoLayerTable))
                {
                    throw new InvalidOperationException(
                        $"Operation '{operation.OperationId}' must declare a rhinoLayerTable write " +
                        "(a layer state save/restore/delete acts on the whole table).");
                }
                return;

            case "rhino.ensureLayer":
                // Creating or updating a layer by path: the layer is the write. A brand-new layer
                // has no id yet, so the declaration is an absent-expectation on its path-derived
                // id — the adapter returns the real id after creating it.
                return;
        }
    }

    /// <summary>
    /// Per-object after-fingerprints from a batch mutation result (keyed by the resource id form
    /// the writeSet uses), or null when the response is not a batch.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? ReadBatchItemFingerprints(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("objectId", out var objectId) &&
                objectId.ValueKind == JsonValueKind.String &&
                Guid.TryParse(objectId.GetString(), out var id) &&
                item.TryGetProperty("afterFingerprint", out var fingerprint) &&
                fingerprint.ValueKind == JsonValueKind.String)
            {
                map[id.ToString("D")] = fingerprint.GetString()!;
            }
        }
        return map.Count > 0 ? map : null;
    }

    /// <summary>
    /// Every purge entry must be declared as a write in its own table domain, and every declared
    /// table write must be purged — the exactness single-object ops get, applied per entry.
    /// </summary>
    private static void RequireExactDeclaredTableTargets(TypedOperation operation, JsonElement arguments)
    {
        var declared = operation.Writes
            .Where(resource => resource.Kind is ResourceKind.RhinoBlockDefinition
                or ResourceKind.RhinoDimensionStyle or ResourceKind.RhinoMaterial or ResourceKind.RhinoLinetype)
            .Select(resource => (resource.Kind, Id: resource.Id))
            .ToHashSet();
        var payload = new HashSet<(ResourceKind Kind, string Id)>();
        foreach (var entry in arguments.GetProperty("entries").EnumerateArray())
        {
            var table = entry.TryGetProperty("table", out var tableValue) ? tableValue.GetString() : null;
            var id = entry.TryGetProperty("id", out var idValue) && Guid.TryParse(idValue.GetString(), out var parsed)
                ? parsed
                : Guid.Empty;
            var kind = (table ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "block" => ResourceKind.RhinoBlockDefinition,
                "dimstyle" => ResourceKind.RhinoDimensionStyle,
                "linetype" => ResourceKind.RhinoLinetype,
                "material" => ResourceKind.RhinoMaterial,
                _ => (ResourceKind?)null,
            };
            if (kind is null || id == Guid.Empty)
            {
                throw new InvalidOperationException(
                    $"Operation '{operation.OperationId}' has an invalid purge entry.");
            }
            payload.Add((kind.Value, id.ToString("D")));
        }
        if (!declared.SetEquals(payload))
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' must declare exactly one write per purged entry " +
                "(kinds rhinoBlockDefinition|rhinoDimensionStyle|rhinoLinetype|rhinoMaterial).");
        }
    }

    private static IReadOnlyList<Guid> ReadItemGuids(
        JsonElement arguments,
        string arrayProperty,
        string idProperty,
        string operationId)
    {
        if (!arguments.TryGetProperty(arrayProperty, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' argument '{arrayProperty}' must be an array.");
        }
        var ids = new List<Guid>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty(idProperty, out var idValue) ||
                idValue.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(idValue.GetString(), out var id) ||
                id == Guid.Empty)
            {
                throw new InvalidOperationException(
                    $"Operation '{operationId}' has an item without a valid '{idProperty}'.");
            }
            ids.Add(id);
        }
        return ids;
    }

    private static HashSet<Guid> ReadGuidPropertyNames(
        JsonElement value,
        string operationId,
        string property)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' argument '{property}' must be an object keyed by component UUID.");
        }
        HashSet<Guid> result = [];
        foreach (var item in value.EnumerateObject())
        {
            if (!Guid.TryParse(item.Name, out var id) || id == Guid.Empty || !result.Add(id))
            {
                throw new InvalidOperationException(
                    $"Operation '{operationId}' argument '{property}' contains an invalid or duplicate UUID key.");
            }
        }
        if (result.Count == 0)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' argument '{property}' cannot be empty.");
        }
        return result;
    }

    private static void RequireExactDeclaredGuidTarget(
        TypedOperation operation,
        Guid target,
        bool write,
        ResourceKind kind) =>
        RequireExactDeclaredGuidTargets(operation, new HashSet<Guid> { target }, write, kind);

    private static void RequireExactDeclaredGuidTargets(
        TypedOperation operation,
        IReadOnlySet<Guid> targets,
        bool write,
        ResourceKind kind)
    {
        var declared = (write ? operation.Writes : operation.Reads)
            .ToArray();
        if (declared.Length != targets.Count ||
            declared.Any(resource =>
                resource.Kind != kind ||
                resource.Field != "*" ||
                !Guid.TryParse(resource.Id, out var id) ||
                !string.Equals(resource.Id, id.ToString("D"), StringComparison.Ordinal) ||
                !targets.Contains(id)) ||
            targets.Any(target => !declared.Any(resource =>
                Guid.TryParse(resource.Id, out var id) && id == target)))
        {
            var expected = string.Join(", ", targets.Select(id => $"{kind} id='{id:D}' field='*'"));
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' payload targets do not match its declared " +
                $"{(write ? "write" : "read")} resources. Declare exactly: {expected}.");
        }
    }

    private static void RequireSingleDeclaredGuidTarget(
        TypedOperation operation,
        Guid target,
        bool write,
        params ResourceKind[] allowedKinds)
    {
        var declared = write ? operation.Writes : operation.Reads;
        if (declared.Count != 1 ||
            !allowedKinds.Contains(declared[0].Kind) ||
            declared[0].Field != "*" ||
            !string.Equals(declared[0].Id, target.ToString("D"), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' payload target does not match its declared " +
                $"{(write ? "write" : "read")} resource. Declare exactly one {allowedKinds[0]} resource with " +
                $"id='{target:D}' and field='*'.");
        }
    }

    private static void RequireExactDeclaredStringTarget(
        TypedOperation operation,
        string target,
        bool write,
        ResourceKind kind)
    {
        var declared = (write ? operation.Writes : operation.Reads)
            .ToArray();
        if (declared.Length != 1 ||
            declared[0].Kind != kind ||
            declared[0].Field != "*" ||
            !string.Equals(declared[0].Id, target, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' payload target does not match its declared " +
                $"{(write ? "write" : "read")} resource. Declare exactly one {kind} resource with " +
                $"id='{target}' and field='*' (this exact string, derived from the payload).");
        }
    }

    private static IReadOnlyList<string> GuidArguments(string bridgeOperation) => bridgeOperation switch
    {
        "canvas.create" => ["objectId", "componentTypeId"],
        "canvas.referenceRhinoObjects" => ["objectId"],
        "canvas.delete" => ["objectId"],
        "canvas.setNumberSlider" => ["objectId"],
        "canvas.setGroup" => ["groupId"],
        "python.setSource" or "python.setSchema" or "python.execute" or
            "python.runtimeMessages" or "python.inspect" => ["componentId"],
        "python.setTyping" => ["componentId", "inputParameterId"],
        "canvas.inspect" or "rhino.inspect" or "rhino.createPrimitive" or
            "rhino.transform" or "rhino.upsert" or "rhino.delete" => ["objectId"],
        "rhino.fixEndpointPair" => ["anchorObjectId", "moveObjectId"],
        "rhino.moveObjectsToLayer" => ["targetLayerId"],
        "rhino.updateLayer" or "rhino.deleteLayer" or "rhino.ensureLayer" => ["layerId"],
        _ => Array.Empty<string>()
    };

    private static string RequireArgumentString(
        JsonElement arguments,
        string property,
        string operationId)
    {
        if (!arguments.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' argument '{property}' must be a non-empty string.");
        }
        return value.GetString()!;
    }

    private static Guid RequireArgumentGuid(
        JsonElement arguments,
        string property,
        string operationId)
    {
        var text = RequireArgumentString(arguments, property, operationId);
        if (!Guid.TryParse(text, out var value) || value == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' argument '{property}' must be a non-empty UUID.");
        }
        return value;
    }

}
