using GPTino.BridgeContract;

namespace GPTino.CordycepsAdapter;

public interface ICordycepsDocumentResolver<out TDocument>
    where TDocument : class
{
    TDocument Resolve(DocumentTarget target);
}

/// <summary>
/// Forces every canvas call through an explicit target resolver. No active-canvas fallback exists.
/// </summary>
public abstract class DocumentBoundCanvasAdapter<TDocument> : ICordycepsCanvasAdapter
    where TDocument : class
{
    private readonly ICordycepsDocumentResolver<TDocument> _resolver;

    protected DocumentBoundCanvasAdapter(ICordycepsDocumentResolver<TDocument> resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public Task<CanvasSnapshot> CaptureSnapshotAsync(DocumentTarget target, CancellationToken cancellationToken = default) =>
        CaptureSnapshotCoreAsync(Resolve(target), cancellationToken);

    public Task<CanvasObjectState> InspectObjectAsync(DocumentTarget target, Guid objectId, CancellationToken cancellationToken = default) =>
        InspectObjectCoreAsync(Resolve(target), objectId, cancellationToken);

    public Task<CanvasOutputInspection> InspectOutputsAsync(DocumentTarget target, InspectCanvasOutputsRequest request, CancellationToken cancellationToken = default) =>
        InspectOutputsCoreAsync(Resolve(target), request, cancellationToken);

    public Task<ComponentCatalogSearchResult> SearchComponentCatalogAsync(DocumentTarget target, ComponentCatalogSearchRequest request, CancellationToken cancellationToken = default) =>
        SearchComponentCatalogCoreAsync(Resolve(target), request, cancellationToken);

    public Task<CanvasMutationResult> CreateObjectAsync(DocumentTarget target, CreateCanvasObjectRequest request, CancellationToken cancellationToken = default) =>
        CreateObjectCoreAsync(Resolve(target), request, cancellationToken);

    public Task<CanvasMutationResult> DeleteObjectAsync(DocumentTarget target, DeleteCanvasObjectRequest request, CancellationToken cancellationToken = default) =>
        DeleteObjectCoreAsync(Resolve(target), request, cancellationToken);

    public Task<CanvasMutationResult> MoveObjectsAsync(DocumentTarget target, MoveCanvasObjectsRequest request, CancellationToken cancellationToken = default) =>
        MoveObjectsCoreAsync(Resolve(target), request, cancellationToken);

    public Task<CanvasMutationResult> SetNumberSliderValueAsync(DocumentTarget target, SetNumberSliderValueRequest request, CancellationToken cancellationToken = default) =>
        SetNumberSliderValueCoreAsync(Resolve(target), request, cancellationToken);

    public Task<CanvasMutationResult> SetWireAsync(DocumentTarget target, SetWireRequest request, CancellationToken cancellationToken = default) =>
        SetWireCoreAsync(Resolve(target), request, cancellationToken);

    public Task<CanvasMutationResult> SetGroupAsync(DocumentTarget target, SetGroupRequest request, CancellationToken cancellationToken = default) =>
        SetGroupCoreAsync(Resolve(target), request, cancellationToken);

    protected abstract Task<CanvasSnapshot> CaptureSnapshotCoreAsync(TDocument document, CancellationToken cancellationToken);
    protected abstract Task<CanvasObjectState> InspectObjectCoreAsync(TDocument document, Guid objectId, CancellationToken cancellationToken);
    protected abstract Task<CanvasOutputInspection> InspectOutputsCoreAsync(TDocument document, InspectCanvasOutputsRequest request, CancellationToken cancellationToken);
    protected abstract Task<ComponentCatalogSearchResult> SearchComponentCatalogCoreAsync(TDocument document, ComponentCatalogSearchRequest request, CancellationToken cancellationToken);
    protected abstract Task<CanvasMutationResult> CreateObjectCoreAsync(TDocument document, CreateCanvasObjectRequest request, CancellationToken cancellationToken);
    protected abstract Task<CanvasMutationResult> DeleteObjectCoreAsync(TDocument document, DeleteCanvasObjectRequest request, CancellationToken cancellationToken);
    protected abstract Task<CanvasMutationResult> MoveObjectsCoreAsync(TDocument document, MoveCanvasObjectsRequest request, CancellationToken cancellationToken);
    protected abstract Task<CanvasMutationResult> SetNumberSliderValueCoreAsync(TDocument document, SetNumberSliderValueRequest request, CancellationToken cancellationToken);
    protected abstract Task<CanvasMutationResult> SetWireCoreAsync(TDocument document, SetWireRequest request, CancellationToken cancellationToken);
    protected abstract Task<CanvasMutationResult> SetGroupCoreAsync(TDocument document, SetGroupRequest request, CancellationToken cancellationToken);

    private TDocument Resolve(DocumentTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Validate();
        return _resolver.Resolve(target);
    }
}

/// <summary>
/// Forces every Rhino scene call through an explicit target resolver. No active-doc fallback exists.
/// </summary>
public abstract class DocumentBoundRhinoSceneAdapter<TRhinoDocument> : ICordycepsRhinoSceneAdapter
    where TRhinoDocument : class
{
    private readonly ICordycepsDocumentResolver<TRhinoDocument> _resolver;

    protected DocumentBoundRhinoSceneAdapter(ICordycepsDocumentResolver<TRhinoDocument> resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    public Task<RhinoSceneListResult> ListObjectsAsync(DocumentTarget target, RhinoListObjectsRequest request, CancellationToken cancellationToken = default) =>
        ListObjectsCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoSceneObjectState> InspectObjectAsync(DocumentTarget target, Guid objectId, CancellationToken cancellationToken = default) =>
        InspectObjectCoreAsync(Resolve(target), objectId, cancellationToken);

    public Task<RhinoSceneMutationResult> CreatePrimitiveAsync(DocumentTarget target, CreateRhinoPrimitiveRequest request, CancellationToken cancellationToken = default) =>
        CreatePrimitiveCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoSceneMutationResult> UpsertObjectAsync(DocumentTarget target, UpsertRhinoObjectRequest request, CancellationToken cancellationToken = default) =>
        UpsertObjectCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoUpsertValidationResult> ValidateUpsertObjectAsync(DocumentTarget target, UpsertRhinoObjectRequest request, CancellationToken cancellationToken = default) =>
        ValidateUpsertObjectCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoSceneMutationResult> DeleteObjectAsync(DocumentTarget target, DeleteRhinoObjectRequest request, CancellationToken cancellationToken = default) =>
        DeleteObjectCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoSceneMutationResult> EnsureLayerAsync(DocumentTarget target, EnsureRhinoLayerRequest request, CancellationToken cancellationToken = default) =>
        EnsureLayerCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoSceneMutationResult> TransformObjectAsync(DocumentTarget target, TransformRhinoObjectRequest request, CancellationToken cancellationToken = default) =>
        TransformObjectCoreAsync(Resolve(target), request, cancellationToken);

    protected abstract Task<RhinoSceneListResult> ListObjectsCoreAsync(TRhinoDocument document, RhinoListObjectsRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneObjectState> InspectObjectCoreAsync(TRhinoDocument document, Guid objectId, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneMutationResult> CreatePrimitiveCoreAsync(TRhinoDocument document, CreateRhinoPrimitiveRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneMutationResult> UpsertObjectCoreAsync(TRhinoDocument document, UpsertRhinoObjectRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoUpsertValidationResult> ValidateUpsertObjectCoreAsync(TRhinoDocument document, UpsertRhinoObjectRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneMutationResult> DeleteObjectCoreAsync(TRhinoDocument document, DeleteRhinoObjectRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneMutationResult> EnsureLayerCoreAsync(TRhinoDocument document, EnsureRhinoLayerRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneMutationResult> TransformObjectCoreAsync(TRhinoDocument document, TransformRhinoObjectRequest request, CancellationToken cancellationToken);

    private TRhinoDocument Resolve(DocumentTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Validate();
        return _resolver.Resolve(target);
    }
}
