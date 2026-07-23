using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.BridgeContract;
using GPTino.Contracts;
using GPTino.CordycepsAdapter;

namespace GPTino.AgentHost.Tests;

[Collection(LiveDocumentBackendCollection.Name)]
public sealed class DomainFingerprintTests
{
    private const string WholeFingerprint = "whole-fp";
    private const string LayoutFingerprint = "layout-fp";
    private const string ValueFingerprint = "value-fp";

    [Fact]
    public async Task ValueWriteSucceedsAgainstTheValueDomainFingerprintDespiteOtherDomains()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder(responseFactory: request =>
            request.Operation == "canvas.snapshot"
                ? BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: false,
                    DomainSnapshot(harness))
                : null);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Domain value write"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponentValue,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "domain-slider.json",
            new
            {
                bridgeOperation = "canvas.setNumberSlider",
                arguments = new
                {
                    operationId = "domain-slider",
                    objectId = harness.CanvasObjectId,
                    expectedFingerprint = ValueFingerprint,
                    value = 42m,
                    minimum = 0m,
                    maximum = 100m,
                    decimalPlaces = 0
                }
            });
        var changeSet = harness.CreateCustomChangeSet(
            session,
            snapshot.Revision,
            new TypedOperation(
                "domain-slider",
                OperationKind.SetValue,
                AdapterOwner.Cordyceps,
                [],
                [resource],
                Reversible: true,
                artifact),
            [new ResourceExpectation(resource, ValueFingerprint)]);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "domain-value-write"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var jobView = await harness.ReadJobViewAsync(jobId);

        // The value expectation matches the VALUE domain fingerprint, so the differing whole and
        // layout hashes (a concurrently moved component) must not stale this write.
        Assert.True(state == "committed", jobView.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ValueWriteDeclaringTheWholeObjectHashIsBlockedWithTheValueFingerprint()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder(responseFactory: request =>
            request.Operation == "canvas.snapshot"
                ? BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: false,
                    DomainSnapshot(harness))
                : null);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Stale value write"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponentValue,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "stale-slider.json",
            new
            {
                bridgeOperation = "canvas.setNumberSlider",
                arguments = new
                {
                    operationId = "stale-slider",
                    objectId = harness.CanvasObjectId,
                    expectedFingerprint = WholeFingerprint,
                    value = 42m,
                    minimum = 0m,
                    maximum = 100m,
                    decimalPlaces = 0
                }
            });
        var changeSet = harness.CreateCustomChangeSet(
            session,
            snapshot.Revision,
            new TypedOperation(
                "stale-slider",
                OperationKind.SetValue,
                AdapterOwner.Cordyceps,
                [],
                [resource],
                Reversible: true,
                artifact),
            [new ResourceExpectation(resource, WholeFingerprint)]);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "stale-value-write"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var jobView = await harness.ReadJobViewAsync(jobId);

        Assert.Equal("blocked", state);
        // The Stale recipe must carry the correct per-domain value so the retry lands.
        Assert.Contains(
            ValueFingerprint,
            jobView.GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreatedComponentProjectsSiblingLayoutAndValueFingerprints()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        var created = 0;
        await using var responder = harness.StartResponder(responseFactory: request =>
        {
            // The canvas is empty until the create write lands; afterwards the slider exists with
            // distinct per-domain fingerprints the model could not have known at submit time.
            if (request.Operation == "canvas.create")
            {
                Interlocked.Exchange(ref created, 1);
                return null;
            }
            if (request.Operation != "canvas.snapshot")
            {
                return null;
            }
            var snapshot = Volatile.Read(ref created) == 1
                ? DomainSnapshot(harness)
                : new CanvasSnapshot(
                    harness.Target.GrasshopperDocumentId,
                    "empty-document-v1",
                    Array.Empty<CanvasObjectState>(),
                    Array.Empty<WireState>(),
                    Array.Empty<GroupState>());
            return BridgeOperationResponse.Create(request.OperationId, changed: false, snapshot);
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Create sibling projection"));
        var snapshotView = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponent,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "create-slider.json",
            new
            {
                bridgeOperation = "canvas.create",
                arguments = new
                {
                    operationId = "create-slider",
                    objectId = harness.CanvasObjectId,
                    componentTypeId = Guid.Parse("57da07bd-ecab-415d-9d86-af36d7073abc"),
                    pivot = new { x = 100, y = 100 },
                    nickName = "Grid Spacing"
                }
            });
        var changeSet = harness.CreateCustomChangeSet(
            session,
            snapshotView.Revision,
            new TypedOperation(
                "create-slider",
                OperationKind.CreateComponent,
                AdapterOwner.Cordyceps,
                [],
                [resource],
                Reversible: false,
                artifact),
            [new ResourceExpectation(resource, ResourceExpectation.AbsentFingerprint)]);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshotView.Id, "create-sibling-projection"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var jobView = await harness.ReadJobViewAsync(jobId);

        Assert.True(state == "committed", jobView.GetProperty("message").GetString());
        var resources = jobView.GetProperty("committed").GetProperty("resources").EnumerateArray().ToArray();
        Assert.Contains(resources, item =>
            item.GetProperty("kind").GetString() == "grasshopperComponentValue" &&
            item.GetProperty("fingerprint").GetString() == ValueFingerprint);
        Assert.Contains(resources, item =>
            item.GetProperty("kind").GetString() == "grasshopperComponentLayout" &&
            item.GetProperty("fingerprint").GetString() == LayoutFingerprint);
    }

    private static CanvasSnapshot DomainSnapshot(LiveDocumentBackendHarness harness)
    {
        var slider = new CanvasObjectState(
            harness.CanvasObjectId,
            Guid.Parse("57da07bd-ecab-415d-9d86-af36d7073abc"),
            "Grid Spacing",
            new CanvasPoint(10, 20),
            new CanvasSize(90, 40),
            WholeFingerprint)
        {
            ValueJson = "{\"kind\":\"numberSlider\",\"value\":5,\"minimum\":0,\"maximum\":100,\"decimalPlaces\":0}",
            StructureFingerprint = "structure-fp",
            LayoutFingerprint = LayoutFingerprint,
            ValueFingerprint = ValueFingerprint,
        };
        return new CanvasSnapshot(
            harness.Target.GrasshopperDocumentId,
            "domain-document-v1",
            [slider],
            Array.Empty<WireState>(),
            Array.Empty<GroupState>());
    }

    private static JsonElement Submission(
        ChangeSet changeSet,
        string snapshotId,
        string idempotencyKey) =>
        JsonSerializer.SerializeToElement(
            new
            {
                changeSet,
                expectedSnapshotId = snapshotId,
                idempotencyKey,
                summary = "Domain fingerprint regression"
            },
            BridgeProtocol.JsonOptions);

    private static JsonElement ToElement(object value) =>
        JsonSerializer.SerializeToElement(value, value.GetType(), BridgeProtocol.JsonOptions);
}
