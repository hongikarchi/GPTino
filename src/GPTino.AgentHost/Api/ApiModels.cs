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
    // The goal card as opaque JSON (GoalCard shape below), or null before the agent has framed
    // one. The store persists it; the agent proposes it and the user confirms it.
    string? GoalCard = null,
    // A pending/answered approval card as opaque JSON (ApprovalCard shape), or null.
    string? ApprovalCard = null);

/// <summary>
/// What the agent understood the user to be asking for, framed BEFORE the work starts so the
/// user can correct it cheaply: the objective in one line, the criteria that will decide whether
/// it worked, the assumptions the agent had to make, and what it is deliberately leaving out.
/// The options are the user's structured replies (approve / narrow / correct …), each optionally
/// carrying Rhino object ids so choosing one can also show what it means in the viewport.
/// Lifecycle: proposing -> confirmed -> scored (or rejected).
/// </summary>
public sealed record GoalCard(
    string Status,
    string Objective,
    IReadOnlyList<string> Criteria,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<string> OutOfScope,
    IReadOnlyList<GoalOption>? Options = null,
    string? ChosenOption = null,
    IReadOnlyList<GoalCriterionScore>? Scores = null,
    DateTimeOffset? ProposedAt = null,
    DateTimeOffset? ConfirmedAt = null);

public sealed record GoalOption(string Id, string Label, string? Detail = null, IReadOnlyList<Guid>? ObjectIds = null);

/// <summary>One criterion's verdict. Evidence must quote a job/predicate result, never a claim.</summary>
public sealed record GoalCriterionScore(string Criterion, bool Passed, string Evidence);

public sealed record ChatMessage(
    long Id,
    Guid SessionId,
    string Role,
    string Content,
    string? Phase,
    DateTimeOffset CreatedAt);

public sealed record CreateSessionRequest(
    string Name,
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
/// <summary>
/// A destructive fix the agent wants to make to geometry the USER owns. The broker refuses those
/// by default, so the agent lists exactly what it would touch and the user picks. Each item is
/// pinned to the fingerprint that was audited: if the object moved since, the grant no longer
/// matches it and the fix fails instead of hitting something else (approve-what-you-saw).
/// Lifecycle: proposing -> granted (carries grantId) or rejected.
/// </summary>
public sealed record ApprovalCard(
    string Status,
    string Summary,
    IReadOnlyList<ApprovalItem> Items,
    string? GrantId = null,
    IReadOnlyList<string>? ApprovedItemIds = null,
    DateTimeOffset? ProposedAt = null,
    // The option the user picked per item (item id -> chosen label), for the items whose fix the
    // machine must NOT decide. Persisted because it IS the answer: without it the next turn carries
    // a grant covering both near-duplicates and no word on which one the user meant to lose.
    IReadOnlyDictionary<string, string>? Choices = null);

/// <summary>
/// One reviewable fix. Choices exist for findings where the machine must not decide — which of two
/// near-duplicates to keep is the user's call, because design-option stacks are intentional.
/// </summary>
public sealed record ApprovalItem(
    string Id,
    string Label,
    string? Measure,
    IReadOnlyList<ApprovalGrantItem> Targets,
    IReadOnlyList<string>? Choices = null);

/// <summary>The user's answer: which items to grant, plus any per-item choice they made.</summary>
public sealed record AnswerApprovalRequest(
    string Status,
    IReadOnlyList<string>? ApprovedItemIds = null,
    IReadOnlyDictionary<string, string>? Choices = null);

/// <summary>
/// One (objectId, fingerprint) target the approval card displays. Label/Role/Impact are optional
/// model-authored display strings (SHARED CONTRACT with the panel): label = short name, role =
/// what the component does in the definition, impact = what changes if it is deleted / what
/// replaces it. Absent on legacy cards — they keep deserializing (null) and the panel simply
/// omits those lines. Grant minting still binds ONLY (ObjectId, Fingerprint).
/// </summary>
public sealed record ApprovalGrantItem(
    Guid ObjectId,
    string Fingerprint,
    string? Label = null,
    string? Role = null,
    string? Impact = null);

/// <summary>Panel viewport focus: mode is select | isolate | lock | restore.</summary>
public sealed record FocusRequest(IReadOnlyList<Guid>? ObjectIds, string? Mode, bool? Zoom);

/// <summary>Panel canvas focus: select + frame the given Grasshopper components (no mode; view-only).</summary>
public sealed record CanvasFocusEndpointRequest(IReadOnlyList<Guid>? ObjectIds, bool? Zoom);

/// <summary>Prose language for GPTino's answers: "ko" or "en" (anything else reads as "en").</summary>
public sealed record LanguageSetting(string Language);

public sealed record ReorderSessionsRequest(
    IReadOnlyList<Guid> OrderedSessionIds,
    long OrderVersion);

public sealed record SendMessageRequest(
    string Content,
    string? ClientMessageId = null,
    IReadOnlyList<IncomingAttachment>? Attachments = null,
    // A selection the user pinned in the composer at send time (like an attachment). When present it
    // is the AUTHORITATIVE operand for this turn — the objects to act on — not the live-selection
    // discovery hint. Lets the user pin, then keep working in Rhino/GH without holding the selection.
    PinnedSelection? PinnedSelection = null);

/// <summary>A file the panel attached to a message, carried as Base64 over the loopback API.</summary>
public sealed record IncomingAttachment(string FileName, string MediaType, string DataBase64);

/// <summary>
/// A user-pinned selection snapshot: exactly the Rhino objects and/or Grasshopper components the
/// message targets. Ids fix WHICH objects; fingerprints are still resolved live before any write.
/// </summary>
public sealed record PinnedSelection(
    IReadOnlyList<Guid>? RhinoObjectIds = null,
    IReadOnlyList<PinnedGrasshopperObject>? GrasshopperObjects = null);

public sealed record PinnedGrasshopperObject(Guid Id, string? Label = null);

public sealed record SetPausedRequest(bool Paused);

public sealed record SetModelRequest(string ModelProfile, string? Model = null);

/// <summary>Rename a session; the name becomes its display title.</summary>
public sealed record RenameSessionRequest(string Name);

/// <summary>
/// The user's answer to a proposed goal card. Status is "confirmed" or "rejected"; the optional
/// edits let the user correct the objective/criteria before approving (approve-what-you-saw:
/// whatever comes back is what the agent is held to).
/// </summary>
public sealed record SetGoalRequest(
    string Status,
    string? ChosenOption = null,
    string? Objective = null,
    IReadOnlyList<string>? Criteria = null);

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
    int MessageCount,
    bool Deleted);

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
