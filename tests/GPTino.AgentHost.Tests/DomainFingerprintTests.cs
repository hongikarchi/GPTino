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
