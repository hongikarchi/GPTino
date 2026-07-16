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
    RhinoPrimitiveAttributes? Attributes = null) : IRhinoSceneMutationRequest;

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
    string? ExpectedFingerprint) : IRhinoSceneMutationRequest;

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
    string ExpectedFingerprint) : IRhinoSceneMutationRequest;

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
    RhinoTransformMatrix Matrix) : IRhinoSceneMutationRequest;

public sealed record RhinoSceneMutationResult(
    string OperationId,
    bool Changed,
    string? BeforeFingerprint,
    string? AfterFingerprint,
    Guid? ObjectId,
    RhinoSceneObjectState? State = null,
    IReadOnlyList<BridgeDiagnostic>? Diagnostics = null);
