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
    DateTimeOffset UpdatedAt,
    string? GrasshopperDoc = null,
    // Opt-in: when true, the session's Codex thread gets a native goal (objective + optional budget)
    // via thread/goal/set. Off by default.
    bool GoalEnabled = false,
    // Orthogonal to Role: Role says WHO the session is (modeler|curator|read-only, fixed at
    // creation), Mode says HOW it currently runs (auto|plan, user-toggleable). Before the curator
    // work plan mode was encoded by rewriting Role to 'planner', which made a mode flip erase the
    // session's identity.
    string Mode = "auto");

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
    // Reasoning-effort level (low|medium|high|xhigh|max|ultra); field name kept as ModelProfile for
    // wire back-compat. Default xhigh. No adaptive routing — the value is used directly (clamped to
    // the model's supported efforts at turn time).
    string ModelProfile = "xhigh",
    string? Model = null,
    string? GrasshopperDoc = null,
    bool GoalEnabled = false);

/// <summary>
/// Rebinds a session to one Grasshopper document (a durable docKey) or clears the binding (null =
/// default document resolution: the only registered document when exactly one is open).
/// </summary>
public sealed record SetSessionTargetRequest(string? GrasshopperDoc = null);

/// <summary>One (objectId, fingerprint) pair the approval card displayed.</summary>
public sealed record ApprovalGrantItem(Guid ObjectId, string Fingerprint);

public sealed record MintApprovalGrantRequest(IReadOnlyList<ApprovalGrantItem> Items);

public sealed record ReorderSessionsRequest(
    IReadOnlyList<Guid> OrderedSessionIds,
    long OrderVersion);

public sealed record SendMessageRequest(
    string Content,
    string? ClientMessageId = null,
    IReadOnlyList<IncomingAttachment>? Attachments = null);

/// <summary>A file the panel attached to a message, carried as Base64 over the loopback API.</summary>
public sealed record IncomingAttachment(string FileName, string MediaType, string DataBase64);

public sealed record SetPausedRequest(bool Paused);

public sealed record SetModeRequest(string Mode);

public sealed record SetModelRequest(string ModelProfile, string? Model = null);

public sealed record SetGoalRequest(bool Enabled);

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

public sealed record ArchivedSession(
    Guid Id,
    string Name,
    string State,
    DateTimeOffset UpdatedAt,
    int MessageCount);

public sealed record ArchivedProject(
    string Fingerprint,
    string? ProjectName,
    string? RhinoFile,
    string? GrasshopperFile,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastActivityAt,
    int SessionCount,
    bool Current,
    bool Available,
    IReadOnlyList<ArchivedSession> Sessions);

public sealed record ArchivedMessage(
    long Id,
    string Role,
    string Content,
    string? Phase,
    DateTimeOffset CreatedAt);

public sealed record ApiError(string Code, string Message);
