namespace GPTino.Contracts;

public enum AdapterOwner
{
    Wireify,
    Cordyceps,
    RhinoBridge,
}

public enum OperationKind
{
    Read,
    MoveComponent,
    ConnectWire,
    DisconnectWire,
    SetValue,
    Rename,
    UpdatePythonSource,
    SetComponentIo,
    ConvertSocket,
    CreateComponent,
    DeleteComponent,
    SetLayout,
    SetSolverState,
    CreateRhinoObject,
    ModifyRhinoObject,
    DeleteRhinoObject,
    BakeGeometry,
    UpdateRhinoAttributes,
    UpdateRhinoLayer,
    DocumentGlobal,
    SetGroup,
    ExecutePython,
    ReadRuntimeMessages,
    CreateRhinoPrimitive,
    TransformRhinoObject,
}

public sealed record ResourceExpectation(
    ResourceAddress Resource,
    string ExpectedFingerprint)
{
    /// <summary>
    /// Optimistic-concurrency sentinel for a resource that must not exist.
    /// It is valid only for explicitly supported create operations.
    /// </summary>
    public const string AbsentFingerprint = "gptino:absent";

    /// <summary>
    /// Opt-in sentinel that asks the server to fill in the current fingerprint from live state — but only
    /// when the resource is unchanged since THIS session last committed it (a self-sequential write). A
    /// foreign-session write or manual Grasshopper drift is refused and blocks the job, so optimistic
    /// concurrency stays safe. Not valid for create targets (use <see cref="AbsentFingerprint"/>).
    /// </summary>
    public const string AutoFingerprint = "gptino:auto";

    /// <summary>
    /// Sentinel <c>BaseSnapshotRevision</c> that opts a ChangeSet out of the whole-document revision gate;
    /// per-resource auto expectations still govern every resource the ChangeSet touches.
    /// </summary>
    public const long AutoBaseRevision = -1;

    public bool ExpectsAbsence =>
        string.Equals(ExpectedFingerprint, AbsentFingerprint, StringComparison.Ordinal);

    public bool IsAuto =>
        string.Equals(ExpectedFingerprint, AutoFingerprint, StringComparison.Ordinal);
}

public sealed record TypedOperation(
    string OperationId,
    OperationKind Kind,
    AdapterOwner Owner,
    IReadOnlyList<ResourceAddress> Reads,
    IReadOnlyList<ResourceAddress> Writes,
    bool Reversible,
    string? PayloadArtifact = null,
    string? PayloadSha256 = null);

public enum PredicateKind
{
    FingerprintEquals,
    RuntimeErrorAbsent,
    OutputEquals,
    WireExists,
    WireAbsent,
    ObjectExists,
    ObjectAbsent,
    BoundingBoxEquals,
    Custom,
}

public sealed record VerificationPredicate(
    string Name,
    PredicateKind Kind,
    ResourceAddress? Resource,
    string? ExpectedValue);

public sealed record RollbackBeforeImage(
    ResourceAddress Resource,
    string ArtifactReference,
    string Fingerprint);

public sealed record ChangeSet(
    Guid ChangeSetId,
    Guid ProjectId,
    Guid SessionId,
    long BaseSnapshotRevision,
    string? BaseGitCommit,
    IReadOnlyList<Guid> Dependencies,
    IReadOnlyList<ResourceExpectation> ReadSet,
    IReadOnlyList<ResourceExpectation> WriteSet,
    IReadOnlyList<TypedOperation> Operations,
    IReadOnlyList<VerificationPredicate> AcceptancePredicates,
    IReadOnlyList<RollbackBeforeImage> RollbackBeforeImages,
    DateTimeOffset CreatedAt);

public enum JobState
{
    Draft,
    Queued,
    Validating,
    Executing,
    Verifying,
    Committed,
    RolledBack,
    Blocked,
    RecoveryRequired,
    Failed,
    Cancelled,
}

public sealed record QueuedJob(
    Guid JobId,
    ChangeSet ChangeSet,
    long EnqueueSequence,
    DateTimeOffset EnqueuedAt,
    bool IsSystemRecovery = false);

public sealed record JobExecutionResult(
    Guid JobId,
    JobState State,
    string? Message = null)
{
    public bool Succeeded => State == JobState.Committed;
}
