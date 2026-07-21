using GPTino.BridgeContract;

namespace GPTino.CordycepsAdapter;

public sealed class CordycepsCanvasBridgeOperationHandler : IBridgeOperationHandler
{
    private readonly ICordycepsCanvasAdapter _adapter;

    public CordycepsCanvasBridgeOperationHandler(ICordycepsCanvasAdapter adapter)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public BridgeAdapterOwner Owner => BridgeAdapterOwner.CordycepsCanvas;

    public async Task<BridgeOperationResponse> HandleAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireRequest(request);
        return request.Operation switch
        {
            "canvas.snapshot" => await SnapshotAsync(target, request, cancellationToken).ConfigureAwait(false),
            "canvas.inspect" => await InspectAsync(target, request, cancellationToken).ConfigureAwait(false),
            "canvas.inspectOutputs" => await InspectOutputsAsync(target, request, cancellationToken).ConfigureAwait(false),
            "canvas.catalog" => await CatalogAsync(target, request, cancellationToken).ConfigureAwait(false),
            "canvas.create" => await MutationAsync<CreateCanvasObjectRequest>(target, request, _adapter.CreateObjectAsync, cancellationToken).ConfigureAwait(false),
            "canvas.delete" => await MutationAsync<DeleteCanvasObjectRequest>(target, request, _adapter.DeleteObjectAsync, cancellationToken).ConfigureAwait(false),
            "canvas.move" => await MutationAsync<MoveCanvasObjectsRequest>(target, request, _adapter.MoveObjectsAsync, cancellationToken).ConfigureAwait(false),
            "canvas.setNumberSlider" => await MutationAsync<SetNumberSliderValueRequest>(target, request, _adapter.SetNumberSliderValueAsync, cancellationToken).ConfigureAwait(false),
            "canvas.setWire" => await MutationAsync<SetWireRequest>(target, request, _adapter.SetWireAsync, cancellationToken).ConfigureAwait(false),
            "canvas.setGroup" => await MutationAsync<SetGroupRequest>(target, request, _adapter.SetGroupAsync, cancellationToken).ConfigureAwait(false),
            _ => throw new BridgeProtocolException(
                "unknown_cordyceps_canvas_operation",
                $"Unknown Cordyceps canvas operation '{request.Operation}'."),
        };
    }

    private async Task<BridgeOperationResponse> SnapshotAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var snapshot = await _adapter.CaptureSnapshotAsync(target, cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            snapshot,
            afterFingerprint: snapshot.DocumentFingerprint);
    }

    private async Task<BridgeOperationResponse> InspectAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var arguments = request.DeserializeArguments<ObjectIdArguments>();
        var state = await _adapter.InspectObjectAsync(target, arguments.ObjectId, cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            state,
            afterFingerprint: state.Fingerprint);
    }

    private async Task<BridgeOperationResponse> InspectOutputsAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var result = await _adapter.InspectOutputsAsync(
            target,
            request.DeserializeArguments<InspectCanvasOutputsRequest>(),
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            result,
            afterFingerprint: result.Fingerprint);
    }

    private async Task<BridgeOperationResponse> CatalogAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var result = await _adapter.SearchComponentCatalogAsync(
            target,
            request.DeserializeArguments<ComponentCatalogSearchRequest>(),
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            result);
    }

    private static async Task<BridgeOperationResponse> MutationAsync<TRequest>(
        DocumentTarget target,
        BridgeOperationRequest request,
        Func<DocumentTarget, TRequest, CancellationToken, Task<CanvasMutationResult>> action,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Write);
        var result = await action(
            target,
            request.DeserializeArguments<TRequest>(),
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            result.Changed,
            result,
            result.BeforeFingerprint,
            result.AfterFingerprint);
    }

    private void RequireRequest(BridgeOperationRequest request)
    {
        if (request.Owner != Owner)
        {
            throw new BridgeProtocolException("adapter_owner", "Canvas handler received another owner's request.");
        }

        request.Validate();
    }

    private static void RequireAccess(BridgeOperationRequest request, BridgeOperationAccess expected)
    {
        if (request.Access != expected)
        {
            throw new BridgeProtocolException(
                "operation_access",
                $"Operation '{request.Operation}' requires {expected} access.");
        }
    }

    private sealed record ObjectIdArguments(Guid ObjectId);
}

public sealed class CordycepsRhinoBridgeOperationHandler : IBridgeOperationHandler
{
    private readonly ICordycepsRhinoSceneAdapter _adapter;

    public CordycepsRhinoBridgeOperationHandler(ICordycepsRhinoSceneAdapter adapter)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public BridgeAdapterOwner Owner => BridgeAdapterOwner.CordycepsRhino;

