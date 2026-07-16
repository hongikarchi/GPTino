using GPTino.CordycepsAdapter;
using System.Text.Json;

namespace GPTino.BridgeContract.Tests;

public sealed class CordycepsRhinoBridgeOperationHandlerTests
{
    [Fact]
    public void ListPayload_OmittedFiltersUseBoundedDefaults()
    {
        var arguments = JsonSerializer.Deserialize<RhinoListObjectsRequest>(
            "{}",
            BridgeProtocol.JsonOptions);

        Assert.NotNull(arguments);
        Assert.Equal(100, arguments.Limit);
        Assert.Null(arguments.ObjectId);
        Assert.Null(arguments.Selected);
    }

    [Fact]
    public async Task List_IsReadOnlyBoundedAndReturnsTruncationDiagnostic()
    {
        var adapter = new FakeRhinoSceneAdapter
        {
            ListResult = new RhinoSceneListResult(
                1,
                1,
                Truncated: true,
                Bounds: null,
                new[]
                {
                    new RhinoSceneObjectSummary(
                        Guid.Parse("2f927896-83f3-43c2-8a84-29b779547b7a"),
                        "wall-1",
                        "Wall",
                        "Brep",
                        Guid.Parse("80ace29e-9912-41de-88af-de9a7b6a57f0"),
                        "Building::Walls",
                        Selected: false,
                        Bounds: null,
                        "object-fingerprint"),
                },
                "list-fingerprint"),
        };
        var handler = new CordycepsRhinoBridgeOperationHandler(adapter);
        var arguments = new RhinoListObjectsRequest(
            Limit: 1,
            LayerFullPath: "Building::Walls",
            GeometryType: "Brep");
        var request = BridgeOperationRequest.Create(
            "list-1",
            BridgeAdapterOwner.CordycepsRhino,
            "rhino.list",
            BridgeOperationAccess.Read,
            2,
            arguments);

        var response = await handler.HandleAsync(DocumentTargetTests.CreateTarget(), request);

        Assert.False(response.Changed);
        Assert.Equal("list-fingerprint", response.AfterFingerprint);
        Assert.Equal("rhino_list_truncated", Assert.Single(response.Diagnostics).Code);
        Assert.Equal(arguments, adapter.LastListRequest);
    }

    [Fact]
    public async Task CreatePrimitive_UsesExplicitTypedPayload()
    {
        var objectId = Guid.Parse("660eb647-3699-4f8c-a9dc-bfeb010f5d0f");
        var adapter = new FakeRhinoSceneAdapter
        {
            MutationResult = Mutation("create-1", objectId, before: null, after: "created"),
        };
        var handler = new CordycepsRhinoBridgeOperationHandler(adapter);
        var arguments = new CreateRhinoPrimitiveRequest(
            "create-1",
            objectId,
            "control-point-1",
            RhinoPrimitiveKind.Point,
            Point: new RhinoPointPrimitive(new RhinoPoint3d(1, 2, 3)),
            Attributes: new RhinoPrimitiveAttributes(Name: "Control Point"));
        var request = BridgeOperationRequest.Create(
            "create-1",
            BridgeAdapterOwner.CordycepsRhino,
            "rhino.createPrimitive",
            BridgeOperationAccess.Write,
            2,
            arguments,
            writerLeaseToken: "broker-lease");

        var response = await handler.HandleAsync(DocumentTargetTests.CreateTarget(), request);

        Assert.True(response.Changed);
        Assert.Equal("created", response.AfterFingerprint);
        Assert.Equal(arguments, adapter.LastCreateRequest);
    }

    [Fact]
    public async Task Mutation_RejectsPayloadOperationIdMismatch()
    {
        var objectId = Guid.Parse("660eb647-3699-4f8c-a9dc-bfeb010f5d0f");
        var adapter = new FakeRhinoSceneAdapter();
        var handler = new CordycepsRhinoBridgeOperationHandler(adapter);
        var arguments = new CreateRhinoPrimitiveRequest(
            "payload-id",
            objectId,
            "control-point-1",
            RhinoPrimitiveKind.Point,
            Point: new RhinoPointPrimitive(new RhinoPoint3d(1, 2, 3)));
        var request = BridgeOperationRequest.Create(
            "envelope-id",
            BridgeAdapterOwner.CordycepsRhino,
            "rhino.createPrimitive",
            BridgeOperationAccess.Write,
            2,
            arguments,
            writerLeaseToken: "broker-lease");

        var exception = await Assert.ThrowsAsync<BridgeProtocolException>(
            () => handler.HandleAsync(DocumentTargetTests.CreateTarget(), request));

        Assert.Equal("operation_id", exception.Code);
        Assert.Null(adapter.LastCreateRequest);
    }

