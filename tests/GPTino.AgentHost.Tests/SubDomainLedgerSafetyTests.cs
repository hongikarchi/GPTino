using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Runtime;
using GPTino.BridgeContract;
using GPTino.Contracts;
using GPTino.ScriptAdapter;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// Sub-domain resource-ledger safety (live gate 20260807T175523Z-d1884d03). The raw snapshot
/// carries no Source/Io (or python Value) rows, so the pre-fix ledger never recorded script
/// sub-domain writes and every source auto resolved through the parent-ownership fallback —
/// which cannot see a foreign or manual SOURCE change, because the parent structure fingerprint
/// hashes nickname/sockets/wires only, never the source text. These tests pin the fixed
/// contract: source/value autos require a DIRECT session-owned ledger row (seeded at component
/// creation, advanced on every script write and on structure moves the session applies), a
/// foreign write flips ownership and DECLINES the old owner's auto, manual script drift
/// declines, and the standard authoring chain stays decline-free.
/// </summary>
[Collection(LiveDocumentBackendCollection.Name)]
public sealed class SubDomainLedgerSafetyTests
{
    private static readonly BridgeAdapterOwner[] Adapters =
    [
        BridgeAdapterOwner.Canvas,
        BridgeAdapterOwner.Script
    ];

    /// <summary>The evolving live Python state of the one script component in the fake world.
    /// Script writes and structure edits advance it exactly like the real adapter's whole-state
    /// fingerprint (source + schema + typing + runtime messages).</summary>
    private sealed class ScriptWorld
    {
        public string Fingerprint = "py-f0";
    }

    /// <summary>
    /// One stateful responder for the whole authoring conversation: canvas.create flips the
    /// harness canvas to include the created script component; python.inspect always reports the
    /// CURRENT world fingerprint (serving both snapshot enrichment and commit-time ledger
    /// recording); every script write advances the world fingerprint and echoes the resolved
    /// expectation as its before-fingerprint, like the real Script adapter's chain. canvas.move
    /// on the created component models a structure-moving canvas edit whose re-solve also moves
    /// the script state (the wire→execute hot path): the component fingerprint AND the world
    /// fingerprint both advance.
    /// </summary>
    private static FakeBridgeResponder StartWorldResponder(
        LiveDocumentBackendHarness harness,
        ScriptWorld world) =>
        harness.StartResponder(responseFactory: request =>
        {
            switch (request.Operation)
            {
                case "canvas.create":
                    harness.IncludeCreatedComponent = true;
                    return null;
                case "canvas.move":
                    harness.CreatedComponentFingerprint = "created-v2-moved";
                    world.Fingerprint += "+resolve";
                    return null;
                case "python.inspect":
                    return BridgeOperationResponse.Create(
                        request.OperationId,
                        changed: false,
                        new { componentId = request.Arguments.GetProperty("componentId").GetGuid() },
                        afterFingerprint: world.Fingerprint);
                case "python.setSource":
                case "python.setSchema":
                case "python.execute":
                    world.Fingerprint = $"{world.Fingerprint}+{request.Operation[7..]}";
                    return BridgeOperationResponse.Create(
                        request.OperationId,
                        changed: true,
                        new { applied = true },
                        beforeFingerprint: request.ExpectedFingerprint,
                        afterFingerprint: world.Fingerprint);
                default:
                    return null;
            }
        });

