using System.Text.Json;

namespace GPTino.BridgeContract;

public static class BridgeMessageTypes
{
    public const string RegisterDocument = "document.register";
    public const string DocumentRegistered = "document.registered";
    public const string DocumentClosed = "document.closed";
    public const string SelectionChanged = "selection.changed";
    public const string HealthRequest = "bridge.health.request";
    public const string HealthResponse = "bridge.health.response";
    public const string OperationRequest = "operation.request";
    public const string OperationResponse = "operation.response";
}

public enum BridgeAdapterOwner
{
    Wireify,
    CordycepsCanvas,
    CordycepsRhino,
}

public enum BridgeOperationAccess
{
    Read,
    Write,
}

public sealed record RegisterDocumentRequest(
    string InstanceId,
    string BridgeVersion,
    IReadOnlyList<BridgeAdapterOwner> AvailableAdapters);

public sealed record DocumentRegisteredResponse(
    string InstanceId,
    string TargetKey,
    long Generation,
    IReadOnlyList<BridgeAdapterOwner> AvailableAdapters);

public sealed record DocumentClosedEvent(string Reason, long LastGeneration);

/// <summary>
/// Rhino-to-AgentHost push describing the user's current selection in BOTH documents of the
/// bound pair. Ids and display names only — no geometry — so the event stays cheap under rapid
/// selection changes. Selection is a discovery hint, never a snapshot fingerprint substitute,
/// and is deliberately kept OUT of canvas object fingerprints so clicking around never
/// invalidates optimistic concurrency.
/// </summary>
public sealed record SelectionChangedEvent(
    IReadOnlyList<Guid> RhinoObjectIds,
    string? ActiveLayerName,
    DateTimeOffset ObservedAt,
    IReadOnlyList<GrasshopperSelectedObject>? GrasshopperObjects = null);

/// <summary>One selected Grasshopper canvas object, captured by the .gha-side watcher.</summary>
public sealed record GrasshopperSelectedObject(
    Guid ObjectId,
    string Name,
    string NickName);

public sealed record BridgeHealthRequest(string ProbeId);

public sealed record BridgeHealthResponse(
    string ProbeId,
    bool Healthy,
    string InstanceId,
    string TargetKey,
    long Generation,
    DateTimeOffset ObservedAt);

/// <summary>
/// AgentHost-to-Rhino/GH operation envelope. A write lease is mandatory for writes and
/// is issued by the single-writer broker, never by a model.
/// </summary>
public sealed record BridgeOperationRequest(
    string OperationId,
    BridgeAdapterOwner Owner,
    string Operation,
    BridgeOperationAccess Access,
    long BaseSnapshotRevision,
    string? ExpectedFingerprint,
    string? WriterLeaseToken,
    JsonElement Arguments)
{
    public static BridgeOperationRequest Create<TArguments>(
        string operationId,
        BridgeAdapterOwner owner,
        string operation,
        BridgeOperationAccess access,
        long baseSnapshotRevision,
        TArguments arguments,
        string? expectedFingerprint = null,
        string? writerLeaseToken = null) =>
        new(
            operationId,
            owner,
            operation,
            access,
            baseSnapshotRevision,
            expectedFingerprint,
            writerLeaseToken,
            JsonSerializer.SerializeToElement(arguments, BridgeProtocol.JsonOptions));

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(OperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(Operation);
        if (BaseSnapshotRevision < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BaseSnapshotRevision));
        }

        if (Access == BridgeOperationAccess.Write && string.IsNullOrWhiteSpace(WriterLeaseToken))
        {
            throw new BridgeProtocolException(
                "writer_lease_required",
                "Bridge write operations require a broker-issued writer lease.");
        }
    }

    public TArguments DeserializeArguments<TArguments>() =>
        Arguments.Deserialize<TArguments>(BridgeProtocol.JsonOptions)
        ?? throw new JsonException($"Arguments for '{Operation}' deserialized to null.");
}

public sealed record BridgeOperationResponse(
    string OperationId,
    bool Changed,
    string? BeforeFingerprint,
    string? AfterFingerprint,
    JsonElement Result,
    IReadOnlyList<BridgeDiagnostic> Diagnostics)
{
    public static BridgeOperationResponse Create<TResult>(
        string operationId,
        bool changed,
        TResult result,
        string? beforeFingerprint = null,
        string? afterFingerprint = null,
        IReadOnlyList<BridgeDiagnostic>? diagnostics = null) =>
        new(
            operationId,
            changed,
            beforeFingerprint,
            afterFingerprint,
            JsonSerializer.SerializeToElement(result, BridgeProtocol.JsonOptions),
            diagnostics ?? Array.Empty<BridgeDiagnostic>());
}

public sealed record BridgeDiagnostic(
    BridgeDiagnosticSeverity Severity,
    string Code,
    string Message,
    Guid? ObjectId = null);

public enum BridgeDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record BridgeFailure(
    string Code,
    string Message,
    bool Retryable,
    string? OperationId = null);

/// <summary>Routes one adapter owner without allowing cross-owner fallback behavior.</summary>
public interface IBridgeOperationHandler
{
    BridgeAdapterOwner Owner { get; }

    Task<BridgeOperationResponse> HandleAsync(
        DocumentTarget target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken = default);
}
