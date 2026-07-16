using System.Text.Json;
using GPTino.CordycepsAdapter;

namespace GPTino.BridgeContract.Tests;

public sealed class CordycepsCanvasBridgeOperationHandlerTests
{
    [Fact]
    public void CatalogPayload_OmittedOptionsUseBoundedDefaults()
    {
        var arguments = JsonSerializer.Deserialize<ComponentCatalogSearchRequest>(
            "{}",
            BridgeProtocol.JsonOptions);

        Assert.NotNull(arguments);
        Assert.Null(arguments.Query);
        Assert.Equal(25, arguments.Limit);
        Assert.False(arguments.IncludeObsolete);
    }

    [Fact]
    public async Task Catalog_IsReadOnlyAndRoutesExactQuery()
    {
        var typeId = Guid.Parse("67a88d84-3fc2-47df-9704-307bb46d5f91");
        var adapter = new FakeCanvasAdapter
        {
            CatalogResult = new ComponentCatalogSearchResult(
                DocumentTargetTests.CreateTarget().GrasshopperDocumentId,
                "point",
                10,
                new[]
                {
                    new CanvasComponentCatalogItem(
                        typeId,
                        "Construct Point",
                        "Pt",
                        "Vector",
                        "Point",
                        "Construct a point from coordinates.",
                        "primary",
                        Obsolete: false)
                })
        };
        var handler = new CordycepsCanvasBridgeOperationHandler(adapter);
        var arguments = new ComponentCatalogSearchRequest("point", 10);
        var request = BridgeOperationRequest.Create(
            "catalog-1",
            BridgeAdapterOwner.CordycepsCanvas,
            "canvas.catalog",
            BridgeOperationAccess.Read,
            2,
            arguments);

        var response = await handler.HandleAsync(DocumentTargetTests.CreateTarget(), request);
        var result = response.Result.Deserialize<ComponentCatalogSearchResult>(BridgeProtocol.JsonOptions);

        Assert.False(response.Changed);
        Assert.Equal(arguments, adapter.LastCatalogRequest);
        Assert.Equal(typeId, Assert.Single(Assert.IsType<ComponentCatalogSearchResult>(result).Matches).ComponentTypeId);
    }

    private sealed class FakeCanvasAdapter : ICordycepsCanvasAdapter
    {
        public required ComponentCatalogSearchResult CatalogResult { get; init; }

        public ComponentCatalogSearchRequest? LastCatalogRequest { get; private set; }

        public Task<ComponentCatalogSearchResult> SearchComponentCatalogAsync(
            DocumentTarget target,
            ComponentCatalogSearchRequest request,
            CancellationToken cancellationToken = default)
        {
            LastCatalogRequest = request;
            return Task.FromResult(CatalogResult);
        }

        public Task<CanvasSnapshot> CaptureSnapshotAsync(DocumentTarget target, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CanvasObjectState> InspectObjectAsync(DocumentTarget target, Guid objectId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CanvasMutationResult> CreateObjectAsync(DocumentTarget target, CreateCanvasObjectRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CanvasMutationResult> DeleteObjectAsync(DocumentTarget target, DeleteCanvasObjectRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CanvasMutationResult> MoveObjectsAsync(DocumentTarget target, MoveCanvasObjectsRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CanvasMutationResult> SetWireAsync(DocumentTarget target, SetWireRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<CanvasMutationResult> SetGroupAsync(DocumentTarget target, SetGroupRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
