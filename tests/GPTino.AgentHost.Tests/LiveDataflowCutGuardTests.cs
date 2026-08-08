using System.Security.Cryptography;
using System.Text;
using GPTino.AgentHost.Api;
using GPTino.BridgeContract;
using GPTino.Contracts;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// W3 Finding 2 — the DATAFLOW-CUTTING extension of the live-wire guard: a bare canvas.setWire
/// DISCONNECT whose consumer (the component losing an input) is a live foreign component, and a
/// python.setSchema that would drop a foreign component's wired inputs, take the same 3-branch
/// decision as a live-foreign delete (consumer self-authored / approval covering the consumer /
/// refuse pre-write). Without this, "orphan X in changeset 1, delete it freely in changeset 2"
/// reduced approval to submitting twice. The fixture chain is Source -> Stage -> Sink with stable
/// socket ids.
/// </summary>
[Collection(LiveDocumentBackendCollection.Name)]
public sealed class LiveDataflowCutGuardTests
{
    [Fact]
    public async Task BareForeignDisconnectIsRefusedPreWrite()
    {
        // This IS the two-changeset orphan-then-delete attack blocked at step 1: the bare
        // disconnect that would orphan the foreign stage never reaches the bridge.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Cutter"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await CreateDisconnectChangeSetAsync(harness, session, snapshot.Revision);

        var submitted = LiveDeleteGuardTests.ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            LiveDeleteGuardTests.Submission(changeSet, snapshot.Id, "cut-bare-key", "Bare disconnect"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);

        Assert.Equal("failed", state);
        var message = (await harness.ReadJobViewAsync(jobId)).GetProperty("message").GetString()!;
        // The refusal names the cut wire and teaches the approval path.
        Assert.Contains("Source → Stage", message, StringComparison.Ordinal);
        Assert.Contains("approval_request", message, StringComparison.Ordinal);
        Assert.Contains("approvalGrantId", message, StringComparison.Ordinal);
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task ForeignDisconnectWithApprovalPasses()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Approved cut"));
        // The grant covers the CONSUMER (Stage) at its current structure fingerprint.
        var grant = LiveDeleteGuardTests.ToElement(harness.Backend.MintApprovalGrant(
            [(harness.SecondCanvasObjectId, harness.SecondObjectStructureFingerprint)]));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await CreateDisconnectChangeSetAsync(
            harness, session, snapshot.Revision,
            approvalGrantId: grant.GetProperty("grantId").GetString());

        var submitted = LiveDeleteGuardTests.ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            LiveDeleteGuardTests.Submission(changeSet, snapshot.Id, "cut-approved-key", "Approved disconnect"),
            CancellationToken.None));
        var state = await harness.WaitForJobStateAsync(submitted.GetProperty("jobId").GetGuid());

        Assert.Equal("committed", state);
        Assert.Equal(new[] { "disconnect-stage-in" }, responder.WriteOperationIds);
    }

    [Fact]
    public async Task SelfAuthoredConsumerDisconnectPasses()
    {
        // Rewiring your own chain must not regress: the consumer's DIRECT ledger row at its
        // current structure fingerprint keeps the disconnect free.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Own chain"));
        harness.Backend.SeedResourceLedgerForTests(
            session,
            new ResourceAddress(ResourceKind.GrasshopperComponent, harness.SecondCanvasObjectId.ToString("D")),
            harness.SecondObjectStructureFingerprint);
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await CreateDisconnectChangeSetAsync(harness, session, snapshot.Revision);

        var submitted = LiveDeleteGuardTests.ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            LiveDeleteGuardTests.Submission(changeSet, snapshot.Id, "cut-self-key", "Rewire my own stage"),
            CancellationToken.None));
        var state = await harness.WaitForJobStateAsync(submitted.GetProperty("jobId").GetGuid());

        Assert.Equal("committed", state);
        Assert.Equal(new[] { "disconnect-stage-in" }, responder.WriteOperationIds);
    }

    [Fact]
    public async Task DisconnectOfConsumerInsideApprovedDeleteBatchPasses()
    {
        // Consumer ∈ D: the wire's consumer is deleted by this same batch, whose delete carries
        // the approval — the disconnect itself stays free and the batch commits as one.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Batch cut"));
        var grant = LiveDeleteGuardTests.ToElement(harness.Backend.MintApprovalGrant(
            [(harness.SecondCanvasObjectId, harness.SecondObjectStructureFingerprint)]));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var (deleteOperation, deleteExpectation) = await LiveDeleteGuardTests.CreateDeleteOperationAsync(
            harness, session, "delete-stage",
            harness.SecondCanvasObjectId, harness.SecondObjectStructureFingerprint);
        var (disconnectOperation, disconnectExpectation) = await CreateDisconnectOperationAsync(
            harness, session, "disconnect-stage-in");
        var changeSet = LiveDeleteGuardTests.CreateChangeSet(
            harness,
            session,
            snapshot.Revision,
            [deleteOperation, disconnectOperation],
            [deleteExpectation, disconnectExpectation],
            approvalGrantId: grant.GetProperty("grantId").GetString());

        var submitted = LiveDeleteGuardTests.ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            LiveDeleteGuardTests.Submission(changeSet, snapshot.Id, "cut-batch-key", "Approved batch"),
            CancellationToken.None));
        var state = await harness.WaitForJobStateAsync(submitted.GetProperty("jobId").GetGuid());

        Assert.Equal("committed", state);
        Assert.Equal(2, responder.WriteOperationIds.Count);
    }

    [Fact]
    public async Task ForeignSchemaDroppingAWiredInputIsRefusedPreWrite()
    {
        // The setSchema variant of the same cut: the declared inputs no longer include the
        // foreign stage's WIRED input "In", which would drop/rebind its wire.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters: [BridgeAdapterOwner.Canvas, BridgeAdapterOwner.Script]);
        harness.IncludeDeleteChain = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Schema cut"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await CreateSchemaDropChangeSetAsync(harness, session, snapshot.Revision);

        var submitted = LiveDeleteGuardTests.ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            LiveDeleteGuardTests.Submission(changeSet, snapshot.Id, "cut-schema-key", "Reshape foreign IO"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);

        Assert.Equal("failed", state);
        var message = (await harness.ReadJobViewAsync(jobId)).GetProperty("message").GetString()!;
        Assert.Contains("'In'", message, StringComparison.Ordinal);
        Assert.Contains("Source → In", message, StringComparison.Ordinal);
        Assert.Contains("approval_request", message, StringComparison.Ordinal);
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task SelfAuthoredSchemaReshapeOfWiredInputPasses()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters: [BridgeAdapterOwner.Canvas, BridgeAdapterOwner.Script]);
        harness.IncludeDeleteChain = true;
        // Script writes commit only when the responder plays the adapter's fingerprint chain
        // (inspect + before/after on the mutation), like every committing python-op test.
        await using var responder = harness.StartResponder(responseFactory: request => request.Operation switch
        {
            "python.inspect" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: false,
                new { componentId = request.Arguments.GetProperty("componentId").GetGuid() },
                afterFingerprint: "stage-io-v0"),
            "python.setSchema" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: true,
                new { applied = true },
                beforeFingerprint: request.ExpectedFingerprint,
                afterFingerprint: "stage-io-v1"),
            _ => null
        });
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Own schema"));
        harness.Backend.SeedResourceLedgerForTests(
            session,
            new ResourceAddress(ResourceKind.GrasshopperComponent, harness.SecondCanvasObjectId.ToString("D")),
            harness.SecondObjectStructureFingerprint);
        var snapshot = await harness.CaptureSnapshotViewAsync();
        // The enrichment python.inspect above serves the dedicated Io fingerprint, so the CAS
        // expectation binds to it directly (not to the parent-component fallback).
        var changeSet = await CreateSchemaDropChangeSetAsync(
            harness, session, snapshot.Revision, ioExpectation: "stage-io-v0");

        var submitted = LiveDeleteGuardTests.ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            LiveDeleteGuardTests.Submission(changeSet, snapshot.Id, "cut-schema-self-key", "Reshape my IO"),
            CancellationToken.None));
        var state = await harness.WaitForJobStateAsync(submitted.GetProperty("jobId").GetGuid());

        Assert.Equal("committed", state);
        Assert.Equal(new[] { "reshape-stage-io" }, responder.WriteOperationIds);
    }

    /// <summary>One bare disconnect of Source.Out -> Stage.In (consumer = the foreign Stage).</summary>
    private static async Task<ChangeSet> CreateDisconnectChangeSetAsync(
        LiveDocumentBackendHarness harness,
        SessionRecord session,
        long revision,
        string? approvalGrantId = null)
    {
        var (operation, expectation) = await CreateDisconnectOperationAsync(
            harness, session, "disconnect-stage-in");
        return LiveDeleteGuardTests.CreateChangeSet(
            harness, session, revision, [operation], [expectation], approvalGrantId);
    }

    private static async Task<(TypedOperation Operation, ResourceExpectation Expectation)> CreateDisconnectOperationAsync(
        LiveDocumentBackendHarness harness,
        SessionRecord session,
        string operationId)
    {
        // Guid "N" formatting is culture-invariant by construction.
        var wireId =
            $"{harness.CanvasObjectId:N}/{harness.SourceOutputParameterId:N}>" +
            $"{harness.SecondCanvasObjectId:N}/{harness.StageInputParameterId:N}";
        var resource = new ResourceAddress(ResourceKind.GrasshopperWire, wireId);
        var artifact = await harness.WritePayloadAsync(
            session,
            $"{operationId}.json",
            new
            {
                bridgeOperation = "canvas.setWire",
                arguments = new
                {
                    operationId,
                    wire = new
                    {
                        sourceObjectId = harness.CanvasObjectId,
                        sourceParameterId = harness.SourceOutputParameterId,
                        targetObjectId = harness.SecondCanvasObjectId,
                        targetParameterId = harness.StageInputParameterId,
                    },
                    action = "disconnect",
                    rejectCycles = true,
                }
            });
        var operation = new TypedOperation(
            operationId,
            OperationKind.DisconnectWire,
            AdapterOwner.Canvas,
            Array.Empty<ResourceAddress>(),
            [resource],
            Reversible: true,
            artifact);
        // An existing wire's snapshot fingerprint is Sha256(wireId) — mirror BuildResources.
        return (operation, new ResourceExpectation(resource, Sha256(wireId)));
    }

    /// <summary>
    /// A python.setSchema on Stage whose declared inputs rename the wired "In" away (counts stay
    /// append-only legal: 1 input, 1 output).
    /// </summary>
    private static async Task<ChangeSet> CreateSchemaDropChangeSetAsync(
        LiveDocumentBackendHarness harness,
        SessionRecord session,
        long revision,
        string? ioExpectation = null)
    {
        var ioResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentIo,
            harness.SecondCanvasObjectId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "reshape-stage-io.json",
            new
            {
                bridgeOperation = "python.setSchema",
                arguments = new
                {
                    operationId = "reshape-stage-io",
                    componentId = harness.SecondCanvasObjectId,
                    inputs = new[] { new { name = "x", access = "item" } },
                    outputs = new[] { new { name = "Out", access = "item" } },
                    preserveIncidentWires = true,
                }
            });
        var operation = new TypedOperation(
            "reshape-stage-io",
            OperationKind.SetComponentIo,
            AdapterOwner.Script,
            Array.Empty<ResourceAddress>(),
            [ioResource],
            Reversible: true,
            artifact);
        return LiveDeleteGuardTests.CreateChangeSet(
            harness, session, revision,
            [operation],
            // Default: the fixture snapshot has no dedicated Io row, so the CAS expectation is
            // validated against the overlapping PARENT component row — its structure fingerprint.
            // Tests whose responder serves a python.inspect fingerprint pass that value instead.
            [new ResourceExpectation(ioResource, ioExpectation ?? harness.SecondObjectStructureFingerprint)]);
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
