using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.BridgeContract;
using GPTino.Contracts;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// Consumer-first delete reordering: the canvas STRUCTURE fingerprint hashes a component's input
/// wires, so a batch that deletes an upstream component first moves a surviving target's
/// fingerprint and refuses the batch mid-apply (RecoveryRequired). The executor therefore
/// dispatches contiguous canvas.delete runs consumer-first — dispatch order ONLY, never the
/// accepted payloads or the submit-time request hash.
/// </summary>
[Collection(LiveDocumentBackendCollection.Name)]
public sealed class DeleteReorderTests
{
    [Fact]
    public async Task LinearChainDispatchesConsumerFirst()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Deletes"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        // Submitted upstream-first (source, stage, sink) — the exact order that used to die
        // mid-batch; the wire chain is source -> stage -> sink.
        var changeSet = await CreateDeleteBatchAsync(
            harness,
            session,
            snapshot.Revision,
            ("delete-source", harness.CanvasObjectId, harness.ObjectStructureFingerprint),
            ("delete-stage", harness.SecondCanvasObjectId, harness.SecondObjectStructureFingerprint),
            ("delete-sink", harness.ThirdCanvasObjectId, harness.ThirdObjectStructureFingerprint));

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "chain-key", "Delete the chain"),
            CancellationToken.None));
        await harness.WaitForJobStateAsync(submitted.GetProperty("jobId").GetGuid());

        // Dispatched sink (deepest consumer) first, then stage, then source.
        Assert.Equal(
            new[] { "delete-sink", "delete-stage", "delete-source" },
            responder.WriteOperationIds);
    }

    [Fact]
    public async Task NonDeleteOperationSplitsTheReorderSegments()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Split"));
        // The stage still feeds the SURVIVING sink, so the W3 live-wire delete guard would refuse
        // it as foreign; this test is about reorder segmentation, so make the stage self-authored
        // (Direct-origin ledger row at the CURRENT structure fingerprint — the full predicate).
        harness.Backend.SeedResourceLedgerForTests(
            session,
            new ResourceAddress(ResourceKind.GrasshopperComponent, harness.SecondCanvasObjectId.ToString("D")),
            harness.SecondObjectStructureFingerprint);
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var deleteSource = await CreateDeleteOperationAsync(
            harness, session, "delete-source", harness.CanvasObjectId, harness.ObjectStructureFingerprint);
        var deleteStage = await CreateDeleteOperationAsync(
            harness, session, "delete-stage", harness.SecondCanvasObjectId, harness.SecondObjectStructureFingerprint);
        var moveResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentLayout,
            harness.ThirdCanvasObjectId.ToString("D"));
        var moveArtifact = await harness.WritePayloadAsync(
            session,
            "move-sink.json",
            new
            {
                bridgeOperation = "canvas.move",
                arguments = new
                {
                    operationId = "move-sink",
                    pivots = new Dictionary<Guid, object>
                    {
                        [harness.ThirdCanvasObjectId] = new { x = 400, y = 20 }
                    },
                    expectedFingerprints = new Dictionary<Guid, string>
                    {
                        [harness.ThirdCanvasObjectId] = harness.ThirdObjectLayoutFingerprint
                    }
                }
            });
        var moveOperation = new TypedOperation(
            "move-sink",
            OperationKind.MoveComponent,
            AdapterOwner.Canvas,
            Array.Empty<ResourceAddress>(),
            [moveResource],
            Reversible: true,
            moveArtifact);
        // delete-stage CONSUMES delete-source's target, but the move between them is a segment
        // boundary: nothing may reorder across it, so the submitted order survives verbatim.
        var changeSet = CreateChangeSet(
            harness,
            session,
            snapshot.Revision,
            [deleteSource.Operation, moveOperation, deleteStage.Operation],
            [
                deleteSource.Expectation,
                new ResourceExpectation(moveResource, harness.ThirdObjectLayoutFingerprint),
                deleteStage.Expectation,
            ]);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "split-key", "Delete around a move"),
            CancellationToken.None));
        await harness.WaitForJobStateAsync(submitted.GetProperty("jobId").GetGuid());

        Assert.Equal(
            new[] { "delete-source", "move-sink", "delete-stage" },
            responder.WriteOperationIds);
    }

    [Fact]
    public async Task CyclicTopologyKeepsTheSubmittedOrder()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        harness.IncludeDeleteCycle = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Cycle"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await CreateDeleteBatchAsync(
            harness,
            session,
            snapshot.Revision,
            ("delete-source", harness.CanvasObjectId, harness.ObjectStructureFingerprint),
            ("delete-stage", harness.SecondCanvasObjectId, harness.SecondObjectStructureFingerprint));

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "cycle-key", "Delete a cycle"),
            CancellationToken.None));
        await harness.WaitForJobStateAsync(submitted.GetProperty("jobId").GetGuid());

        // source <-> stage form a cycle: no consumer-first order exists, keep the submitted order.
        Assert.Equal(
            new[] { "delete-source", "delete-stage" },
            responder.WriteOperationIds);
    }

    [Fact]
    public async Task ReorderNeverChangesTheAcceptedRequestHash()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Hash"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await CreateDeleteBatchAsync(
            harness,
            session,
            snapshot.Revision,
            ("delete-source", harness.CanvasObjectId, harness.ObjectStructureFingerprint),
            ("delete-stage", harness.SecondCanvasObjectId, harness.SecondObjectStructureFingerprint),
            ("delete-sink", harness.ThirdCanvasObjectId, harness.ThirdObjectStructureFingerprint));
        var submission = Submission(changeSet, snapshot.Id, "hash-key", "Delete the chain");

        var first = ToElement(await harness.Backend.SubmitChangeAsync(session, submission, CancellationToken.None));
        var jobId = first.GetProperty("jobId").GetGuid();
        await harness.WaitForJobStateAsync(jobId);
        // The reordered dispatch ran consumer-first...
        Assert.Equal("delete-sink", responder.WriteOperationIds[0]);

        // ...and the byte-identical replay still matches the ACCEPTED request hash (computed at
        // submit, before any reorder): a drifted hash would throw "different accepted request".
        var replay = ToElement(await harness.Backend.SubmitChangeAsync(session, submission, CancellationToken.None));
        Assert.True(replay.GetProperty("duplicate").GetBoolean());
        Assert.Equal(jobId, replay.GetProperty("jobId").GetGuid());
    }

    private static async Task<ChangeSet> CreateDeleteBatchAsync(
        LiveDocumentBackendHarness harness,
        SessionRecord session,
        long revision,
        params (string OperationId, Guid ObjectId, string Fingerprint)[] deletes)
    {
        var operations = new List<TypedOperation>(deletes.Length);
        var writeSet = new List<ResourceExpectation>(deletes.Length);
        foreach (var (operationId, objectId, fingerprint) in deletes)
        {
            var prepared = await CreateDeleteOperationAsync(harness, session, operationId, objectId, fingerprint);
            operations.Add(prepared.Operation);
            writeSet.Add(prepared.Expectation);
        }
        return CreateChangeSet(harness, session, revision, operations, writeSet);
    }

    private static async Task<(TypedOperation Operation, ResourceExpectation Expectation)> CreateDeleteOperationAsync(
        LiveDocumentBackendHarness harness,
        SessionRecord session,
        string operationId,
        Guid objectId,
        string fingerprint)
    {
        var resource = new ResourceAddress(ResourceKind.GrasshopperComponent, objectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            $"{operationId}.json",
            new
            {
                bridgeOperation = "canvas.delete",
                arguments = new { operationId, objectId, expectedFingerprint = fingerprint }
            });
        var operation = new TypedOperation(
            operationId,
            OperationKind.DeleteComponent,
            AdapterOwner.Canvas,
            Array.Empty<ResourceAddress>(),
            [resource],
            Reversible: false,
            artifact);
        return (operation, new ResourceExpectation(resource, fingerprint));
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
            Array.Empty<Guid>(),
            Array.Empty<ResourceExpectation>(),
            writeSet,
            operations,
            [new VerificationPredicate("No runtime errors", PredicateKind.RuntimeErrorAbsent, null, null)],
            Array.Empty<RollbackBeforeImage>(),
            DateTimeOffset.UtcNow);

    private static JsonElement Submission(
        ChangeSet changeSet,
        string snapshotId,
        string idempotencyKey,
        string summary) =>
        JsonSerializer.SerializeToElement(
            new { changeSet, expectedSnapshotId = snapshotId, idempotencyKey, summary },
            BridgeProtocol.JsonOptions);

    private static JsonElement ToElement(object value) =>
        JsonSerializer.SerializeToElement(value, value.GetType(), BridgeProtocol.JsonOptions);
}
