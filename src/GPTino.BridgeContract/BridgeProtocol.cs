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
    public const int Version = 5;

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
