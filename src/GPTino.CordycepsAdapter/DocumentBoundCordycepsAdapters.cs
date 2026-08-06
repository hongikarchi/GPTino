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

    public Task<CanvasMutationResult> ReferenceRhinoObjectsAsync(DocumentTarget target, ReferenceRhinoObjectsRequest request, CancellationToken cancellationToken = default) =>
        // referenceRhinoObjects is the one canvas op that also touches the paired Rhino document: it
        // loads the referenced geometry to validate the reference. The core is handed the SAME Rhino
        // document serial that the Rhino scene adapter resolves for rhino_list — never RhinoDoc.ActiveDoc
        // — so the objects it validates are exactly the ones the model listed and referenced by GUID.
        ReferenceRhinoObjectsCoreAsync(Resolve(target), target.RhinoDocumentSerial, request, cancellationToken);

    public Task<ReferencedRhinoIdsResult> ListReferencedRhinoIdsAsync(DocumentTarget target, CancellationToken cancellationToken = default) =>
        // Like referenceRhinoObjects, this read spans both documents: reference GUIDs live in the GH
        // parameters, but existence/layer resolve against the paired Rhino document serial — never
        // RhinoDoc.ActiveDoc.
        ListReferencedRhinoIdsCoreAsync(Resolve(target), target.RhinoDocumentSerial, cancellationToken);

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
    protected abstract Task<CanvasMutationResult> ReferenceRhinoObjectsCoreAsync(TDocument document, uint rhinoDocumentSerial, ReferenceRhinoObjectsRequest request, CancellationToken cancellationToken);
    protected abstract Task<ReferencedRhinoIdsResult> ListReferencedRhinoIdsCoreAsync(TDocument document, uint rhinoDocumentSerial, CancellationToken cancellationToken);

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

    public Task<StampedObjectsResult> ListStampedObjectsAsync(DocumentTarget target, CancellationToken cancellationToken = default) =>
        ListStampedObjectsCoreAsync(Resolve(target), cancellationToken);

    public Task<RhinoAuditResult> AuditAsync(DocumentTarget target, RhinoAuditRequest request, CancellationToken cancellationToken = default) =>
        AuditCoreAsync(Resolve(target), request, cancellationToken);

    public Task<StructuralExtractResult> ExtractStructuralAxesAsync(DocumentTarget target, StructuralExtractRequest request, CancellationToken cancellationToken = default) =>
        ExtractStructuralAxesCoreAsync(Resolve(target), request, cancellationToken);

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

    public Task<RhinoSceneMutationResult> FixEndpointPairAsync(DocumentTarget target, FixEndpointPairRequest request, CancellationToken cancellationToken = default) =>
        FixEndpointPairCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoLayerTableResult> ListLayersAsync(DocumentTarget target, CancellationToken cancellationToken = default) =>
        ListLayersCoreAsync(Resolve(target), cancellationToken);

    public Task<FocusObjectsResult> FocusObjectsAsync(DocumentTarget target, FocusObjectsRequest request, CancellationToken cancellationToken = default) =>
        FocusObjectsCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoSceneMutationResult> UpdateLayerAsync(DocumentTarget target, UpdateRhinoLayerRequest request, CancellationToken cancellationToken = default) =>
        UpdateLayerCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoSceneMutationResult> DeleteLayerAsync(DocumentTarget target, DeleteRhinoLayerRequest request, CancellationToken cancellationToken = default) =>
        DeleteLayerCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoLayerStateResult> LayerStateAsync(DocumentTarget target, RhinoLayerStateRequest request, CancellationToken cancellationToken = default) =>
        LayerStateCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoPurgeResult> PurgeTableEntriesAsync(DocumentTarget target, PurgeTableEntriesRequest request, CancellationToken cancellationToken = default) =>
        PurgeTableEntriesCoreAsync(Resolve(target), request, cancellationToken);

    public Task<RhinoBatchMutationResult> MoveObjectsToLayerAsync(DocumentTarget target, MoveObjectsToLayerRequest request, CancellationToken cancellationToken = default) =>
        MoveObjectsToLayerCoreAsync(Resolve(target), request, cancellationToken);

    protected abstract Task<RhinoSceneListResult> ListObjectsCoreAsync(TRhinoDocument document, RhinoListObjectsRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneObjectState> InspectObjectCoreAsync(TRhinoDocument document, Guid objectId, CancellationToken cancellationToken);
    protected abstract Task<StampedObjectsResult> ListStampedObjectsCoreAsync(TRhinoDocument document, CancellationToken cancellationToken);
    protected abstract Task<RhinoAuditResult> AuditCoreAsync(TRhinoDocument document, RhinoAuditRequest request, CancellationToken cancellationToken);
    protected abstract Task<StructuralExtractResult> ExtractStructuralAxesCoreAsync(TRhinoDocument document, StructuralExtractRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneMutationResult> CreatePrimitiveCoreAsync(TRhinoDocument document, CreateRhinoPrimitiveRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneMutationResult> UpsertObjectCoreAsync(TRhinoDocument document, UpsertRhinoObjectRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoUpsertValidationResult> ValidateUpsertObjectCoreAsync(TRhinoDocument document, UpsertRhinoObjectRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneMutationResult> DeleteObjectCoreAsync(TRhinoDocument document, DeleteRhinoObjectRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneMutationResult> EnsureLayerCoreAsync(TRhinoDocument document, EnsureRhinoLayerRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneMutationResult> TransformObjectCoreAsync(TRhinoDocument document, TransformRhinoObjectRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneMutationResult> FixEndpointPairCoreAsync(TRhinoDocument document, FixEndpointPairRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoLayerTableResult> ListLayersCoreAsync(TRhinoDocument document, CancellationToken cancellationToken);

    protected abstract Task<FocusObjectsResult> FocusObjectsCoreAsync(TRhinoDocument document, FocusObjectsRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneMutationResult> UpdateLayerCoreAsync(TRhinoDocument document, UpdateRhinoLayerRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoSceneMutationResult> DeleteLayerCoreAsync(TRhinoDocument document, DeleteRhinoLayerRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoLayerStateResult> LayerStateCoreAsync(TRhinoDocument document, RhinoLayerStateRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoPurgeResult> PurgeTableEntriesCoreAsync(TRhinoDocument document, PurgeTableEntriesRequest request, CancellationToken cancellationToken);
    protected abstract Task<RhinoBatchMutationResult> MoveObjectsToLayerCoreAsync(TRhinoDocument document, MoveObjectsToLayerRequest request, CancellationToken cancellationToken);

    private TRhinoDocument Resolve(DocumentTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Validate();
        return _resolver.Resolve(target);
    }
}
