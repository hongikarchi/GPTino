using GPTino.BridgeContract;

namespace GPTino.CordycepsAdapter;

/// <summary>Owns supported Rhino scene objects and tables for one explicit Rhino document.</summary>
public interface ICordycepsRhinoSceneAdapter
{
    Task<RhinoSceneListResult> ListObjectsAsync(
        DocumentTarget target,
        RhinoListObjectsRequest request,
        CancellationToken cancellationToken = default);

    Task<RhinoSceneObjectState> InspectObjectAsync(
        DocumentTarget target,
        Guid objectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates GPTino-stamped objects (GPTino.LogicalEntityId / gptino_bake_family user
    /// strings) grouped by source GH document and bake family. Read-only; feeds the data-flow
    /// bake ledger. Objects stamped before GPTino.SourceDocKey existed land in the
    /// null-SourceDocKey (unattributed) groups — never guessed onto a document.
    /// </summary>
    Task<StampedObjectsResult> ListStampedObjectsAsync(
        DocumentTarget target,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deterministic document-hygiene audit: near-miss curve endpoints, near-duplicate geometry
    /// SelDup cannot catch, or purge candidates (unused block definitions, empty layers, bad
    /// objects). Read-only — DETECTION is server code; the model only triages findings. Every
    /// finding carries the fingerprints of its objects so a follow-up fix ChangeSet is CAS-pinned
    /// to exactly what was audited, and every result names the tolerance and units it was
    /// computed under.
    /// </summary>
    Task<RhinoAuditResult> AuditAsync(
        DocumentTarget target,
        RhinoAuditRequest request,
        CancellationToken cancellationToken = default);

    Task<RhinoSceneMutationResult> CreatePrimitiveAsync(
        DocumentTarget target,
        CreateRhinoPrimitiveRequest request,
        CancellationToken cancellationToken = default);

    Task<RhinoSceneMutationResult> UpsertObjectAsync(
        DocumentTarget target,
        UpsertRhinoObjectRequest request,
        CancellationToken cancellationToken = default);

    Task<RhinoUpsertValidationResult> ValidateUpsertObjectAsync(
        DocumentTarget target,
        UpsertRhinoObjectRequest request,
        CancellationToken cancellationToken = default);

    Task<RhinoSceneMutationResult> DeleteObjectAsync(
        DocumentTarget target,
        DeleteRhinoObjectRequest request,
        CancellationToken cancellationToken = default);

    Task<RhinoSceneMutationResult> EnsureLayerAsync(
        DocumentTarget target,
        EnsureRhinoLayerRequest request,
        CancellationToken cancellationToken = default);

    Task<RhinoSceneMutationResult> TransformObjectAsync(
        DocumentTarget target,
        TransformRhinoObjectRequest request,
        CancellationToken cancellationToken = default);

    Task<RhinoSceneMutationResult> FixEndpointPairAsync(
        DocumentTarget target,
        FixEndpointPairRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Bounded, deterministic scene query. Text filters are case-insensitive; IDs and logical entity
/// IDs are exact. A null filter is ignored. Limit must be between 1 and 500.
/// </summary>
public sealed record RhinoListObjectsRequest(
    int Limit = 100,
    Guid? ObjectId = null,
    Guid? LayerId = null,
    string? LayerFullPath = null,
    string? Name = null,
    string? NameContains = null,
    string? GeometryType = null,
    string? LogicalEntityId = null,
    bool? Selected = null);

public sealed record RhinoSceneListResult(
    int Limit,
    int ReturnedCount,
    bool Truncated,
    RhinoBoundingBoxSummary? Bounds,
    IReadOnlyList<RhinoSceneObjectSummary> Objects,
    string Fingerprint);

public sealed record RhinoSceneObjectSummary(
    Guid ObjectId,
    string LogicalEntityId,
    string Name,
    string GeometryType,
    Guid LayerId,
    string LayerFullPath,
    bool Selected,
    RhinoBoundingBoxSummary? Bounds,
    string Fingerprint);

public sealed record RhinoBoundingBoxSummary(
    RhinoPoint3d Minimum,
    RhinoPoint3d Maximum,
    RhinoPoint3d Center,
    RhinoVector3d Size);

public sealed record RhinoPoint3d(double X, double Y, double Z);

public sealed record RhinoVector3d(double X, double Y, double Z);

public interface IRhinoSceneMutationRequest
{
    string OperationId { get; }
}

public sealed record RhinoSceneObjectState(
    Guid ObjectId,
    string LogicalEntityId,
    string GeometryType,
    string GeometryJson,
    string AttributesJson,
    string Fingerprint);

public enum RhinoPrimitiveKind
{
    Point,
    Line,
    Polyline,
    Circle,
    Box,
    Sphere,
}

/// <summary>
/// Exactly one primitive definition matching Kind must be supplied. ObjectId is mandatory so the
/// caller, managed history, and Rhino document retain one stable identity.
/// </summary>
public sealed record CreateRhinoPrimitiveRequest(
    string OperationId,
    Guid ObjectId,
    string LogicalEntityId,
    RhinoPrimitiveKind Kind,
    RhinoPointPrimitive? Point = null,
    RhinoLinePrimitive? Line = null,
    RhinoPolylinePrimitive? Polyline = null,
    RhinoCirclePrimitive? Circle = null,
    RhinoBoxPrimitive? Box = null,
    RhinoSpherePrimitive? Sphere = null,
    RhinoPrimitiveAttributes? Attributes = null,
    // Server-injected provenance, same contract as UpsertRhinoObjectRequest.SourceDocKey: the
    // submit validator rejects model-authored values; the executor stamps the job's docKey.
    string? SourceDocKey = null) : IRhinoSceneMutationRequest;

public sealed record RhinoPointPrimitive(RhinoPoint3d Location);

public sealed record RhinoLinePrimitive(RhinoPoint3d From, RhinoPoint3d To);

public sealed record RhinoPolylinePrimitive(
    IReadOnlyList<RhinoPoint3d> Vertices,
    bool Closed = false);

public sealed record RhinoCirclePrimitive(
    RhinoPoint3d Center,
    RhinoVector3d Normal,
    double Radius);

/// <summary>World-axis-aligned box. Every Maximum component must exceed Minimum.</summary>
public sealed record RhinoBoxPrimitive(RhinoPoint3d Minimum, RhinoPoint3d Maximum);

public sealed record RhinoSpherePrimitive(RhinoPoint3d Center, double Radius);

public sealed record RhinoPrimitiveAttributes(
    string? Name = null,
    Guid? LayerId = null,
    int? ArgbColor = null);

public sealed record UpsertRhinoObjectRequest(
    string OperationId,
    Guid ObjectId,
    string LogicalEntityId,
    string GeometryType,
    string GeometryJson,
    string AttributesJson,
    string? ExpectedFingerprint,
    // Server-injected provenance: the durable docKey of the GH document whose job produced this
    // write. Model payloads must NOT set it (the submit validator rejects non-null values); the
    // executor stamps it after freezing, so bake attribution cannot be spoofed.
    string? SourceDocKey = null,
    bool Approved = false) : IRhinoSceneMutationRequest;

/// <summary>
/// Audit query. Kind: nearMissEndpoints | nearDuplicates | purgeCandidates. Tolerance defaults to
/// the document's absolute tolerance; the near-miss band is (tolerance, tolerance * BandFactor].
/// Limit bounds returned findings (1..100); analyzers also carry internal scan caps and report
/// Truncated honestly instead of silently sampling.
/// </summary>
public sealed record RhinoAuditRequest(
    string Kind,
    double? Tolerance = null,
    double? BandFactor = null,
    int Limit = 50);

public sealed record RhinoAuditFinding(
    string FindingId,
    string Kind,
    IReadOnlyList<Guid> ObjectIds,
    IReadOnlyList<string> Fingerprints,
    double? Measure,
    string Detail,
    IReadOnlyList<string> ProposedFixes,
    // nearMissEndpoints only: which end of each object (0=start, 1=end), parallel to ObjectIds —
    // fixEndpointPair REQUIRES these; prose in Detail is not machine-readable.
    IReadOnlyList<int>? EndIndices = null);

public sealed record RhinoAuditResult(
    string Kind,
    double DocTolerance,
    string DocUnits,
    double ToleranceUsed,
    double? BandUsed,
    int ScannedObjects,
    IReadOnlyList<RhinoAuditFinding> Findings,
    bool Truncated,
    string Fingerprint);

public sealed record StampedObjectGroup(
    string? SourceDocKey,
    string? BakeFamily,
    int Count,
    IReadOnlyList<Guid> ObjectIds);

public sealed record StampedObjectsResult(
    int TotalStamped,
    IReadOnlyList<StampedObjectGroup> Groups,
    string Fingerprint);

public sealed record RhinoUpsertValidationResult(
    string OperationId,
    Guid ObjectId,
    string ActualGeometryType,
    bool ExistingObject,
    string? ExistingFingerprint,
    bool IsValid);

public sealed record DeleteRhinoObjectRequest(
    string OperationId,
    Guid ObjectId,
    string ExpectedFingerprint,
    // Server-injected when the ChangeSet carries a user approval grant covering this object;
    // model payloads must not set it (submit validation rejects). Without it, destructive ops on
    // objects lacking GPTino provenance stamps are refused — the human-wins default-deny.
    bool Approved = false) : IRhinoSceneMutationRequest;

/// <summary>
/// Heals one audited near-miss endpoint pair by moving ONE curve's endpoint (B) onto the anchor
/// curve's endpoint (A). A is only referenced (fingerprint-verified, unchanged); B is the single
/// write target — which curve moves is the user's triage decision. The fix is verified BEFORE the
/// write: the modified duplicate must be valid and its endpoint must land within Tolerance of the
/// anchor, else the operation fails with no document change.
/// </summary>
public sealed record FixEndpointPairRequest(
    string OperationId,
    Guid AnchorObjectId,
    int AnchorEnd,
    Guid MoveObjectId,
    int MoveEnd,
    string ExpectedAnchorFingerprint,
    string ExpectedFingerprint,
    double Tolerance,
    bool Approved = false) : IRhinoSceneMutationRequest;

public sealed record EnsureRhinoLayerRequest(
    string OperationId,
    Guid LayerId,
    string FullPath,
    int ArgbColor,
    Guid? ParentLayerId) : IRhinoSceneMutationRequest;

/// <summary>Row-major affine 4x4 matrix.</summary>
public sealed record RhinoTransformMatrix(
    double M00,
    double M01,
    double M02,
    double M03,
    double M10,
    double M11,
    double M12,
    double M13,
    double M20,
    double M21,
    double M22,
    double M23,
    double M30,
    double M31,
    double M32,
    double M33);

public sealed record TransformRhinoObjectRequest(
    string OperationId,
    Guid ObjectId,
    string ExpectedFingerprint,
    RhinoTransformMatrix Matrix,
    bool Approved = false) : IRhinoSceneMutationRequest;

public sealed record RhinoSceneMutationResult(
    string OperationId,
    bool Changed,
    string? BeforeFingerprint,
    string? AfterFingerprint,
    Guid? ObjectId,
    RhinoSceneObjectState? State = null,
    IReadOnlyList<BridgeDiagnostic>? Diagnostics = null);
