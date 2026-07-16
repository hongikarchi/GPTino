namespace GPTino.AgentHost.Api;

public static class SessionStates
{
    public const string Idle = "idle";
    public const string Running = "running";
    public const string Waiting = "waiting";
    public const string Paused = "paused";
    public const string Failed = "failed";
}

public sealed record SessionRecord(
    Guid Id,
    string Name,
    string Role,
    string ModelProfile,
    string? Model,
    string State,
    int Order,
    string? CodexThreadId,
    string? CurrentTask,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ChatMessage(
    long Id,
    Guid SessionId,
    string Role,
    string Content,
    string? Phase,
    DateTimeOffset CreatedAt);

public sealed record CreateSessionRequest(
    string Name,
    string Role = "modeler",
    string ModelProfile = "auto",
    string? Model = null);

public sealed record ReorderSessionsRequest(
    IReadOnlyList<Guid> OrderedSessionIds,
    long OrderVersion);

public sealed record SendMessageRequest(string Content, string? ClientMessageId = null);

public sealed record SetPausedRequest(bool Paused);

public sealed record SetModeRequest(string Mode);

public sealed record SetModelRequest(string ModelProfile, string? Model = null);

public sealed record RuntimeStatus(
    string State,
    Guid ProjectId,
    string? RhinoPath,
    string? GrasshopperPath,
    bool BridgeConnected,
    string? WriterSessionId,
    int QueueLength,
    DateTimeOffset StartedAt);

public sealed record HostStateResponse(
    RuntimeStatus Runtime,
    IReadOnlyList<SessionRecord> Sessions,
    long OrderVersion);

public sealed record ModelView(
    string Id,
    string Model,
    string DisplayName,
    string Description,
    bool IsDefault,
    IReadOnlyList<string> ReasoningEfforts);

public sealed record AcceptedTurn(Guid SessionId, long MessageId, string State);

public sealed record ApiError(string Code, string Message);