    [Fact]
    public async Task ForeignSourceWriteFlipsOwnershipAndDeclinesTheOldOwnersAuto()
    {
        // The live canary, in-process: session A creates the script component, session B commits
        // a source write with a concrete fingerprint, then A submits setSource with gptino:auto.
        // Pre-fix this COMMITTED through the parent fallback (B's source-only write does not move
        // the component structure fingerprint); it must DECLINE as a foreign write.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters: Adapters);
        var world = new ScriptWorld();
        await using var responder = StartWorldResponder(harness, world);
        var author = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Author"));
        var foreign = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Foreign"));

        await CommitCreateAsync(harness, author, "canary-create");
        var (foreignState, foreignView) = await SubmitSetSourceAsync(
            harness, foreign, "canary-foreign-write", expectedFingerprint: world.Fingerprint);
        Assert.True(foreignState == "committed", foreignView.GetProperty("message").GetString());

        var (state, view) = await SubmitSetSourceAsync(
            harness, author, "canary-owner-auto", expectedFingerprint: ResourceExpectation.AutoFingerprint);

        Assert.Equal("blocked", state);
        var message = view.GetProperty("message").GetString();
        Assert.Contains("gptino:auto declined", message, StringComparison.Ordinal);
        Assert.Contains("another session", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManualScriptDriftDeclinesTheOwnersAuto()
    {
        // A manual Grasshopper edit of the script state (source text / schema / runtime messages)
        // between the session's jobs: the live Python-state fingerprint moves while the session's
        // direct ledger row still holds the old value — the auto must decline as drifted, even
        // though the component structure fingerprint never moved.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters: Adapters);
        var world = new ScriptWorld();
        await using var responder = StartWorldResponder(harness, world);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Author"));

        await CommitCreateAsync(harness, session, "drift-create");
        world.Fingerprint += "+manual-edit"; // the manual edit; no job, no ledger update

        var (state, view) = await SubmitSetSourceAsync(
            harness, session, "drift-owner-auto", expectedFingerprint: ResourceExpectation.AutoFingerprint);

        Assert.Equal("blocked", state);
        var message = view.GetProperty("message").GetString();
        Assert.Contains("gptino:auto declined", message, StringComparison.Ordinal);
        Assert.Contains("drifted", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StandardAuthoringChainResolvesWithoutDeclines()
    {
        // The product's hot path: createComponent → updatePythonSource(auto) → setComponentIo(auto)
        // → execute(auto), each as its own job. Creation seeds the direct Source/Io/Value rows and
        // every script commit advances all three (they share the ONE Python-state fingerprint), so
        // the whole chain must resolve with zero declines and no parent fallback.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters: Adapters);
        var world = new ScriptWorld();
        await using var responder = StartWorldResponder(harness, world);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Author"));

        await CommitCreateAsync(harness, session, "chain-create");

        var (sourceState, sourceView) = await SubmitSetSourceAsync(
            harness, session, "chain-source", expectedFingerprint: ResourceExpectation.AutoFingerprint);
        Assert.True(sourceState == "committed", sourceView.GetProperty("message").GetString());

        var (ioState, ioView) = await SubmitSetSchemaAsync(harness, session, "chain-io");
        Assert.True(ioState == "committed", ioView.GetProperty("message").GetString());

        var (executeState, executeView) = await SubmitExecuteAsync(harness, session, "chain-execute");
        Assert.True(executeState == "committed", executeView.GetProperty("message").GetString());
    }

    [Fact]
    public async Task StructureMovingCanvasEditKeepsTheOwnersScriptChainAlive()
    {
        // The wire→execute analog: after authoring the script state, the SAME session applies a
        // canvas edit that moves the component structure fingerprint AND (via the re-solve's
        // runtime messages) the live Python-state fingerprint. Commit-time recording must refresh
        // the session's own sub-domain rows to the new live value, or the next execute(auto)
        // declines as drifted — the exact chain the removed parent fallback used to carry.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters: Adapters);
        var world = new ScriptWorld();
        await using var responder = StartWorldResponder(harness, world);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Author"));

        await CommitCreateAsync(harness, session, "wire-create");
        var (sourceState, sourceView) = await SubmitSetSourceAsync(
            harness, session, "wire-source", expectedFingerprint: ResourceExpectation.AutoFingerprint);
        Assert.True(sourceState == "committed", sourceView.GetProperty("message").GetString());

        var (moveState, moveView) = await SubmitMoveAsync(harness, session, "wire-move");
        Assert.True(moveState == "committed", moveView.GetProperty("message").GetString());

        var (executeState, executeView) = await SubmitExecuteAsync(harness, session, "wire-execute");
        Assert.True(executeState == "committed", executeView.GetProperty("message").GetString());
    }

    private static async Task CommitCreateAsync(
        LiveDocumentBackendHarness harness,
        SessionRecord session,
        string key)
    {
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponent,
            harness.CreatedComponentId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            $"{key}.json",
            new
            {
                bridgeOperation = "canvas.create",
                arguments = new
                {
                    operationId = key,
                    objectId = harness.CreatedComponentId,
                    componentTypeId = LiveDocumentBackendHarness.ScriptComponentTypeId,
                    pivot = new { x = 220, y = 20 },
                    nickName = "Created"
                }
            });
        var changeSet = harness.CreateCustomChangeSet(
            session,
            snapshot.Revision,
            new TypedOperation(
                key,
                OperationKind.CreateComponent,
                AdapterOwner.Canvas,
                [],
                [resource],
                true,
                artifact),
            [new ResourceExpectation(resource, ResourceExpectation.AbsentFingerprint)]);
        var (state, view) = await SubmitAsync(harness, session, changeSet, snapshot.Id, key);
        Assert.True(state == "committed", view.GetProperty("message").GetString());
    }

    private static async Task<(string State, JsonElement View)> SubmitSetSourceAsync(
        LiveDocumentBackendHarness harness,
        SessionRecord session,
        string key,
        string expectedFingerprint)
    {
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponentSource,
            harness.CreatedComponentId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            $"{key}.json",
            new
            {
                bridgeOperation = "python.setSource",
                arguments = new
                {
                    operationId = key,
                    componentId = harness.CreatedComponentId,
                    expectedSourceSha256 = expectedFingerprint,
                    source = "a = 1",
                    runtime = PythonRuntime.Cpython3,
                    expireSolution = false
                }
            });
        var changeSet = harness.CreateCustomChangeSet(
            session,
            snapshot.Revision,
            new TypedOperation(
                key,
                OperationKind.UpdatePythonSource,
                AdapterOwner.Script,
                [],
                [resource],
                true,
                artifact),
            [new ResourceExpectation(resource, expectedFingerprint)]);
        return await SubmitAsync(harness, session, changeSet, snapshot.Id, key);
    }

    private static async Task<(string State, JsonElement View)> SubmitSetSchemaAsync(
        LiveDocumentBackendHarness harness,
        SessionRecord session,
        string key)
    {
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponentIo,
            harness.CreatedComponentId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            $"{key}.json",
            new
            {
                bridgeOperation = "python.setSchema",
                arguments = new
                {
                    operationId = key,
                    componentId = harness.CreatedComponentId,
                    inputs = Array.Empty<PythonParameter>(),
                    outputs = Array.Empty<PythonParameter>(),
                    preserveIncidentWires = true
                }
            });
        var changeSet = harness.CreateCustomChangeSet(
            session,
            snapshot.Revision,
            new TypedOperation(
                key,
                OperationKind.SetComponentIo,
                AdapterOwner.Script,
                [],
                [resource],
                true,
                artifact),
            [new ResourceExpectation(resource, ResourceExpectation.AutoFingerprint)]);
        return await SubmitAsync(harness, session, changeSet, snapshot.Id, key);
    }

    private static async Task<(string State, JsonElement View)> SubmitExecuteAsync(
        LiveDocumentBackendHarness harness,
        SessionRecord session,
        string key)
    {
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponentValue,
            harness.CreatedComponentId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            $"{key}.json",
            new
            {
                bridgeOperation = "python.execute",
                arguments = new
                {
                    operationId = key,
                    componentId = harness.CreatedComponentId,
                    expireUpstream = false,
                    recomputeDocument = true
                }
            });
        var changeSet = harness.CreateCustomChangeSet(
            session,
            snapshot.Revision,
            new TypedOperation(
                key,
                OperationKind.ExecutePython,
                AdapterOwner.Script,
                [],
                [resource],
                true,
                artifact),
            [new ResourceExpectation(resource, ResourceExpectation.AutoFingerprint)]);
        return await SubmitAsync(harness, session, changeSet, snapshot.Id, key);
    }