    [Fact]
    public async Task Transform_RequiresMatchingEnvelopeFingerprint()
    {
        var objectId = Guid.Parse("40dd3f09-678d-45cd-84c5-27de846b940d");
        var adapter = new FakeRhinoSceneAdapter
        {
            MutationResult = Mutation("transform-1", objectId, "before", "after"),
        };
        var handler = new CordycepsRhinoBridgeOperationHandler(adapter);
        var arguments = new TransformRhinoObjectRequest(
            "transform-1",
            objectId,
            "before",
            Translation(10, 20, 30));
        var request = BridgeOperationRequest.Create(
            "transform-1",
            BridgeAdapterOwner.CordycepsRhino,
            "rhino.transform",
            BridgeOperationAccess.Write,
            2,
            arguments,
            expectedFingerprint: "stale",
            writerLeaseToken: "broker-lease");

        var exception = await Assert.ThrowsAsync<BridgeProtocolException>(
            () => handler.HandleAsync(DocumentTargetTests.CreateTarget(), request));

        Assert.Equal("expected_fingerprint", exception.Code);
        Assert.Null(adapter.LastTransformRequest);
    }

    [Fact]
    public async Task Transform_RoutesExactObjectMatrixAndFingerprint()
    {
        var objectId = Guid.Parse("40dd3f09-678d-45cd-84c5-27de846b940d");
        var adapter = new FakeRhinoSceneAdapter
        {
            MutationResult = Mutation("transform-1", objectId, "before", "after"),
        };
        var handler = new CordycepsRhinoBridgeOperationHandler(adapter);
        var arguments = new TransformRhinoObjectRequest(
            "transform-1",
            objectId,
            "before",
            Translation(10, 20, 30));
        var request = BridgeOperationRequest.Create(
            "transform-1",
            BridgeAdapterOwner.CordycepsRhino,
            "rhino.transform",
            BridgeOperationAccess.Write,
            2,
            arguments,
            expectedFingerprint: "before",
            writerLeaseToken: "broker-lease");

        var response = await handler.HandleAsync(DocumentTargetTests.CreateTarget(), request);

        Assert.True(response.Changed);
        Assert.Equal("before", response.BeforeFingerprint);
        Assert.Equal("after", response.AfterFingerprint);
        Assert.Equal(arguments, adapter.LastTransformRequest);
    }

    [Fact]
    public async Task ValidateUpsert_IsReadOnlyAndRoutesTheExactPayload()
    {
        var objectId = Guid.Parse("1ca7b351-bc98-46c6-bb8c-eec5dff139d8");
        var arguments = new UpsertRhinoObjectRequest(
            "validate-1",
            objectId,
            "surface-1",
            "Brep",
            "{\"archive3dm\":1}",
            "{}",
            ExpectedFingerprint: null);
        var adapter = new FakeRhinoSceneAdapter
        {
            ValidationResult = new RhinoUpsertValidationResult(
                "validate-1",
                objectId,
                "Brep",
                ExistingObject: false,
                ExistingFingerprint: null,
                IsValid: true),
        };
        var handler = new CordycepsRhinoBridgeOperationHandler(adapter);
        var request = BridgeOperationRequest.Create(
            "validate-1",
            BridgeAdapterOwner.CordycepsRhino,
            "rhino.validateUpsert",
            BridgeOperationAccess.Read,
            2,
            arguments);

        var response = await handler.HandleAsync(DocumentTargetTests.CreateTarget(), request);

        Assert.False(response.Changed);
        Assert.Equal(arguments, adapter.LastValidationRequest);
        var result = response.Result.Deserialize<RhinoUpsertValidationResult>(BridgeProtocol.JsonOptions);
        Assert.NotNull(result);
        Assert.True(result.IsValid);
        Assert.Equal("Brep", result.ActualGeometryType);
    }

