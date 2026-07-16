using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.BridgeContract;
using GPTino.Contracts;
using GPTino.CordycepsAdapter;

namespace GPTino.AgentHost.Tests;

[Collection(LiveDocumentBackendCollection.Name)]
public sealed class RhinoLogicalEntityBatchTests
{
    [Fact]
    public async Task PrimitiveAndGenericCreateCannotClaimOneLogicalEntityForDifferentObjects()
    {
        await AssertConflictingClaimsRejectedAsync(
            OperationKind.CreateRhinoObject,
            upsertExpectedFingerprint: null,
            upsertWriteExpectation: ResourceExpectation.AbsentFingerprint);
    }

    [Fact]
    public async Task PrimitiveAndExistingUpsertCannotClaimOneLogicalEntityForDifferentObjects()
    {
        const string existingFingerprint = "existing-rhino-fingerprint";
        await AssertConflictingClaimsRejectedAsync(
            OperationKind.ModifyRhinoObject,
            existingFingerprint,
            existingFingerprint);
    }

    private static async Task AssertConflictingClaimsRejectedAsync(
        OperationKind upsertKind,
        string? upsertExpectedFingerprint,
        string upsertWriteExpectation)
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        harness.Backend.SetPaused(true);
        var session = await harness.Store.CreateSessionAsync(
            new CreateSessionRequest("Rhino logical entity conflict"));
        var snapshot = await harness.CaptureSnapshotViewAsync();

        var primitiveObjectId = Guid.NewGuid();
        var upsertObjectId = Guid.NewGuid();
        const string logicalEntityId = "shared-logical-entity";
        const string primitiveOperationId = "create-primitive";
        const string upsertOperationId = "upsert-rhino-object";

        var primitiveArtifact = await harness.WritePayloadAsync(
            session,
            "logical-entity-primitive.json",
            new
            {
                bridgeOperation = "rhino.createPrimitive",
                arguments = new
                {
                    operationId = primitiveOperationId,
                    objectId = primitiveObjectId,
                    logicalEntityId,
                    kind = RhinoPrimitiveKind.Point,
                    point = new
                    {
                        location = new { x = 1.0, y = 2.0, z = 3.0 }
                    }
                }
            });
        var upsertArtifact = await harness.WritePayloadAsync(
            session,
            "logical-entity-upsert.json",
            new
            {
                bridgeOperation = "rhino.upsert",
                arguments = new
                {
                    operationId = upsertOperationId,
                    objectId = upsertObjectId,
                    logicalEntityId,
                    geometryType = "Point",
                    geometryJson = "{}",
                    attributesJson = "{}",
                    expectedFingerprint = upsertExpectedFingerprint
                }
            });

        var primitiveResource = new ResourceAddress(
            ResourceKind.RhinoObject,
            primitiveObjectId.ToString("D"));
        var upsertResource = new ResourceAddress(
            ResourceKind.RhinoObject,
            upsertObjectId.ToString("D"));
        var changeSet = new ChangeSet(
            Guid.NewGuid(),
            harness.Target.ProjectId,
            session.Id,
            snapshot.Revision,
            BaseGitCommit: null,
            Dependencies: [],
            ReadSet: [],
            WriteSet:
            [
                new ResourceExpectation(primitiveResource, ResourceExpectation.AbsentFingerprint),
                new ResourceExpectation(upsertResource, upsertWriteExpectation)
            ],
            Operations:
            [
                new TypedOperation(
                    primitiveOperationId,
                    OperationKind.CreateRhinoPrimitive,
                    AdapterOwner.RhinoBridge,
                    Reads: [],
                    Writes: [primitiveResource],
                    Reversible: true,
                    primitiveArtifact),
                new TypedOperation(
                    upsertOperationId,
                    upsertKind,
                    AdapterOwner.RhinoBridge,
                    Reads: [],
                    Writes: [upsertResource],
                    Reversible: true,
                    upsertArtifact)
            ],
            AcceptancePredicates:
            [
                new VerificationPredicate(
                    "No runtime errors",
                    PredicateKind.RuntimeErrorAbsent,
                    Resource: null,
                    ExpectedValue: null)
            ],
            RollbackBeforeImages: [],
            CreatedAt: DateTimeOffset.UtcNow);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, $"logical-entity-{upsertKind}"),
                CancellationToken.None));

        Assert.Contains(logicalEntityId, exception.Message, StringComparison.Ordinal);
        Assert.Contains(primitiveOperationId, exception.Message, StringComparison.Ordinal);
        Assert.Contains(upsertOperationId, exception.Message, StringComparison.Ordinal);
        Assert.Empty(harness.Backend.ReadQueue());
        Assert.Empty(responder.WriteOperationIds);
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
                summary = "Reject conflicting Rhino logical-entity claims"
            },
            BridgeProtocol.JsonOptions);
}
