using GPTino.BridgeContract;

namespace GPTino.WireifyAdapter;

public sealed class WireifyBridgeOperationHandler : IBridgeOperationHandler
{
    private readonly IWireifyDocumentAdapter _adapter;

    public WireifyBridgeOperationHandler(IWireifyDocumentAdapter adapter)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public BridgeAdapterOwner Owner => BridgeAdapterOwner.Wireify;

    public async Task<BridgeOperationResponse> HandleAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        RequireOwner(request);
        request.Validate();

        return request.Operation switch
        {
            "python.inspect" => await InspectAsync(target, request, cancellationToken).ConfigureAwait(false),
            "python.setSource" => await SetSourceAsync(target, request, cancellationToken).ConfigureAwait(false),
            "python.setSchema" => await SetSchemaAsync(target, request, cancellationToken).ConfigureAwait(false),
            "python.setTyping" => await SetTypingAsync(target, request, cancellationToken).ConfigureAwait(false),
            "python.execute" => await ExecuteAsync(target, request, cancellationToken).ConfigureAwait(false),
            "python.runtimeMessages" => await RuntimeMessagesAsync(target, request, cancellationToken).ConfigureAwait(false),
            _ => throw new BridgeProtocolException(
                "unknown_wireify_operation",
                $"Unknown Wireify operation '{request.Operation}'."),
        };
    }

    private async Task<BridgeOperationResponse> InspectAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var arguments = request.DeserializeArguments<ComponentIdArguments>();
        var state = await _adapter.ReadPythonComponentAsync(
            target,
            arguments.ComponentId,
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            state,
            afterFingerprint: PythonComponentFingerprint.Compute(state));
    }

    private async Task<BridgeOperationResponse> SetSourceAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Write);
        var before = await ReadExpectedStateAsync(target, request, cancellationToken).ConfigureAwait(false);
        var result = await _adapter.SetSourceAsync(
            target,
            request.DeserializeArguments<SetPythonSourceRequest>(),
            cancellationToken).ConfigureAwait(false);
        return await MutationResponseAsync(target, request, result, before, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<BridgeOperationResponse> SetSchemaAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Write);
        var before = await ReadExpectedStateAsync(target, request, cancellationToken).ConfigureAwait(false);
        var result = await _adapter.SetParameterSchemaAsync(
            target,
            request.DeserializeArguments<SetParameterSchemaRequest>(),
            cancellationToken).ConfigureAwait(false);
        return await MutationResponseAsync(target, request, result, before, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<BridgeOperationResponse> SetTypingAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Write);
        var before = await ReadExpectedStateAsync(target, request, cancellationToken).ConfigureAwait(false);
        var result = await _adapter.SetInputTypingAsync(
            target,
            request.DeserializeArguments<SetInputTypingRequest>(),
            cancellationToken).ConfigureAwait(false);
        return await MutationResponseAsync(target, request, result, before, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<BridgeOperationResponse> ExecuteAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Write);
        var before = await ReadExpectedStateAsync(target, request, cancellationToken).ConfigureAwait(false);
        var result = await _adapter.ExecuteAsync(
            target,
            request.DeserializeArguments<ExecutePythonComponentRequest>(),
            cancellationToken).ConfigureAwait(false);
        var after = await _adapter.ReadPythonComponentAsync(
            target,
            result.ComponentId,
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: true,
            result,
            beforeFingerprint: before,
            afterFingerprint: PythonComponentFingerprint.Compute(after),
            diagnostics: ToDiagnostics(result.RuntimeMessages));
    }

    private async Task<BridgeOperationResponse> RuntimeMessagesAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        RequireAccess(request, BridgeOperationAccess.Read);
        var arguments = request.DeserializeArguments<ComponentIdArguments>();
        var messages = await _adapter.ReadRuntimeMessagesAsync(
            target,
            arguments.ComponentId,
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            changed: false,
            messages,
            diagnostics: ToDiagnostics(messages));
    }

    private async Task<string> ReadExpectedStateAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        var componentId = request.Operation switch
        {
            "python.setSource" => request.DeserializeArguments<SetPythonSourceRequest>().ComponentId,
            "python.setSchema" => request.DeserializeArguments<SetParameterSchemaRequest>().ComponentId,
            "python.setTyping" => request.DeserializeArguments<SetInputTypingRequest>().ComponentId,
            "python.execute" => request.DeserializeArguments<ExecutePythonComponentRequest>().ComponentId,
            _ => throw new BridgeProtocolException(
                "expected_fingerprint_operation",
                $"Operation '{request.Operation}' does not identify a Python component mutation."),
        };
        var state = await _adapter.ReadPythonComponentAsync(
            target,
            componentId,
            cancellationToken).ConfigureAwait(false);
        var actual = PythonComponentFingerprint.Compute(state);
        if (!string.IsNullOrWhiteSpace(request.ExpectedFingerprint) &&
            !string.Equals(actual, request.ExpectedFingerprint, StringComparison.Ordinal))
        {
            throw new BridgeProtocolException(
                "expected_fingerprint_mismatch",
                $"Python component {componentId:D} changed after the request snapshot.");
        }

        return actual;
    }

    private async Task<BridgeOperationResponse> MutationResponseAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        WireifyMutationResult result,
        string beforeFingerprint,
        CancellationToken cancellationToken)
    {
        var componentId = request.Operation switch
        {
            "python.setSource" => request.DeserializeArguments<SetPythonSourceRequest>().ComponentId,
            "python.setSchema" => request.DeserializeArguments<SetParameterSchemaRequest>().ComponentId,
            "python.setTyping" => request.DeserializeArguments<SetInputTypingRequest>().ComponentId,
            _ => throw new BridgeProtocolException(
                "mutation_fingerprint_operation",
                $"Operation '{request.Operation}' does not identify a Python component mutation."),
        };
        var after = await _adapter.ReadPythonComponentAsync(
            target,
            componentId,
            cancellationToken).ConfigureAwait(false);
        return BridgeOperationResponse.Create(
            request.OperationId,
            result.Changed,
            result,
            beforeFingerprint,
            PythonComponentFingerprint.Compute(after),
            ToDiagnostics(result.RuntimeMessages));
    }

    private static IReadOnlyList<BridgeDiagnostic> ToDiagnostics(
        IReadOnlyList<ComponentRuntimeMessage> messages) =>
        messages.Select(message => new BridgeDiagnostic(
            message.Level switch
            {
                RuntimeMessageLevel.Remark => BridgeDiagnosticSeverity.Information,
                RuntimeMessageLevel.Warning => BridgeDiagnosticSeverity.Warning,
                RuntimeMessageLevel.Error => BridgeDiagnosticSeverity.Error,
                _ => throw new ArgumentOutOfRangeException(nameof(message.Level)),
            },
            $"python_{message.Level.ToString().ToLowerInvariant()}",
            message.Message)).ToArray();

    private void RequireOwner(BridgeOperationRequest request)
    {
        if (request.Owner != Owner)
        {
            throw new BridgeProtocolException("adapter_owner", "Wireify handler received another owner's request.");
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

    private sealed record ComponentIdArguments(Guid ComponentId);
}
