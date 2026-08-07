using System.Text.Json;
using System.Text.Json.Serialization;

namespace GPTino.BridgeContract;

public static class BridgeProtocol
{
    // v3: StableTargetKey became path-free so a Save As / rename no longer changes the document target
    // identity. Plugin and AgentHost ship together in one package, so this bump only guards against a
    // stale AgentHost surviving an upgrade; both ends must compute the key identically.
    // v4: CanvasOutputParameterInspection gained SampleValues. JsonOptions disallow unmapped members,
    // so any payload-shape change MUST bump this version or skew fails as an opaque JsonException.
    // v5: SelectionChangedEvent gained GrasshopperObjects (canvas selection discovery hint).
    // v6: CanvasObjectState gained per-domain Structure/Layout/Value fingerprints so a component
    //     move no longer invalidates pending value writes.
    // v7: CanvasObjectState gained BoundsOrigin (bounds top-left) feeding deterministic component
    //     auto-placement. It is EXCLUDED from every fingerprint, so it never churns the revision;
    //     the bump only guards the wire shape against a stale AgentHost, as SampleValues did in v4.
    // v11: DocumentRuntime's Grasshopper half is nullable — a saved Rhino document with no .gh open
    //      is a complete target (document work needs no canvas). rhinoLayerTable resources are now
    //      addressed by the Rhino-scoped ProjectId instead of the bound Grasshopper document id.
    // v12: rhino.structuralExtract read operation (structural member axis extraction). Purely
    //      additive, but a new host calling an old plugin would fail mid-feature with an unknown
    //      operation instead of at connect — the bump keeps mixed installs failing loudly.
    // v13: adapter-owner rename (wireify → script, cordycepsCanvas → canvas, cordycepsRhino →
    //      rhinoScene) changed the BridgeAdapterOwner wire strings; a mixed install would
    //      misroute every operation, so it must fail at connect.
    // v14: canvas.catalog GUID-lookup semantics became load-bearing — the AgentHost's
    //      canvas.create preflight rejects a create when a GUID catalog query returns an
    //      explicit empty Matches list, which only a plugin with the GUID catalog branch can
    //      answer honestly. An older plugin fuzzy-matches the GUID string to zero results
    //      (SUCCESS + empty Matches), which would false-reject every valid create; mixed
    //      installs must fail loudly at connect. AgentHost and plugin ship in one Yak package.
    public const int Version = 14;

    public const int DefaultMaximumFrameBytes = 8 * 1024 * 1024;

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
        };
        // Registered before the general enum converter so it wins for AdapterOwner: stored
        // ChangeSets (live-jobs.db) and pre-upgrade sessions may still carry legacy owner names.
        options.Converters.Add(new GPTino.Contracts.LegacyAdapterOwnerConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

public enum BridgeMessageKind
{
    Hello,
    Challenge,
    Authenticate,
    Authenticated,
    Request,
    Response,
    Event,
    Error,
    Shutdown,
}

public sealed record BridgeFrame
{
    public required int ProtocolVersion { get; init; }

    public required Guid MessageId { get; init; }

    public Guid? CorrelationId { get; init; }

    public required BridgeMessageKind Kind { get; init; }

    public DocumentTarget? Target { get; init; }

    public required string PayloadType { get; init; }

    public required JsonElement Payload { get; init; }

    public string? ErrorCode { get; init; }

    public static BridgeFrame Create<T>(
        BridgeMessageKind kind,
        string payloadType,
        T payload,
        DocumentTarget? target = null,
        Guid? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadType);

        return new BridgeFrame
        {
            ProtocolVersion = BridgeProtocol.Version,
            MessageId = Guid.NewGuid(),
            CorrelationId = correlationId,
            Kind = kind,
            Target = target,
            PayloadType = payloadType,
            Payload = JsonSerializer.SerializeToElement(payload, BridgeProtocol.JsonOptions),
        };
    }

    public T DeserializePayload<T>() =>
        Payload.Deserialize<T>(BridgeProtocol.JsonOptions)
        ?? throw new JsonException($"Payload '{PayloadType}' deserialized to null.");

    public void Validate(bool requireTargetForApplicationMessage = true)
    {
        if (ProtocolVersion != BridgeProtocol.Version)
        {
            throw new BridgeProtocolException(
                "protocol_version",
                $"Unsupported bridge protocol {ProtocolVersion}; expected {BridgeProtocol.Version}.");
        }

        if (MessageId == Guid.Empty)
        {
            throw new BridgeProtocolException("message_id", "MessageId is required.");
        }

        if (string.IsNullOrWhiteSpace(PayloadType))
        {
            throw new BridgeProtocolException("payload_type", "PayloadType is required.");
        }

        var isApplicationMessage = Kind is BridgeMessageKind.Request or
            BridgeMessageKind.Response or BridgeMessageKind.Event;
        if (requireTargetForApplicationMessage && isApplicationMessage && Target is null)
        {
            throw new BridgeProtocolException(
                "target_required",
                "Application bridge messages must carry an explicit document target.");
        }

        Target?.Validate();
    }
}

public sealed class BridgeProtocolException : IOException
{
    public BridgeProtocolException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
