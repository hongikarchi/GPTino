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
    ReferenceRhinoObjects,
    FixRhinoEndpointPair,
    // Phase 4/5 curator surface. UpdateRhinoLayer above stays reserved (it bundled rename and
    // re-parent, whose descendant-path rewrites are still out of scope); these are the narrow,
    // provable operations that replace it.
    PurgeTableEntries,
    MoveObjectsToLayer,
    UpdateRhinoLayerProperties,
    DeleteRhinoLayer,
    SaveRhinoLayerState,
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
    /// <summary>
    /// A named output of a component solves to an item count within [min,max]. ExpectedValue is
    /// "outputName:min:max" (max may be "*" for no upper bound). Verified against the post-solve
    /// output inspection — a real semantic check beyond mere existence.
    /// </summary>
    OutputCountInRange,
    /// <summary>A named output's geometry is all closed (curve IsClosed / Brep solid / mesh closed). ExpectedValue = "outputName".</summary>
    GeometryClosed,
    /// <summary>A named output's summed surface area is within [min,max]. ExpectedValue = "outputName:min:max" (max may be "*").</summary>
    AreaInRange,
    /// <summary>A named output's data-tree branch count is within [min,max]. ExpectedValue = "outputName:min:max" (max may be "*").</summary>
    DataTreeBranchCountInRange,
    /// <summary>A named output's summed closed volume is within [min,max]. ExpectedValue = "outputName:min:max" (max may be "*").</summary>
    VolumeInRange,
    /// <summary>A named output's bounding-box extent on an axis is within [min,max]. ExpectedValue = "outputName:axis:min:max" where axis = x|y|z|diagonal.</summary>
    BoundingBoxInRange,
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
    DateTimeOffset CreatedAt,
    // Optional user-approval reference (panel-issued): authorizes destructive operations on
    // objects WITHOUT GPTino provenance stamps, bound server-side to the exact
    // (objectId, fingerprint) pairs the user saw. Rides the ChangeSet so it is covered by the
    // idempotency hash; absent on GPTino-created objects (autonomy-by-default).
    string? ApprovalGrantId = null);

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