    public async Task<BridgeOperationResponse> HandleAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Owner != Owner)
        {
            throw new BridgeProtocolException("adapter_owner", "Rhino handler received another owner's request.");
        }

        request.Validate();
        return request.Operation switch
        {
            "rhino.list" => await ListAsync(target, request, cancellationToken).ConfigureAwait(false),
            "rhino.inspect" => await InspectAsync(target, request, cancellationToken).ConfigureAwait(false),
            "rhino.validateUpsert" => await ValidateUpsertAsync(target, request, cancellationToken).ConfigureAwait(false),
            "rhino.createPrimitive" => await MutationAsync<CreateRhinoPrimitiveRequest>(target, request, _adapter.CreatePrimitiveAsync, cancellationToken).ConfigureAwait(false),
            "rhino.upsert" => await MutationAsync<UpsertRhinoObjectRequest>(target, request, _adapter.UpsertObjectAsync, cancellationToken).ConfigureAwait(false),
            "rhino.delete" => await MutationAsync<DeleteRhinoObjectRequest>(target, request, _adapter.DeleteObjectAsync, cancellationToken).ConfigureAwait(false),
            "rhino.ensureLayer" => await MutationAsync<EnsureRhinoLayerRequest>(target, request, _adapter.EnsureLayerAsync, cancellationToken).ConfigureAwait(false),
            "rhino.transform" => await TransformAsync(target, request, cancellationToken).ConfigureAwait(false),
            _ => throw new BridgeProtocolException(
                "unknown_cordyceps_rhino_operation",
                $"Unknown Cordyceps Rhino operation '{request.Operation}'."),
        };
    }

    private async Task<BridgeOperationResponse> ListAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var result = await _adapter.ListObjectsAsync(
            target,
            request.DeserializeArguments<RhinoListObjectsRequest>(),
            cancellationToken).ConfigureAwait(false);
        var diagnostics = result.Truncated
            ? new[]
            {
                new BridgeDiagnostic(
                    BridgeDiagnosticSeverity.Warning,
                    "rhino_list_truncated",
                    $"Rhino list result was truncated at the requested limit {result.Limit}.")
            }
            : Array.Empty<BridgeDiagnostic>();
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            result,
            afterFingerprint: result.Fingerprint,
            diagnostics: diagnostics);
    }

    private async Task<BridgeOperationResponse> InspectAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var arguments = request.DeserializeArguments<ObjectIdArguments>();
        var state = await _adapter.InspectObjectAsync(target, arguments.ObjectId, cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            state,
            afterFingerprint: state.Fingerprint);
    }

    private static async Task<BridgeOperationResponse> MutationAsync<TRequest>(
        DocumentTarget target,
        BridgeOperationRequest request,
        Func<DocumentTarget, TRequest, CancellationToken, Task<RhinoSceneMutationResult>> action,
        CancellationToken cancellationToken)
        where TRequest : IRhinoSceneMutationRequest
    {
        RequireAccess(request, BridgeOperationAccess.Write);
        var arguments = request.DeserializeArguments<TRequest>();
        RequireMatchingOperationId(request, arguments);
        var result = await action(
            target,
            arguments,
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            result.Changed,
            result,
            result.BeforeFingerprint,
            result.AfterFingerprint,
            result.Diagnostics);
    }

    private async Task<BridgeOperationResponse> ValidateUpsertAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var arguments = request.DeserializeArguments<UpsertRhinoObjectRequest>();
        RequireMatchingOperationId(request, arguments);
        var result = await _adapter.ValidateUpsertObjectAsync(
            target,
            arguments,
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            result);
    }

    private async Task<BridgeOperationResponse> TransformAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Write);
        var arguments = request.DeserializeArguments<TransformRhinoObjectRequest>();
        RequireMatchingOperationId(request, arguments);
        if (string.IsNullOrWhiteSpace(request.ExpectedFingerprint) ||
            !string.Equals(
                request.ExpectedFingerprint,
                arguments.ExpectedFingerprint,
                StringComparison.Ordinal))
        {
            throw new BridgeProtocolException(
                "expected_fingerprint",
                "rhino.transform requires matching envelope and argument fingerprints.");
        }

        var result = await _adapter.TransformObjectAsync(
            target,
            arguments,
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            result.Changed,
            result,
            result.BeforeFingerprint,
            result.AfterFingerprint,
            result.Diagnostics);
    }

    private static void RequireMatchingOperationId(
        BridgeOperationRequest request,
        IRhinoSceneMutationRequest arguments)
    {
        if (!string.Equals(request.OperationId, arguments.OperationId, StringComparison.Ordinal))
        {
            throw new BridgeProtocolException(
                "operation_id",
                $"Operation envelope '{request.OperationId}' does not match payload " +
                $"'{arguments.OperationId}'.");
        }
    }

    private static void RequireAccess(BridgeOperationRequest request, BridgeOperationAccess expected)
    {
        if (request.Access != expected)
        {
            throw new BridgeProtocolException(
                "operation_access",
                $"Operation '{request.Operation}' requires {expected} access.");
        }
    }

    private sealed record ObjectIdArguments(Guid ObjectId);
}
