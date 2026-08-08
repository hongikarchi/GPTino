using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.BridgeContract;
using GPTino.Contracts;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// W3 Layer 2 — the declared cleanup intent (ChangeSet.Intent, wire name "intent"): a submit-time
/// tier gate. cleanupRelayout admits {moveComponent, setLayout}; cleanupRegroup adds setGroup;
/// cleanupDestructive adds deleteComponent — WITHOUT demanding a grant at submit (orphan and
/// self-authored deletes are the honest destructive cleanup; the Layer-1 guard is the authority
/// that refuses live-foreign targets at execution). A supplied grant id must still resolve.
/// Null intent = authoring, no tier restriction (Layer 1 still applies). The host's own auto-tidy
/// stamps cleanupRelayout and must pass its own gate.
/// </summary>
[Collection(LiveDocumentBackendCollection.Name)]
public sealed class CleanupIntentTests
{
    [Fact]
    public async Task RelayoutIntentRejectsADeleteOperationAtSubmit()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Tiers"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var (deleteOperation, deleteExpectation) = await LiveDeleteGuardTests.CreateDeleteOperationAsync(
            harness, session, "delete-source", harness.CanvasObjectId, harness.ObjectStructureFingerprint);
        var changeSet = CreateChangeSet(
            harness, session, snapshot.Revision, [deleteOperation], [deleteExpectation],
            intent: CleanupIntents.Relayout);

        var rejection = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                LiveDeleteGuardTests.Submission(changeSet, snapshot.Id, "tier-relayout-key", "Sneaky delete"),
                CancellationToken.None));

        Assert.Contains(CleanupIntents.Relayout, rejection.Message, StringComparison.Ordinal);
        Assert.Contains("delete-source", rejection.Message, StringComparison.Ordinal);
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task UnknownIntentIsRejectedWithTheValidTiers()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Tiers"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = (await harness.CreateChangeSetAsync(session, "move-1", snapshot.Revision))
            with
            { Intent = "cleanupEverything" };

        var rejection = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                LiveDeleteGuardTests.Submission(changeSet, snapshot.Id, "tier-unknown-key", "Bad intent"),
                CancellationToken.None));

        Assert.Contains("Unknown cleanup intent", rejection.Message, StringComparison.Ordinal);
        Assert.Contains(CleanupIntents.Destructive, rejection.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DestructiveOrphanOnlyCleanupWithoutAGrantCommits()
    {
        // FINDING 4: honest destructive intent must be usable without a grant when every target is
        // an orphan — the execution-time guard stays the authority for live-foreign targets.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Tiers"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var operations = new List<TypedOperation>();
        var writeSet = new List<ResourceExpectation>();
        foreach (var (operationId, objectId, fingerprint) in new[]
        {
            ("delete-source", harness.CanvasObjectId, harness.ObjectStructureFingerprint),
            ("delete-stage", harness.SecondCanvasObjectId, harness.SecondObjectStructureFingerprint),
            ("delete-sink", harness.ThirdCanvasObjectId, harness.ThirdObjectStructureFingerprint),
        })
        {
            var prepared = await LiveDeleteGuardTests.CreateDeleteOperationAsync(
                harness, session, operationId, objectId, fingerprint);
            operations.Add(prepared.Operation);
            writeSet.Add(prepared.Expectation);
        }
        // The whole chain: every wire ends inside the delete batch, so every target is an orphan.
        var changeSet = CreateChangeSet(
            harness, session, snapshot.Revision, operations, writeSet,
            intent: CleanupIntents.Destructive);

        var submitted = LiveDeleteGuardTests.ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            LiveDeleteGuardTests.Submission(changeSet, snapshot.Id, "tier-nogrant-key", "Orphan cleanup"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);

        Assert.Equal("committed", state);
        var jobView = await harness.ReadJobViewAsync(jobId);
        Assert.Equal(CleanupIntents.Destructive, jobView.GetProperty("intent").GetString());
        Assert.Equal(3, responder.WriteOperationIds.Count);
    }

    [Fact]
    public async Task DestructiveIntentWithAnUnknownGrantIdStillFailsToResolve()
    {
        // FINDING 4 keeps one half: destructive needs no grant, but a grant id that IS present
        // must resolve — a fabricated/expired id is refused with the re-approval teaching.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Tiers"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var (deleteOperation, deleteExpectation) = await LiveDeleteGuardTests.CreateDeleteOperationAsync(
            harness, session, "delete-source", harness.CanvasObjectId, harness.ObjectStructureFingerprint);
        var changeSet = CreateChangeSet(
            harness, session, snapshot.Revision, [deleteOperation], [deleteExpectation],
            intent: CleanupIntents.Destructive,
            approvalGrantId: "deadbeefdeadbeefdeadbeefdeadbeef");

        var rejection = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                LiveDeleteGuardTests.Submission(changeSet, snapshot.Id, "tier-badgrant-key", "Bogus grant"),
                CancellationToken.None));

        Assert.Contains("unknown or expired", rejection.Message, StringComparison.Ordinal);
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task DestructiveIntentWithAGrantOverCoveredTargetsCommits()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Tiers"));
        // The grant binds to the STRUCTURE fingerprint — the delete-CAS domain (Finding 3).
        var grant = LiveDeleteGuardTests.ToElement(harness.Backend.MintApprovalGrant(
            [(harness.SecondCanvasObjectId, harness.SecondObjectStructureFingerprint)]));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var (deleteOperation, deleteExpectation) = await LiveDeleteGuardTests.CreateDeleteOperationAsync(
            harness, session, "delete-stage", harness.SecondCanvasObjectId, harness.SecondObjectStructureFingerprint);
        var changeSet = CreateChangeSet(
            harness, session, snapshot.Revision, [deleteOperation], [deleteExpectation],
            intent: CleanupIntents.Destructive,
            approvalGrantId: grant.GetProperty("grantId").GetString());

        var submitted = LiveDeleteGuardTests.ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            LiveDeleteGuardTests.Submission(changeSet, snapshot.Id, "tier-grant-key", "Approved destructive cleanup"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);

        // The live delete passed the Layer-1 guard through the grant, the tier admitted it, and
        // the job view surfaces the declared intent (the cheap cleanup label for projections).
        Assert.Equal("committed", state);
        var jobView = await harness.ReadJobViewAsync(jobId);
        Assert.Equal(CleanupIntents.Destructive, jobView.GetProperty("intent").GetString());
        Assert.Equal(new[] { "delete-stage" }, responder.WriteOperationIds);
    }

    [Fact]
    public async Task ArrangeStampsRelayoutIntentAndPassesItsOwnGate()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeNumberSliderValue = true; // adds the second component
        harness.WireFirstTwoObjects = true;      // wires first -> second, so a real cluster exists
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Tidy"));

        var result = LiveDeleteGuardTests.ToElement(await harness.Backend.ArrangeLayoutAsync(
            session,
            JsonSerializer.SerializeToElement(
                new
                {
                    seedComponentIds = new[]
                    {
                        harness.CanvasObjectId.ToString("D"),
                        harness.SecondCanvasObjectId.ToString("D"),
                    },
                    wait = false,
                },
                BridgeProtocol.JsonOptions),
            CancellationToken.None));

        Assert.True(result.TryGetProperty("jobId", out var jobIdElement),
            "arrange_layout should submit a move job; got: " + result);
        var state = await harness.WaitForJobStateAsync(jobIdElement.GetGuid());
        var jobView = await harness.ReadJobViewAsync(jobIdElement.GetGuid());
        // Committed = the host's own tidy passed the tier gate it declared; the job view carries
        // the stamped relayout intent.
        Assert.Equal("committed", state);
        Assert.Equal(CleanupIntents.Relayout, jobView.GetProperty("intent").GetString());
    }

    [Fact]
    public async Task NullIntentKeepsAuthoringUnrestricted()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Authoring"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await harness.CreateChangeSetAsync(session, "move-free", snapshot.Revision);
        Assert.Null(changeSet.Intent);

        var submitted = LiveDeleteGuardTests.ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            LiveDeleteGuardTests.Submission(changeSet, snapshot.Id, "tier-null-key", "Plain authoring move"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);

        Assert.Equal("committed", state);
        var jobView = await harness.ReadJobViewAsync(jobId);
        Assert.Equal(JsonValueKind.Null, jobView.GetProperty("intent").ValueKind);
    }

    private static ChangeSet CreateChangeSet(
        LiveDocumentBackendHarness harness,
        SessionRecord session,
        long revision,
        IReadOnlyList<TypedOperation> operations,
        IReadOnlyList<ResourceExpectation> writeSet,
        string? intent = null,
        string? approvalGrantId = null) =>
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
            DateTimeOffset.UtcNow,
            ApprovalGrantId: approvalGrantId,
            Intent: intent);
}
