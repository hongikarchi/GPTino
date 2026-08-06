namespace GPTino.Contracts;

public enum SessionRunState
{
    Idle,
    Drafting,
    Ready,
    Running,
    Paused,
    Blocked,
    WaitingForDependency,
    Completed,
    Failed,
}

public sealed record SessionOrderSnapshot(
    Guid ProjectId,
    IReadOnlyList<Guid> OrderedSessionIds,
    long Version);

public sealed record SessionOrderChange(
    Guid ProjectId,
    long ExpectedVersion,
    IReadOnlyList<Guid> OrderedSessionIds);

public enum SessionOrderChangeStatus
{
    Applied,
    ProjectNotFound,
    VersionMismatch,
    DuplicateSession,
    InvalidMembership,
}

public sealed record SessionOrderChangeResult(
    SessionOrderChangeStatus Status,
    SessionOrderSnapshot? Snapshot,
    string? Message)
{
    public bool Applied => Status == SessionOrderChangeStatus.Applied;
}