    [Fact]
    public async Task ValidateUpsert_RejectsWriteAccess()
    {
        var objectId = Guid.NewGuid();
        var arguments = new UpsertRhinoObjectRequest(
            "validate-write",
            objectId,
            "surface-1",
            "Brep",
            "{}",
            "{}",
            ExpectedFingerprint: null);
        var adapter = new FakeRhinoSceneAdapter();
        var handler = new CordycepsRhinoBridgeOperationHandler(adapter);
        var request = BridgeOperationRequest.Create(
            "validate-write",
            BridgeAdapterOwner.CordycepsRhino,
            "rhino.validateUpsert",
            BridgeOperationAccess.Write,
            2,
            arguments,
            writerLeaseToken: "broker-lease");

        await Assert.ThrowsAsync<BridgeProtocolException>(
            () => handler.HandleAsync(DocumentTargetTests.CreateTarget(), request));
        Assert.Null(adapter.LastValidationRequest);
    }

    private static RhinoTransformMatrix Translation(double x, double y, double z) => new(
        1, 0, 0, x,
        0, 1, 0, y,
        0, 0, 1, z,
        0, 0, 0, 1);

    private static RhinoSceneMutationResult Mutation(
        string operationId,
        Guid objectId,
        string? before,
        string after) =>
        new(operationId, Changed: true, before, after, objectId);

    private sealed class FakeRhinoSceneAdapter : ICordycepsRhinoSceneAdapter
    {
        public RhinoSceneListResult ListResult { get; set; } = new(
            100,
            0,
            Truncated: false,
            Bounds: null,
            Array.Empty<RhinoSceneObjectSummary>(),
            "empty");

        public RhinoSceneMutationResult MutationResult { get; set; } =
            Mutation("unused", Guid.NewGuid(), "before", "after");

        public RhinoUpsertValidationResult ValidationResult { get; set; } =
            new(
                "unused",
                Guid.NewGuid(),
                "Point",
                ExistingObject: false,
                ExistingFingerprint: null,
                IsValid: true);

        public RhinoListObjectsRequest? LastListRequest { get; private set; }
        public CreateRhinoPrimitiveRequest? LastCreateRequest { get; private set; }
        public TransformRhinoObjectRequest? LastTransformRequest { get; private set; }
        public UpsertRhinoObjectRequest? LastValidationRequest { get; private set; }

        public Task<RhinoSceneListResult> ListObjectsAsync(
            DocumentTarget target,
            RhinoListObjectsRequest request,
            CancellationToken cancellationToken = default)
        {
            LastListRequest = request;
            return Task.FromResult(ListResult);
        }

        public Task<RhinoSceneObjectState> InspectObjectAsync(
            DocumentTarget target,
            Guid objectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(State(objectId));

        public Task<RhinoSceneMutationResult> CreatePrimitiveAsync(
            DocumentTarget target,
            CreateRhinoPrimitiveRequest request,
            CancellationToken cancellationToken = default)
        {
            LastCreateRequest = request;
            return Task.FromResult(MutationResult);
        }

        public Task<RhinoSceneMutationResult> UpsertObjectAsync(
            DocumentTarget target,
            UpsertRhinoObjectRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(MutationResult);

        public Task<RhinoUpsertValidationResult> ValidateUpsertObjectAsync(
            DocumentTarget target,
            UpsertRhinoObjectRequest request,
            CancellationToken cancellationToken = default)
        {
            LastValidationRequest = request;
            return Task.FromResult(ValidationResult);
        }

        public Task<RhinoSceneMutationResult> DeleteObjectAsync(
            DocumentTarget target,
            DeleteRhinoObjectRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(MutationResult);

        public Task<RhinoSceneMutationResult> EnsureLayerAsync(
            DocumentTarget target,
            EnsureRhinoLayerRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(MutationResult);

        public Task<RhinoSceneMutationResult> TransformObjectAsync(
            DocumentTarget target,
            TransformRhinoObjectRequest request,
            CancellationToken cancellationToken = default)
        {
            LastTransformRequest = request;
            return Task.FromResult(MutationResult);
        }

        private static RhinoSceneObjectState State(Guid objectId) =>
            new(objectId, "logical", "Point", "{}", "{}", "fingerprint");
    }
}