    private static async Task<(string State, JsonElement View)> SubmitMoveAsync(
        LiveDocumentBackendHarness harness,
        SessionRecord session,
        string key)
    {
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponentLayout,
            harness.CreatedComponentId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            $"{key}.json",
            new
            {
                bridgeOperation = "canvas.move",
                arguments = new
                {
                    operationId = key,
                    pivots = new Dictionary<Guid, object>
                    {
                        [harness.CreatedComponentId] = new { x = 320, y = 40 }
                    },
                    expectedFingerprints = new Dictionary<Guid, string>
                    {
                        [harness.CreatedComponentId] = harness.CreatedComponentFingerprint
                    }
                }
            });
        var changeSet = harness.CreateCustomChangeSet(
            session,
            snapshot.Revision,
            new TypedOperation(
                key,
                OperationKind.MoveComponent,
                AdapterOwner.Canvas,
                [],
                [resource],
                true,
                artifact),
            [new ResourceExpectation(resource, harness.CreatedComponentFingerprint)]);
        return await SubmitAsync(harness, session, changeSet, snapshot.Id, key);
    }

    private static async Task<(string State, JsonElement View)> SubmitAsync(
        LiveDocumentBackendHarness harness,
        SessionRecord session,
        ChangeSet changeSet,
        string snapshotId,
        string idempotencyKey)
    {
        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            JsonSerializer.SerializeToElement(
                new
                {
                    changeSet,
                    expectedSnapshotId = snapshotId,
                    idempotencyKey,
                    summary = "Sub-domain ledger safety"
                },
                BridgeProtocol.JsonOptions),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var view = await harness.ReadJobViewAsync(jobId);
        return (state, view);
    }

    private static JsonElement ToElement(object value) =>
        JsonSerializer.SerializeToElement(value, value.GetType(), BridgeProtocol.JsonOptions);
}
