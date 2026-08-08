using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Data;
using GPTino.AgentHost.Hosting;
using GPTino.BridgeContract;
using GPTino.Contracts;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// W3 Layer 1 — the live-wire delete guard: a canvas delete whose target still has wires to
/// SURVIVING components (survivor-adjacent wires) is refused PRE-WRITE unless the resource
/// ledger PROVES this session authored the component (same session AND a DIRECT-origin row AND
/// the recorded fingerprint still equals the component's CURRENT structure fingerprint) or the
/// user's approval grant covers (objectId, current STRUCTURE fingerprint — the delete-CAS
/// domain). Orphans — every wire ends inside the same delete batch — stay freely deletable. The
/// refusal is a deterministic clean Failed (precondition_refused), observed at two points: the
/// job state AND zero dispatched writes. The fixture chain is Source -> Stage -> Sink
/// (IncludeDeleteChain), whose components carry SEPARATE structure/layout fingerprints.
/// </summary>
[Collection(LiveDocumentBackendCollection.Name)]
public sealed class LiveDeleteGuardTests
{
    [Fact]
    public async Task LiveForeignDeleteFailsCleanBeforeAnyWrite()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Guard"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        // Delete ONLY the stage: Source -> Stage and Stage -> Sink both survive the batch, so the
        // stage is LIVE; this fresh session never committed it and no grant covers it.
        var changeSet = await CreateDeleteChangeSetAsync(
            harness,
            session,
            snapshot.Revision,
            [("delete-stage", harness.SecondCanvasObjectId, harness.SecondObjectStructureFingerprint)]);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "guard-live-key", "Delete the live stage"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);

        // Observation 1: a clean deterministic Failed (not blocked, not recoveryRequired).
        Assert.Equal("failed", state);
        var jobView = await harness.ReadJobViewAsync(jobId);
        var message = jobView.GetProperty("message").GetString()!;
        // The teaching message lists the survivor-adjacent wires as "source nick → target nick"...
        Assert.Contains("Source → Stage", message, StringComparison.Ordinal);
        Assert.Contains("Stage → Sink", message, StringComparison.Ordinal);
        // ...and prescribes BOTH remedies: rewire-to-orphan-first, or a user approval bound to
        // the structure fingerprint (the delete-CAS domain — never the whole-object hash).
        Assert.Contains("orphan", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("approval_request", message, StringComparison.Ordinal);
        Assert.Contains("approvalGrantId", message, StringComparison.Ordinal);
        Assert.Contains("structure fingerprint", message, StringComparison.Ordinal);
        // Observation 2: the refusal happened PRE-WRITE — nothing was dispatched to the bridge.
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task OrphanBatchDeleteStillPasses()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Orphans"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        // The whole chain is deleted: every wire's other endpoint is also in the delete set, so
        // every target is an orphan of the batch — current behavior, fully allowed.
        var changeSet = await CreateDeleteChangeSetAsync(
            harness,
            session,
            snapshot.Revision,
            [
                ("delete-source", harness.CanvasObjectId, harness.ObjectStructureFingerprint),
                ("delete-stage", harness.SecondCanvasObjectId, harness.SecondObjectStructureFingerprint),
                ("delete-sink", harness.ThirdCanvasObjectId, harness.ThirdObjectStructureFingerprint),
            ]);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "guard-orphan-key", "Delete the whole chain"),
            CancellationToken.None));
        var state = await harness.WaitForJobStateAsync(submitted.GetProperty("jobId").GetGuid());

        Assert.Equal("committed", state);
        Assert.Equal(3, responder.WriteOperationIds.Count);
    }

    [Fact]
    public async Task SelfAuthoredLiveDeletePasses()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Own work"));
        // The ledger row (doc-scoped GrasshopperComponent entry) proves genuine authorship: THIS
        // session, DIRECT origin, recorded at the component's CURRENT structure fingerprint.
        harness.Backend.SeedResourceLedgerForTests(
            session,
            new ResourceAddress(ResourceKind.GrasshopperComponent, harness.SecondCanvasObjectId.ToString("D")),
            harness.SecondObjectStructureFingerprint);
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await CreateDeleteChangeSetAsync(
            harness,
            session,
            snapshot.Revision,
            [("delete-stage", harness.SecondCanvasObjectId, harness.SecondObjectStructureFingerprint)]);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "guard-self-key", "Delete my own live stage"),
            CancellationToken.None));
        var state = await harness.WaitForJobStateAsync(submitted.GetProperty("jobId").GetGuid());

        Assert.Equal("committed", state);
        Assert.Equal(new[] { "delete-stage" }, responder.WriteOperationIds);
    }

    [Fact]
    public async Task ForeignSessionLedgerRowDoesNotAuthorizeTheDelete()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var author = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Author"));
        var intruder = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Intruder"));
        // ANOTHER session authored the stage; the deleting session still has no claim.
        harness.Backend.SeedResourceLedgerForTests(
            author,
            new ResourceAddress(ResourceKind.GrasshopperComponent, harness.SecondCanvasObjectId.ToString("D")),
            harness.SecondObjectStructureFingerprint);
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await CreateDeleteChangeSetAsync(
            harness,
            intruder,
            snapshot.Revision,
            [("delete-stage", harness.SecondCanvasObjectId, harness.SecondObjectStructureFingerprint)]);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            intruder,
            Submission(changeSet, snapshot.Id, "guard-foreign-key", "Delete another session's stage"),
            CancellationToken.None));
        var state = await harness.WaitForJobStateAsync(submitted.GetProperty("jobId").GetGuid());

        Assert.Equal("failed", state);
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task StaleOwnershipDoesNotAuthorizeTheDelete()
    {
        // FINDING 1(a) — Scenario B: a session that authored the stage long ago keeps a DIRECT
        // ledger row, but the USER has since rewired it (its structure fingerprint moved). The
        // "same session AND unchanged" predicate must refuse — old authorship is not current
        // authorship.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Stale author"));
        harness.Backend.SeedResourceLedgerForTests(
            session,
            new ResourceAddress(ResourceKind.GrasshopperComponent, harness.SecondCanvasObjectId.ToString("D")),
            "structure-v2-before-user-rewire"); // != current SecondObjectStructureFingerprint
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await CreateDeleteChangeSetAsync(
            harness,
            session,
            snapshot.Revision,
            [("delete-stage", harness.SecondCanvasObjectId, harness.SecondObjectStructureFingerprint)]);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "guard-stale-key", "Delete a since-rewired stage"),
            CancellationToken.None));
        var state = await harness.WaitForJobStateAsync(submitted.GetProperty("jobId").GetGuid());

        Assert.Equal("failed", state);
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task ObservedLedgerRowDoesNotAuthorizeTheDelete()
    {
        // FINDING 1(b) — the origin gate in isolation: a row recorded from a side-effect snapshot
        // diff (Observed) matches session AND current fingerprint, yet must not authorize a delete.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Toucher"));
        harness.Backend.SeedResourceLedgerForTests(
            session,
            new ResourceAddress(ResourceKind.GrasshopperComponent, harness.SecondCanvasObjectId.ToString("D")),
            harness.SecondObjectStructureFingerprint,
            ResourceLedgerOrigin.Observed);
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await CreateDeleteChangeSetAsync(
            harness,
            session,
            snapshot.Revision,
            [("delete-stage", harness.SecondCanvasObjectId, harness.SecondObjectStructureFingerprint)]);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "guard-observed-key", "Delete a merely-touched stage"),
            CancellationToken.None));
        var state = await harness.WaitForJobStateAsync(submitted.GetProperty("jobId").GetGuid());

        Assert.Equal("failed", state);
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task WiringAForeignComponentThenDeletingItIsRefused()
    {
        // FINDING 1(b) — Scenario A end-to-end: merely wiring a foreign live component moves its
        // structure fingerprint, the commit's ledger diff records that move under this session —
        // but as an OBSERVED row, so the next changeset's delete must still be refused.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder(responseFactory: request =>
        {
            if (string.Equals(request.Operation, "canvas.setWire", StringComparison.Ordinal))
            {
                // The wire re-solve moves the consumer's structure fingerprint, exactly like the
                // real adapter (incoming wires are hashed into the structure domain).
                harness.ThirdObjectStructureFingerprint = "structure-v3-wired";
            }
            return null;
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Escalation"));

        // ChangeSet 1: wire Source.Out -> Sink.In (a legitimate build op) and commit it.
        var snapshot = await harness.CaptureSnapshotViewAsync();
        // Guid "N" formatting is culture-invariant by construction.
        var wireId =
            $"{harness.CanvasObjectId:N}/{harness.SourceOutputParameterId:N}>" +
            $"{harness.ThirdCanvasObjectId:N}/{harness.SinkInputParameterId:N}";
        var wireResource = new ResourceAddress(ResourceKind.GrasshopperWire, wireId);
        var wireArtifact = await harness.WritePayloadAsync(
            session,
            "wire-sink.json",
            new
            {
                bridgeOperation = "canvas.setWire",
                arguments = new
                {
                    operationId = "wire-sink",
                    wire = new
                    {
                        sourceObjectId = harness.CanvasObjectId,
                        sourceParameterId = harness.SourceOutputParameterId,
                        targetObjectId = harness.ThirdCanvasObjectId,
                        targetParameterId = harness.SinkInputParameterId,
                    },
                    action = "connect",
                    rejectCycles = true,
                }
            });
        var wireChangeSet = CreateChangeSet(
            harness,
            session,
            snapshot.Revision,
            [
                new TypedOperation(
                    "wire-sink",
                    OperationKind.ConnectWire,
                    AdapterOwner.Canvas,
                    Array.Empty<ResourceAddress>(),
                    [wireResource],
                    Reversible: true,
                    wireArtifact)
            ],
            [new ResourceExpectation(wireResource, ResourceExpectation.AbsentFingerprint)]);
        var wireSubmitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(wireChangeSet, snapshot.Id, "guard-escalate-wire-key", "Wire into the sink"),
            CancellationToken.None));
        Assert.Equal("committed", await harness.WaitForJobStateAsync(
            wireSubmitted.GetProperty("jobId").GetGuid()));

        // ChangeSet 2: delete the sink at its CURRENT (moved) structure fingerprint. Session and
        // fingerprint both match the ledger row the wire commit recorded — origin must refuse.
        var afterWire = await harness.CaptureSnapshotViewAsync();
        var deleteChangeSet = await CreateDeleteChangeSetAsync(
            harness,
            session,
            afterWire.Revision,
            [("delete-sink", harness.ThirdCanvasObjectId, "structure-v3-wired")]);
        var deleteSubmitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(deleteChangeSet, afterWire.Id, "guard-escalate-delete-key", "Delete the wired sink"),
            CancellationToken.None));
        var state = await harness.WaitForJobStateAsync(deleteSubmitted.GetProperty("jobId").GetGuid());

        Assert.Equal("failed", state);
        // Only the wire write ever reached the bridge — the delete was refused pre-write.
        Assert.Equal(new[] { "wire-sink" }, responder.WriteOperationIds);
    }

    [Fact]
    public async Task ApprovedLiveDeletePassesAndConsumesTheGrant()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Approved"));
        // The user approved exactly (stage objectId, current STRUCTURE fingerprint) — the same
        // fingerprint domain the delete CAS and job results expose.
        var grant = ToElement(harness.Backend.MintApprovalGrant(
            [(harness.SecondCanvasObjectId, harness.SecondObjectStructureFingerprint)]));
        var grantId = grant.GetProperty("grantId").GetString()!;
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await CreateDeleteChangeSetAsync(
            harness,
            session,
            snapshot.Revision,
            [("delete-stage", harness.SecondCanvasObjectId, harness.SecondObjectStructureFingerprint)],
            approvalGrantId: grantId);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "guard-approved-key", "Delete the approved stage"),
            CancellationToken.None));
        var state = await harness.WaitForJobStateAsync(submitted.GetProperty("jobId").GetGuid());

        // Observation 1: the grant-covered live delete commits and the write dispatched.
        Assert.Equal("committed", state);
        Assert.Equal(new[] { "delete-stage" }, responder.WriteOperationIds);

        // Observation 2: the approval was ONE application — the commit consumed the grant, so a
        // FRESH submission carrying the same grantId is refused as unknown/expired.
        var replayChangeSet = await CreateDeleteChangeSetAsync(
            harness,
            session,
            snapshot.Revision,
            [("delete-stage-again", harness.SecondCanvasObjectId, harness.SecondObjectStructureFingerprint)],
            approvalGrantId: grantId);
        var consumed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(replayChangeSet, snapshot.Id, "guard-consumed-key", "Replay the grant"),
                CancellationToken.None));
        Assert.Contains("unknown or expired", consumed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrantBoundToTheWholeObjectFingerprintIsRefused()
    {
        // FINDING 3: the grant must bind to the STRUCTURE fingerprint (what the delete CAS and
        // job results expose). A grant carrying the whole-object hash — the old comparison domain
        // — must NOT match, or a mere auto-tidy move would void real grants and structure-domain
        // grants would never apply.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Wrong domain"));
        var grant = ToElement(harness.Backend.MintApprovalGrant(
            [(harness.SecondCanvasObjectId, harness.SecondObjectFingerprint)])); // whole-object hash
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await CreateDeleteChangeSetAsync(
            harness,
            session,
            snapshot.Revision,
            [("delete-stage", harness.SecondCanvasObjectId, harness.SecondObjectStructureFingerprint)],
            approvalGrantId: grant.GetProperty("grantId").GetString());

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "guard-wrongfp-key", "Delete with a wrong-domain grant"),
            CancellationToken.None));
        var state = await harness.WaitForJobStateAsync(submitted.GetProperty("jobId").GetGuid());

        Assert.Equal("failed", state);
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task PartialGrantCoverageRefusesPreWriteAndKeepsTheGrant()
    {
        // FINDING 3: a batch whose grant covers only SOME live targets must refuse PRE-WRITE with
        // the grant intact — a half-applied approval would be worse than no approval.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Partial"));
        // Deleting Source AND Sink while Stage survives leaves BOTH targets live
        // (Source -> Stage and Stage -> Sink each cross the batch boundary).
        var grant = ToElement(harness.Backend.MintApprovalGrant(
            [(harness.CanvasObjectId, harness.ObjectStructureFingerprint)])); // covers Source only
        var grantId = grant.GetProperty("grantId").GetString()!;
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await CreateDeleteChangeSetAsync(
            harness,
            session,
            snapshot.Revision,
            [
                ("delete-source", harness.CanvasObjectId, harness.ObjectStructureFingerprint),
                ("delete-sink", harness.ThirdCanvasObjectId, harness.ThirdObjectStructureFingerprint),
            ],
            approvalGrantId: grantId);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "guard-partial-key", "Delete beyond the grant"),
            CancellationToken.None));
        var state = await harness.WaitForJobStateAsync(submitted.GetProperty("jobId").GetGuid());

        // Refused pre-write: no write dispatched, so nothing was half-applied...
        Assert.Equal("failed", state);
        Assert.Empty(responder.WriteOperationIds);

        // ...and the grant is intact: the covered target alone still commits with the SAME grant
        // (grants are consumed only by a commit, never by a refusal).
        var retryChangeSet = await CreateDeleteChangeSetAsync(
            harness,
            session,
            snapshot.Revision,
            [("delete-source", harness.CanvasObjectId, harness.ObjectStructureFingerprint)],
            approvalGrantId: grantId);
        var retry = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(retryChangeSet, snapshot.Id, "guard-partial-retry-key", "Delete the covered target"),
            CancellationToken.None));
        Assert.Equal("committed", await harness.WaitForJobStateAsync(retry.GetProperty("jobId").GetGuid()));
        Assert.Equal(new[] { "delete-source" }, responder.WriteOperationIds);
    }

    [Fact]
    public async Task MixedBatchWithLiveForeignDeleteIsRejectedAtSubmitNamingTheOperations()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var author = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Author"));
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Mixed"));
        // ANOTHER session's row: the ledger positively KNOWS this session did not author the
        // stage, so the refusal teaches the rebuild sequence (the cold could-not-confirm wording
        // is covered separately below).
        harness.Backend.SeedResourceLedgerForTests(
            author,
            new ResourceAddress(ResourceKind.GrasshopperComponent, harness.SecondCanvasObjectId.ToString("D")),
            harness.SecondObjectStructureFingerprint);
        var snapshot = await harness.CaptureSnapshotViewAsync();

        var changeSet = await CreateMixedRebuildChangeSetAsync(harness, session, snapshot.Revision);

        var rejection = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "guard-mixed-key", "Rebuild in one batch"),
                CancellationToken.None));

        // The submit-time refusal names BOTH sides — the live foreign delete and the build ops —
        // and teaches the safe sequence.
        Assert.Contains("delete-stage", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("create-replacement", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("author → rewire → delete-orphans", rejection.Message, StringComparison.Ordinal);
        // Nothing was enqueued, nothing dispatched.
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task ColdLedgerMixedBatchConsultsTheDurableStoreReadOnly()
    {
        // FINDING 5: after a restart the in-memory ledger is empty, but the durable store still
        // proves this session authored the stage — the submit-time mixed-batch check must consult
        // it READ-ONLY instead of false-rejecting, and execution then re-proves authorship after
        // hydration.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Cold author"));
        var docKey = AgentHostOptions.ComputeDocumentKey(harness.Options.GrasshopperPath);
        var store = new ResourceLedgerStore(
            Path.Combine(harness.Options.ResolveDataDirectory(), "resource-ledger.db"));
        await store.InitializeAsync();
        var resourceKey =
            $"{ResourceKind.GrasshopperComponent}:{harness.SecondCanvasObjectId:D}:*";
        await store.UpsertAsync(docKey,
        [
            new ResourceLedgerRecord(
                resourceKey,
                new ResourceAddress(ResourceKind.GrasshopperComponent, harness.SecondCanvasObjectId.ToString("D")),
                harness.SecondObjectStructureFingerprint,
                session.Id,
                Revision: 1,
                ResourceLedgerOrigin.Direct),
        ]);
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await CreateMixedRebuildChangeSetAsync(harness, session, snapshot.Revision);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "guard-cold-hit-key", "Rebuild after restart"),
            CancellationToken.None));
        var state = await harness.WaitForJobStateAsync(submitted.GetProperty("jobId").GetGuid());

        Assert.Equal("committed", state);
        Assert.Equal(new[] { "delete-stage", "create-replacement" }, responder.WriteOperationIds);
    }

    [Fact]
    public async Task ColdLedgerMixedBatchRefusalTeachesTheRightCause()
    {
        // FINDING 5: when NEITHER ledger has a row, the refusal must say authorship could not be
        // CONFIRMED (not assert "did not author it") and teach the two real remedies.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Cold unknown"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await CreateMixedRebuildChangeSetAsync(harness, session, snapshot.Revision);

        var rejection = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "guard-cold-miss-key", "Rebuild with no ledger"),
                CancellationToken.None));

        Assert.Contains("could not be confirmed", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("submit the deletes in their own ChangeSet", rejection.Message, StringComparison.Ordinal);
        Assert.Contains("approval_request", rejection.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("did not author", rejection.Message, StringComparison.Ordinal);
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task BracedGuidDeletePayloadIsRejectedAtSubmit()
    {
        // FINDING 7: Guid.TryParse used to accept braced forms the adapter's STJ deserialization
        // rejects — an engineered mid-batch adapter failure. The host now requires the exact "D"
        // format, so the payload dies at submit with nothing enqueued.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Braced"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponent, harness.SecondCanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "delete-braced.json",
            new
            {
                bridgeOperation = "canvas.delete",
                arguments = new
                {
                    operationId = "delete-braced",
                    objectId = $"{{{harness.SecondCanvasObjectId:D}}}", // braced "B" format
                    expectedFingerprint = harness.SecondObjectStructureFingerprint,
                }
            });
        var changeSet = CreateChangeSet(
            harness,
            session,
            snapshot.Revision,
            [
                new TypedOperation(
                    "delete-braced",
                    OperationKind.DeleteComponent,
                    AdapterOwner.Canvas,
                    Array.Empty<ResourceAddress>(),
                    [resource],
                    Reversible: false,
                    artifact)
            ],
            [new ResourceExpectation(resource, harness.SecondObjectStructureFingerprint)]);

        var rejection = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(changeSet, snapshot.Id, "guard-braced-key", "Delete with a braced id"),
                CancellationToken.None));

        Assert.Contains("canonical dashed", rejection.Message, StringComparison.Ordinal);
        Assert.Empty(responder.WriteOperationIds);
    }

    /// <summary>Delete-stage (live foreign) + create-replacement — the canonical banned mixed batch.</summary>
    private static async Task<ChangeSet> CreateMixedRebuildChangeSetAsync(
        LiveDocumentBackendHarness harness,
        SessionRecord session,
        long revision)
    {
        var (deleteOperation, deleteExpectation) = await CreateDeleteOperationAsync(
            harness, session, "delete-stage",
            harness.SecondCanvasObjectId, harness.SecondObjectStructureFingerprint);
        var newComponentId = Guid.Parse("5d1f70a4-93ab-4f43-9c5e-7f34a7f0c210");
        var createResource = new ResourceAddress(ResourceKind.GrasshopperComponent, newComponentId.ToString("D"));
        var createArtifact = await harness.WritePayloadAsync(
            session,
            "create-replacement.json",
            new
            {
                bridgeOperation = "canvas.create",
                arguments = new
                {
                    operationId = "create-replacement",
                    objectId = newComponentId,
                    componentTypeId = Guid.Parse("29322931-96ae-4d34-874b-a722bc3a0e4a"),
                    pivot = new { x = 400.0, y = 40.0 },
                }
            });
        var createOperation = new TypedOperation(
            "create-replacement",
            OperationKind.CreateComponent,
            AdapterOwner.Canvas,
            Array.Empty<ResourceAddress>(),
            [createResource],
            Reversible: false,
            createArtifact);
        return CreateChangeSet(
            harness,
            session,
            revision,
            [deleteOperation, createOperation],
            [
                deleteExpectation,
                new ResourceExpectation(createResource, ResourceExpectation.AbsentFingerprint),
            ]);
    }

    private static async Task<ChangeSet> CreateDeleteChangeSetAsync(
        LiveDocumentBackendHarness harness,
        SessionRecord session,
        long revision,
        (string OperationId, Guid ObjectId, string Fingerprint)[] deletes,
        string? approvalGrantId = null,
        string? intent = null)
    {
        var operations = new List<TypedOperation>(deletes.Length);
        var writeSet = new List<ResourceExpectation>(deletes.Length);
        foreach (var (operationId, objectId, fingerprint) in deletes)
        {
            var prepared = await CreateDeleteOperationAsync(harness, session, operationId, objectId, fingerprint);
            operations.Add(prepared.Operation);
            writeSet.Add(prepared.Expectation);
        }
        return new ChangeSet(
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

    internal static ChangeSet CreateChangeSet(
        LiveDocumentBackendHarness harness,
        SessionRecord session,
        long revision,
        IReadOnlyList<TypedOperation> operations,
        IReadOnlyList<ResourceExpectation> writeSet,
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
            ApprovalGrantId: approvalGrantId);

    internal static async Task<(TypedOperation Operation, ResourceExpectation Expectation)> CreateDeleteOperationAsync(
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

    internal static JsonElement Submission(
        ChangeSet changeSet,
        string snapshotId,
        string idempotencyKey,
        string summary) =>
        JsonSerializer.SerializeToElement(
            new { changeSet, expectedSnapshotId = snapshotId, idempotencyKey, summary },
            BridgeProtocol.JsonOptions);

    internal static JsonElement ToElement(object value) =>
        JsonSerializer.SerializeToElement(value, value.GetType(), BridgeProtocol.JsonOptions);
}
