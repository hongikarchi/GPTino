namespace GPTino.Contracts;

public enum ModelProfile
{
    ReadFast,
    FastSafe,
    Standard,
    HighAssurance,
    Recovery,
}

public enum ReasoningEffort
{
    Low,
    Medium,
    High,
    ExtraHigh,
    Maximum,
}

public enum ModelCapabilityTier
{
    ReadOnly,
    FastWrite,
    StandardWrite,
    DeepReasoning,
    Recovery,
}

public sealed record ModelDescriptor(
    string Id,
    ModelCapabilityTier CapabilityTier,
    bool Available = true,
    bool QualifiedForLiveWrites = true);

public enum TaskClass
{
    ReadOnly,
    SimpleDeterministicWrite,
    StandardWrite,
    ComplexWrite,
    Recovery,
}

[Flags]
public enum RiskFlags : long
{
    None = 0,
    UnknownTarget = 1L << 0,
    AmbiguousTarget = 1L << 1,
    WireCycle = 1L << 2,
    TypeMismatch = 1L << 3,
    PythonChange = 1L << 4,
    IoSchemaChange = 1L << 5,
    GeometryTopologyChange = 1L << 6,
    Delete = 1L << 7,
    Bake = 1L << 8,
    SolverGlobal = 1L << 9,
    OpaquePluginState = 1L << 10,
    ExternalReference = 1L << 11,
    ManualDrift = 1L << 12,
    CrossSessionDependency = 1L << 13,
    RuntimeFailure = 1L << 14,
    MultiDocument = 1L << 15,
    NonReversible = 1L << 16,
    Unsupported = 1L << 17,
    UnqualifiedModelVersion = 1L << 18,
    LargeScope = 1L << 19,
}

public sealed record RoutingRequest(
    Guid TaskId,
    QualityPolicy RequestedQuality,
    bool IsRecovery,
    bool TargetIsExact,
    int ResourceCount,
    IReadOnlyList<TypedOperation> Operations,
    RiskFlags RiskFlags = RiskFlags.None,
    string? PinnedModelId = null);

public sealed record RoutingEscalation(
    ModelProfile From,
    ModelProfile To,
    string Reason);

public sealed record RoutingDecision(
    string RouterVersion,
    TaskClass TaskClass,
    RiskFlags RiskFlags,
    ModelProfile RequestedProfile,
    ModelProfile EffectiveProfile,
    string EffectiveModel,
    ReasoningEffort Reasoning,
    double Confidence,
    IReadOnlyList<string> RouteEvidence,
    IReadOnlyList<RoutingEscalation> EscalationHistory);

public sealed record FastSafeEvaluation(
    bool Eligible,
    IReadOnlyList<string> Reasons);
