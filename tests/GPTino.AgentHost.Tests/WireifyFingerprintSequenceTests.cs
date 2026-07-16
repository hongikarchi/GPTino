using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.BridgeContract;
using GPTino.Contracts;
using GPTino.WireifyAdapter;

namespace GPTino.AgentHost.Tests;

[Collection(LiveDocumentBackendCollection.Name)]
public sealed class WireifyFingerprintSequenceTests
{
    private const string InitialFingerprint = "python-f0";

    [Fact]
    public async Task MixedPythonAndCanvasWritesAreRejectedBeforeQueueing()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters:
            [
                BridgeAdapterOwner.CordycepsCanvas,
                BridgeAdapterOwner.Wireify
            ]);
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Mixed writes"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var (pythonOperation, pythonExpectation) = await CreatePythonSourceOperationAsync(
            harness,
            session,
            harness.CanvasObjectId,
            "mixed-python",
            "mixed-python.json");
        var layoutResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentLayout,
            harness.CanvasObjectId.ToString("D"));
        var canvasArtifact = await harness.WritePayloadAsync(
            session,
            "mixed-canvas.json",
            new
            {
                bridgeOperation = "canvas.move",
                arguments = new
                {
                    operationId = "mixed-canvas",
                    pivots = new Dictionary<Guid, object>
                    {
                        [harness.CanvasObjectId] = new { x = 30, y = 40 }
                    },
                    expectedFingerprints = new Dictionary<Guid, string>
                    {
                        [harness.CanvasObjectId] = harness.ObjectFingerprint
                    }
                }
            });
        var canvasOperation = new TypedOperation(
            "mixed-canvas",
            OperationKind.MoveComponent,
            AdapterOwner.Cordyceps,
            [],
            [layoutResource],
            Reversible: true,
            canvasArtifact);
        var changeSet = CreateChangeSet(
            harness,
            session,
            snapshot.Revision,
            [pythonOperation, canvasOperation],
            [pythonExpectation, new ResourceExpectation(layoutResource, harness.ObjectFingerprint)]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "mixed-writes"),
                CancellationToken.None));

        Assert.Contains("cannot contain other writes", exception.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Backend.ReadQueue());
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task TwoPythonComponentWriteDomainsAreRejectedBeforeQueueing()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters:
            [
                BridgeAdapterOwner.CordycepsCanvas,
                BridgeAdapterOwner.Wireify
            ]);
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Two Python components"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var first = await CreatePythonSourceOperationAsync(
            harness,
            session,
            harness.CanvasObjectId,
            "first-python",
            "first-python.json");
        var second = await CreatePythonSourceOperationAsync(
            harness,
            session,
            Guid.NewGuid(),
            "second-python",
            "second-python.json");
        var changeSet = CreateChangeSet(
            harness,
            session,
            snapshot.Revision,
            [first.Operation, second.Operation],
            [first.Expectation, second.Expectation]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "two-python-components"),
                CancellationToken.None));

        Assert.Contains("exactly one Python component", exception.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Backend.ReadQueue());
        Assert.Empty(responder.WriteOperationIds);
    }

    [Theory]
    [InlineData("unexpected-before", "python-f1")]
    [InlineData(InitialFingerprint, "")]
    public async Task InvalidWireifyFingerprintResponseRequiresRecovery(
        string responseBefore,
        string responseAfter)
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters:
            [
                BridgeAdapterOwner.CordycepsCanvas,
                BridgeAdapterOwner.Wireify
            ]);
        await using var responder = harness.StartResponder(responseFactory: request => request.Operation switch
        {
            "python.inspect" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: false,
                new { componentId = request.Arguments.GetProperty("componentId").GetGuid() },
                afterFingerprint: InitialFingerprint),
            "python.setSource" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: true,
                new { applied = true },
                beforeFingerprint: responseBefore,
                afterFingerprint: responseAfter),
            _ => null
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Invalid chain response"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var python = await CreatePythonSourceOperationAsync(
            harness,
            session,
            harness.CanvasObjectId,
            "invalid-chain-response",
            "invalid-chain-response.json");
        var changeSet = CreateChangeSet(
            harness,
            session,
            snapshot.Revision,
            [python.Operation],
            [python.Expectation]);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, $"invalid-chain-{Guid.NewGuid():N}"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();

        Assert.Equal("recoveryrequired", await harness.WaitForJobStateAsync(jobId));
        var job = await harness.ReadJobViewAsync(jobId);
        Assert.Contains(
            "invalid fingerprint chain",
            job.GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["invalid-chain-response"], responder.WriteOperationIds);
    }

    private static async Task<(TypedOperation Operation, ResourceExpectation Expectation)>
        CreatePythonSourceOperationAsync(
            LiveDocumentBackendHarness harness,
            SessionRecord session,
            Guid componentId,
            string operationId,
            string artifactName)
    {
        var artifact = await harness.WritePayloadAsync(
            session,
            artifactName,
            new
            {
                bridgeOperation = "python.setSource",
                arguments = new
                {
                    operationId,
                    componentId,
                    expectedSourceSha256 = "source-v0",
                    source = "print('GPTino')",
                    runtime = PythonRuntime.Cpython3,
                    expireSolution = false
                }
            });
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponentSource,
            componentId.ToString("D"));
        return (
            new TypedOperation(
                operationId,
                OperationKind.UpdatePythonSource,
                AdapterOwner.Wireify,
                [],
                [resource],
                Reversible: true,
                artifact),
            new ResourceExpectation(resource, InitialFingerprint));
    }

    private static ChangeSet CreateChangeSet(
        LiveDocumentBackendHarness harness,
        SessionRecord session,
        long revision,
        IReadOnlyList<TypedOperation> operations,
        IReadOnlyList<ResourceExpectation> writeSet) =>
        new(
            Guid.NewGuid(),
            harness.Target.ProjectId,
            session.Id,
            revision,
            null,
            [],
            [],
            writeSet,
            operations,
            [new VerificationPredicate("No runtime errors", PredicateKind.RuntimeErrorAbsent, null, null)],
            [],
            DateTimeOffset.UtcNow);

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
                summary = "Wireify fingerprint sequence regression"
            },
            BridgeProtocol.JsonOptions);

    private static JsonElement ToElement(object value) =>
        JsonSerializer.SerializeToElement(value, value.GetType(), BridgeProtocol.JsonOptions);
}
