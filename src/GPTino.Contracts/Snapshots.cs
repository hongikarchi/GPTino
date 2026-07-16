namespace GPTino.Contracts;

public enum TrackingState
{
    Managed,
    Untracked,
    Drifted,
    Unsupported,
}

public enum ResourceKind
{
    Document,
    GrasshopperComponent,
    GrasshopperComponentSource,
    GrasshopperComponentIo,
    GrasshopperComponentValue,
    GrasshopperComponentLayout,
    GrasshopperWire,
    GrasshopperGroup,
    GrasshopperSolver,
    RhinoObject,
    RhinoObjectGeometry,
    RhinoObjectAttributes,
    RhinoLayer,
    RhinoGroup,
    RhinoMaterial,
    RhinoLinetype,
}

/// <summary>
/// Addresses one conflict domain. A field of "*" means the whole resource.
/// </summary>
public sealed record ResourceAddress(ResourceKind Kind, string Id, string Field = "*");

public sealed record ResourceFingerprint(
    ResourceAddress Resource,
    string Fingerprint,
    TrackingState TrackingState = TrackingState.Managed);

public sealed record StateSnapshot(
    Guid ProjectId,
    long Revision,
    string? GitCommit,
    DateTimeOffset CapturedAt,
    DocumentRuntime Target,
    IReadOnlyList<ResourceFingerprint> Resources);
