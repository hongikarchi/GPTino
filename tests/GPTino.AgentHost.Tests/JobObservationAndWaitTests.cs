using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.BridgeContract;
using GPTino.Contracts;

namespace GPTino.AgentHost.Tests;

[Collection(LiveDocumentBackendCollection.Name)]
public sealed class JobObservationAndWaitTests
{
    [Fact]
    public async Task WaitTrueReturnsTerminalStateWithDiagnosticsAndOutputsInline()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeNumberSliderValue = true;
        await using var responder = harness.StartResponder(responseFactory: request => request.Operation switch
        {
            "canvas.setNumberSlider" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: true,
                new { applied = true },
                beforeFingerprint: harness.ObjectFingerprint,
                afterFingerprint: "slider-after",
                diagnostics:
                [
                    new BridgeDiagnostic(
                        BridgeDiagnosticSeverity.Warning,
                        "solver_note",
                        "Value clamped to slider precision.")
                ]),
            "canvas.inspectOutputs" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: false,
                new
                {
                    objectId = request.Arguments.GetProperty("objectId").GetGuid(),
                    outputs = new[] { new { name = "N", dataCount = 1 } }
                },
                afterFingerprint: "outputs-v1"),
            _ => null
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Wait for result"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponentValue,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "wait-slider.json",
            new
            {
                bridgeOperation = "canvas.setNumberSlider",
                arguments = new
                {
                    operationId = "wait-slider",
                    objectId = harness.CanvasObjectId,
                    expectedFingerprint = harness.ObjectFingerprint,
                    value = 10m,
                    minimum = 0m,
                    maximum = 100m,
                    decimalPlaces = 0
                }
            });
        var changeSet = harness.CreateCustomChangeSet(
            session,
            snapshot.Revision,
            new TypedOperation(
                "wait-slider",
                OperationKind.SetValue,
                AdapterOwner.Cordyceps,
                [],
                [resource],
                Reversible: true,
                artifact),
            [new ResourceExpectation(resource, harness.ObjectFingerprint)]);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "wait-slider", wait: true),
            CancellationToken.None));

        // wait=true returned the terminal result in the same response: no job_status polling.
        Assert.Equal("committed", submitted.GetProperty("state").GetString());
        Assert.False(submitted.GetProperty("duplicate").GetBoolean());
        var diagnostic = Assert.Single(submitted.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal("wait-slider", diagnostic.GetProperty("operationId").GetString());
        Assert.Equal("warning", diagnostic.GetProperty("severity").GetString());
        Assert.Equal("solver_note", diagnostic.GetProperty("code").GetString());
        var committed = submitted.GetProperty("committed");
        var output = Assert.Single(committed.GetProperty("outputs").EnumerateArray());
        Assert.Equal(harness.CanvasObjectId, output.GetProperty("componentId").GetGuid());
        var inspectedOutput = Assert.Single(
            output.GetProperty("inspection").GetProperty("outputs").EnumerateArray());
        Assert.Equal("N", inspectedOutput.GetProperty("name").GetString());

        // A duplicate submission shares the original completion and projects the same terminal
        // state immediately instead of blocking.
        var duplicate = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "wait-slider", wait: true),
            CancellationToken.None));
        Assert.True(duplicate.GetProperty("duplicate").GetBoolean());
        Assert.Equal("committed", duplicate.GetProperty("state").GetString());
        Assert.Single(duplicate.GetProperty("diagnostics").EnumerateArray());
    }

    [Fact]
    public async Task DiagnosticsAreOmittedWhileTheJobIsStillActive()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeNumberSliderValue = true;
        await using var responder = harness.StartResponder(writeDelay: TimeSpan.FromMilliseconds(750));
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Slim polls"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponentValue,
            harness.CanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "slow-slider.json",
            new
            {
                bridgeOperation = "canvas.setNumberSlider",
                arguments = new
                {
                    operationId = "slow-slider",
                    objectId = harness.CanvasObjectId,
                    expectedFingerprint = harness.ObjectFingerprint,
                    value = 20m,
                    minimum = 0m,
                    maximum = 100m,
                    decimalPlaces = 0
                }
            });
        var changeSet = harness.CreateCustomChangeSet(
            session,
            snapshot.Revision,
            new TypedOperation(
                "slow-slider",
                OperationKind.SetValue,
                AdapterOwner.Cordyceps,
                [],
                [resource],
                Reversible: true,
                artifact),
            [new ResourceExpectation(resource, harness.ObjectFingerprint)]);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "slow-slider", wait: false),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();

        // Non-terminal projections stay slim: diagnostics only appear at a terminal state.
        Assert.Equal(JsonValueKind.Null, submitted.GetProperty("diagnostics").ValueKind);

        var state = await harness.WaitForJobStateAsync(jobId);
        var jobView = await harness.ReadJobViewAsync(jobId);
        Assert.True(state == "committed", jobView.GetProperty("message").GetString());
        Assert.NotEqual(JsonValueKind.Null, jobView.GetProperty("diagnostics").ValueKind);
    }

    private static JsonElement Submission(
        ChangeSet changeSet,
        string snapshotId,
        string idempotencyKey,
        bool wait) =>
        JsonSerializer.SerializeToElement(
            new
            {
                changeSet,
                expectedSnapshotId = snapshotId,
                idempotencyKey,
                summary = "Job observation and wait regression",
                wait
            },
            BridgeProtocol.JsonOptions);

    private static JsonElement ToElement(object value) =>
        JsonSerializer.SerializeToElement(value, value.GetType(), BridgeProtocol.JsonOptions);
}
