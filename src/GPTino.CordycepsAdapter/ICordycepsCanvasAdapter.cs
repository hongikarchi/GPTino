using GPTino.BridgeContract;

namespace GPTino.CordycepsAdapter;

/// <summary>
/// Owns Grasshopper canvas structure, topology, layout, groups, and snapshots.
/// Python source and parameter typing are deliberately absent; those belong to Wireify.
/// </summary>
public interface ICordycepsCanvasAdapter
{
    Task<CanvasSnapshot> CaptureSnapshotAsync(
        DocumentTarget target,
        CancellationToken cancellationToken = default);

    Task<CanvasObjectState> InspectObjectAsync(
        DocumentTarget target,
        Guid objectId,
        CancellationToken cancellationToken = default);

    Task<CanvasOutputInspection> InspectOutputsAsync(
        DocumentTarget target,
        InspectCanvasOutputsRequest request,
        CancellationToken cancellationToken = default);

    Task<ComponentCatalogSearchResult> SearchComponentCatalogAsync(
        DocumentTarget target,
        ComponentCatalogSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<CanvasMutationResult> CreateObjectAsync(
        DocumentTarget target,
        CreateCanvasObjectRequest request,
        CancellationToken cancellationToken = default);

    Task<CanvasMutationResult> DeleteObjectAsync(
        DocumentTarget target,
        DeleteCanvasObjectRequest request,
        CancellationToken cancellationToken = default);

    Task<CanvasMutationResult> MoveObjectsAsync(
        DocumentTarget target,
        MoveCanvasObjectsRequest request,
        CancellationToken cancellationToken = default);

    Task<CanvasMutationResult> SetNumberSliderValueAsync(
        DocumentTarget target,
        SetNumberSliderValueRequest request,
        CancellationToken cancellationToken = default);

    Task<CanvasMutationResult> SetWireAsync(
        DocumentTarget target,
        SetWireRequest request,
        CancellationToken cancellationToken = default);

    Task<CanvasMutationResult> SetGroupAsync(
        DocumentTarget target,
        SetGroupRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record CanvasSnapshot(
    Guid GrasshopperDocumentId,
    string DocumentFingerprint,
    IReadOnlyList<CanvasObjectState> Objects,
    IReadOnlyList<WireState> Wires,
    IReadOnlyList<GroupState> Groups);

public sealed record CanvasObjectState(
    Guid ObjectId,
    Guid ComponentTypeId,
    string Name,
    CanvasPoint Pivot,
    CanvasSize Bounds,
    string Fingerprint)
{
    public IReadOnlyList<CanvasParameterState> Inputs { get; init; } =
        Array.Empty<CanvasParameterState>();

    public IReadOnlyList<CanvasParameterState> Outputs { get; init; } =
        Array.Empty<CanvasParameterState>();

    public string? ValueJson { get; init; }
}

public sealed record CanvasParameterState(
    Guid OwnerObjectId,
    Guid ParameterId,
    string Name,
    string NickName,
    CanvasParameterDirection Direction,
    string TypeName,
    string? TypeHint,
    CanvasParameterAccess Access,
    bool Optional,
    IReadOnlyList<CanvasParameterEndpoint> CurrentSources);

public sealed record InspectCanvasOutputsRequest(Guid ObjectId);

public sealed record CanvasOutputInspection(
    Guid GrasshopperDocumentId,
    Guid ObjectId,
    IReadOnlyList<CanvasOutputParameterInspection> Outputs,
    string Fingerprint);

public sealed record CanvasOutputParameterInspection(
    Guid ParameterId,
    string Name,
    string NickName,
    int DataCount,
    IReadOnlyList<string> TypeNames,
    CanvasBoundingBox3d? GeometryBounds,
    IReadOnlyList<string> SampleValues);

public sealed record CanvasBoundingBox3d(
    CanvasPoint3d Minimum,
    CanvasPoint3d Maximum,
    CanvasPoint3d Size);

public sealed record CanvasPoint3d(double X, double Y, double Z);

public sealed record CanvasParameterEndpoint(
    Guid OwnerObjectId,
    Guid ParameterId);

public enum CanvasParameterDirection
{
    Input,
    Output,
}

public enum CanvasParameterAccess
{
    Item,
    List,
    Tree,
}

public sealed record ComponentCatalogSearchRequest(
    string? Query,
    int Limit = 25,
    bool IncludeObsolete = false);

public sealed record ComponentCatalogSearchResult(
    Guid GrasshopperDocumentId,
    string Query,
    int Limit,
    IReadOnlyList<CanvasComponentCatalogItem> Matches);

public sealed record CanvasComponentCatalogItem(
    Guid ComponentTypeId,
    string Name,
    string NickName,
    string Category,
    string Subcategory,
    string Description,
    string Exposure,
    bool Obsolete);

public sealed record WireState(
    Guid SourceObjectId,
    Guid SourceParameterId,
    Guid TargetObjectId,
    Guid TargetParameterId);

public sealed record GroupState(
    Guid GroupId,
    string Name,
    IReadOnlyList<Guid> ObjectIds,
    int ArgbColor);

public readonly record struct CanvasPoint(float X, float Y);

public readonly record struct CanvasSize(float Width, float Height);

public sealed record CreateCanvasObjectRequest(
    string OperationId,
    Guid ObjectId,
    Guid ComponentTypeId,
    CanvasPoint Pivot,
    string? NickName);

public sealed record DeleteCanvasObjectRequest(
    string OperationId,
    Guid ObjectId,
    string ExpectedFingerprint);

public sealed record MoveCanvasObjectsRequest(
    string OperationId,
    IReadOnlyDictionary<Guid, CanvasPoint> Pivots,
    IReadOnlyDictionary<Guid, string> ExpectedFingerprints);

public sealed record SetNumberSliderValueRequest(
    string OperationId,
    Guid ObjectId,
    string ExpectedFingerprint,
    decimal Value,
    decimal Minimum,
    decimal Maximum,
    int DecimalPlaces);

public sealed record SetWireRequest(
    string OperationId,
    WireState Wire,
    WireAction Action,
    bool RejectCycles);

public enum WireAction
{
    Connect,
    Disconnect,
}

public sealed record SetGroupRequest(
    string OperationId,
    Guid GroupId,
    string Name,
    IReadOnlyList<Guid> ObjectIds,
    int ArgbColor);

public sealed record CanvasMutationResult(
    string OperationId,
    bool Changed,
    string BeforeFingerprint,
    string AfterFingerprint,
    IReadOnlyList<Guid> AffectedObjectIds);
