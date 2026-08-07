using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Data;
using GPTino.AgentHost.Hosting;
using GPTino.AgentHost.Runtime;
using GPTino.BridgeContract;
using GPTino.Contracts;
using GPTino.ScriptAdapter;
using Microsoft.Data.Sqlite;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// W2 restart persistence for the per-runtime resource ledger. The dominant pre-W2 failure: a
/// session creates a component, the AgentHost restarts, and its next gptino:auto submission is
/// Blocked with "this session has not written it" — a pure resubmit round trip. With the durable
/// ledger hydrated on first consult, the same-session/unchanged case resolves; BOTH refusal arms
/// of the unchanged safety predicate (drifted live fingerprint, foreign session) must keep
/// refusing after the restart. Hydration is strictly per document (doc-scoped keys), TryAdd-only
/// (a runtime-written entry always beats a restored row), soft-delete keeps baselines for
/// restore, and rows of a session that no longer exists are swept at startup.
/// </summary>
[Collection(LiveDocumentBackendCollection.Name)]
public sealed class ResourceLedgerPersistenceTests
{
    private static readonly BridgeAdapterOwner[] Adapters =
    [
        BridgeAdapterOwner.Canvas,
        BridgeAdapterOwner.Script
    ];

    [Fact]
    public async Task AutoResolvesAfterRestartForResourceThisSessionAuthored()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters: Adapters);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Author"));
        await CommitCreatedComponentAsync(harness, session);

        // Restart the AgentHost over the same data root: the in-memory ledger is gone by
        // construction, so only durable hydration can prove the session's authorship.
        await harness.RestartBackendAsync(Adapters);
        await using var responder = StartScriptResponder(harness);

        var (state, view) = await SubmitAutoSetSourceAsync(harness, session, "auto-after-restart");

        Assert.True(state == "committed", view.GetProperty("message").GetString());
    }

    [Fact]
    public async Task AutoStillDeclinesAfterRestartWhenTheLiveFingerprintDrifted()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters: Adapters);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Author"));
        await CommitCreatedComponentAsync(harness, session);

        await harness.RestartBackendAsync(Adapters);
        // A manual script edit while the app was off: source text, schema and runtime messages
        // all feed the ONE Python-state fingerprint, so the live source-domain fingerprint no
        // longer matches the baseline this session recorded at creation. Hydration restores
        // knowledge only — the fingerprint-equality half of the safety predicate must still
        // refuse. (A structure-only manual edit — rename/rewire — deliberately no longer blocks
        // a source write: source safety rests on the DIRECT source row, whose domain fingerprint
        // covers the source text itself, not on the parent structure hash that never did.)
        await using var responder = StartScriptResponder(
            harness,
            liveSourceFingerprint: "created-source-f0-manual-edit");

        var (state, view) = await SubmitAutoSetSourceAsync(harness, session, "auto-after-drift");

        Assert.Equal("blocked", state);
        Assert.Contains(
            "gptino:auto declined",
            view.GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoResolvesAfterRestartForASourceThisSessionWrote()
    {
        // The sub-domain restart variant: create + setSource (the commit-time ledger refresh
        // advances the direct Source/Io/Value rows to the post-write Python-state fingerprint),
        // restart, then a second auto setSource. Only the durable sub-domain rows can prove
        // authorship now — and they must hydrate at the ADVANCED fingerprint, not the create-time
        // baseline, or the auto declines as drifted.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters: Adapters);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Author"));
        await CommitCreatedComponentAsync(harness, session);

        var liveSource = "created-source-f0";
        await using (var writeResponder = harness.StartResponder(responseFactory: request =>
        {
            switch (request.Operation)
            {
                case "python.inspect":
                    return BridgeOperationResponse.Create(
                        request.OperationId,
                        changed: false,
                        new { componentId = request.Arguments.GetProperty("componentId").GetGuid() },
                        afterFingerprint: liveSource);
                case "python.setSource":
                    liveSource = "created-source-f1";
                    return BridgeOperationResponse.Create(
                        request.OperationId,
                        changed: true,
                        new { applied = true },
                        beforeFingerprint: request.ExpectedFingerprint,
                        afterFingerprint: liveSource);
                default:
                    return null;
            }
        }))
        {
            var (firstState, firstView) = await SubmitAutoSetSourceAsync(
                harness, session, "auto-source-before-restart");
            Assert.True(firstState == "committed", firstView.GetProperty("message").GetString());
        }

        await harness.RestartBackendAsync(Adapters);
        await using var responder = StartScriptResponder(
            harness,
            liveSourceFingerprint: "created-source-f1");

        var (state, view) = await SubmitAutoSetSourceAsync(harness, session, "auto-source-after-restart");

        Assert.True(state == "committed", view.GetProperty("message").GetString());
    }

    [Fact]
    public async Task AutoStillDeclinesAfterRestartForADifferentSession()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters: Adapters);
        var author = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Author"));
        await CommitCreatedComponentAsync(harness, author);

        await harness.RestartBackendAsync(Adapters);
        await using var responder = StartScriptResponder(harness);
        var stranger = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Stranger"));

        var (state, view) = await SubmitAutoSetSourceAsync(harness, stranger, "auto-foreign-session");

        Assert.Equal("blocked", state);
        Assert.Contains(
            "gptino:auto declined",
            view.GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoResolvesAfterRestartViaTheDirectLedgerKeyMatch()
    {
        // The setSource tests above resolve the SOURCE auto through its DIRECT hydrated sub-domain
        // row (seeded at creation from python.inspect — the parent-ownership fallback no longer
        // covers source/value). This variant additionally declares an auto READ expectation on the
        // created GrasshopperComponent itself, whose hydrated ledger row is a DIRECT key match
        // ("{docKey}|GrasshopperComponent:{id}:*") on the primary lookup path: if hydration
        // restored either row under a wrong key, this Blocks.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters: Adapters);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Author"));
        await CommitCreatedComponentAsync(harness, session);

        await harness.RestartBackendAsync(Adapters);
        await using var responder = StartScriptResponder(harness);

        var snapshot = await harness.CaptureSnapshotViewAsync();
        var componentResource = new ResourceAddress(
            ResourceKind.GrasshopperComponent,
            harness.CreatedComponentId.ToString("D"));
        var sourceResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentSource,
            harness.CreatedComponentId.ToString("D"));
        const string key = "auto-direct-match";
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
                    expectedSourceSha256 = ResourceExpectation.AutoFingerprint,
                    source = "a = 2",
                    runtime = PythonRuntime.Cpython3,
                    expireSolution = false
                }
            });
        var changeSet = new ChangeSet(
            Guid.NewGuid(),
            harness.Target.ProjectId,
            session.Id,
            snapshot.Revision,
            null,
            Array.Empty<Guid>(),
            [new ResourceExpectation(componentResource, ResourceExpectation.AutoFingerprint)],
            [new ResourceExpectation(sourceResource, ResourceExpectation.AutoFingerprint)],
            [
                new TypedOperation(
                    key,
                    OperationKind.UpdatePythonSource,
                    AdapterOwner.Script,
                    [componentResource],
                    [sourceResource],
                    true,
                    artifact)
            ],
            [new VerificationPredicate("No runtime errors", PredicateKind.RuntimeErrorAbsent, null, null)],
            Array.Empty<RollbackBeforeImage>(),
            DateTimeOffset.UtcNow);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, key, "Update Python source with a component read"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var view = await harness.ReadJobViewAsync(jobId);

        Assert.True(state == "committed", view.GetProperty("message").GetString());
    }

    [Fact]
    public async Task SelfStaleConcreteRebaseStillWorksAfterRestart()
    {
        // A value/geometry write carries its concrete fingerprint in the payload; when the live
        // state is this session's own last write (per the hydrated ledger), a stale base must
        // rebase to live instead of Blocking — including right after a restart, where only the
        // durable ledger can prove the self-attribution.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters: Adapters);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Author"));
        await CommitCreatedComponentAsync(harness, session);

        await harness.RestartBackendAsync(Adapters);
        await using var responder = harness.StartResponder();

        var (state, view) = await SubmitStaleMoveAsync(
            harness, session, "stale-move-after-restart", staleFingerprint: "created-v0");

        Assert.True(state == "committed", view.GetProperty("message").GetString());
    }

    [Fact]
    public async Task RuntimeWrittenEntryBeatsALaterHydration()
    {
        // TryAdd claim: an entry the CURRENT runtime recorded must never be clobbered by a
        // (necessarily staler) restored row. The only way hydration can run AFTER a runtime write
        // for the same document is the transient-failure retry: the first hydration attempt hits a
        // SqliteException (rolling the hydrated mark back), the session commits cold (the durable
        // mirror write fails too, so the store still holds the OLD fingerprint), and the next job's
        // successful hydration then TryAdds the stale row — which must lose.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters: Adapters);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Author"));
        await CommitCreatedComponentAsync(harness, session);

        await harness.RestartBackendAsync(Adapters);
        var databasePath = Path.Combine(
            harness.Options.ResolveDataDirectory(), "resource-ledger.db");
        // Preserve the good store (still carrying "created-v1"), then corrupt the live file so the
        // next hydration attempt AND the commit's durable mirror write both fail transiently.
        var staleStoreBytes = ReadLedgerDatabaseBytes(databasePath);
        SwapLedgerDatabaseBytes(databasePath, CorruptDatabaseBytes());

        await using (var moveResponder = harness.StartResponder(responseFactory: request =>
        {
            if (string.Equals(request.Operation, "canvas.move", StringComparison.Ordinal))
            {
                // The write advances the component fingerprint — memory-ledger truth is created-v2.
                harness.CreatedComponentFingerprint = "created-v2";
            }
            return null;
        }))
        {
            var (moveState, moveView) = await SubmitStaleMoveAsync(
                harness, session, "move-under-broken-store", staleFingerprint: "created-v1");
            Assert.True(moveState == "committed", moveView.GetProperty("message").GetString());
        }

        // Restore the STALE durable rows ("created-v1", same session): the next job's hydration
        // retry succeeds and must NOT overwrite the runtime's "created-v2" entries.
        SwapLedgerDatabaseBytes(databasePath, staleStoreBytes);
        await using var responder = StartScriptResponder(harness);

        var (state, view) = await SubmitAutoSetSourceAsync(harness, session, "auto-after-retry");

        // Live is created-v2 == the runtime ledger entry -> auto resolves. If the hydrated stale
        // row had clobbered the runtime entry, the ledger (v1) would mismatch live (v2) -> Blocked.
        Assert.True(state == "committed", view.GetProperty("message").GetString());
    }

    [Fact]
    public async Task AutoIsRefusedInASecondDocumentWithTheSameResourceIds()
    {
        // FINDING 1 regression: a file-copied .gh keeps its component InstanceGuids, so the SAME
        // resource ids (and fingerprints) exist in two registered documents at once. The session
        // only ever committed in doc A; its auto submission targeting doc B must be REFUSED — with
        // a flat (docKey-less) in-memory ledger this resolved and committed, i.e. the session
        // auto-filled a resource it never wrote in that document.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters: Adapters);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Author"));
        await CommitCreatedComponentAsync(harness, session);

        var sibling = harness.CreateSiblingTarget();
        var registration = await harness.RegisterAsync(sibling, Adapters);
        Assert.Equal(BridgeMessageKind.Response, registration.Kind);
        await using var responder = StartScriptResponder(harness);

        var siblingDocKey = AgentHostOptions.ComputeDocumentKey(sibling.GrasshopperPath);
        var boundToSibling = session with { GrasshopperDoc = siblingDocKey };
        var (state, view) = await SubmitAutoSetSourceAsync(
            harness, boundToSibling, "auto-in-doc-b", autoSnapshot: true);

        Assert.Equal("blocked", state);
        Assert.Contains(
            "gptino:auto declined",
            view.GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RestoreAfterSoftDeleteKeepsBaselinesWorkingAcrossRestart()
    {
        // Soft-delete clears ONLY the W1 runtime latches; the ledger (memory and durable) stays,
        // because POST /sessions/{id}/restore exists. The startup orphan sweep runs while the
        // session is still soft-deleted and must count it as known. After restore, gptino:auto
        // resolves exactly as if the delete never happened.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters: Adapters);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Author"));
        await CommitCreatedComponentAsync(harness, session);

        // The soft-delete endpoint pair: hide the session, drop the runtime latches only.
        await harness.Store.SetSessionDeletedAsync(session.Id, deleted: true);
        harness.Backend.ForgetSessionRuntimeState(session.Id);
        await harness.RestartBackendAsync(Adapters); // orphan sweep runs here, session soft-deleted
        await harness.Store.SetSessionDeletedAsync(session.Id, deleted: false);
        await using var responder = StartScriptResponder(harness);

        var (state, view) = await SubmitAutoSetSourceAsync(harness, session, "auto-after-restore");

        Assert.True(state == "committed", view.GetProperty("message").GetString());
    }

    [Fact]
    public async Task OrphanedRowsOfAPurgedSessionAreSweptAtStartup()
    {
        // The purge race: the durable RemoveSessionAsync is fire-and-forget, so a lost race (here
        // simulated by purging the session store WITHOUT the backend forget) strands ledger rows
        // for a session id that no longer exists. The next startup's orphan sweep reclaims them.
        await using var harness = await LiveDocumentBackendHarness.CreateAsync(
            availableAdapters: Adapters);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Author"));
        await CommitCreatedComponentAsync(harness, session);

        var docKey = AgentHostOptions.ComputeDocumentKey(harness.Options.GrasshopperPath);
        var databasePath = Path.Combine(harness.Options.ResolveDataDirectory(), "resource-ledger.db");
        Assert.NotEmpty(await new ResourceLedgerStore(databasePath).ReadDocumentAsync(docKey));

        await harness.Store.PurgeSessionAsync(session.Id);
        await harness.RestartBackendAsync(Adapters);

        Assert.Empty(await new ResourceLedgerStore(databasePath).ReadDocumentAsync(docKey));
    }

    /// <summary>
    /// Job 1: the session authors a SCRIPT component via canvas.create. The responder flips the
    /// harness canvas to include the created object the moment the write lands, so the commit's
    /// after-snapshot records the new component (and its fingerprint) into the resource ledger
    /// under this session. Because the component reports the CPython3 script type, commit-time
    /// ledger recording also reads its Python-state fingerprint (the python.inspect served here)
    /// and seeds the DIRECT Source/Io/Value baseline rows — the rows every later source auto
    /// requires (the parent-ownership fallback no longer covers source/value sub-domains).
    /// </summary>
    private static async Task CommitCreatedComponentAsync(
        LiveDocumentBackendHarness harness,
        SessionRecord session)
    {
        await using var responder = harness.StartResponder(responseFactory: request =>
        {
            if (string.Equals(request.Operation, "canvas.create", StringComparison.Ordinal))
            {
                harness.IncludeCreatedComponent = true;
            }
            if (string.Equals(request.Operation, "python.inspect", StringComparison.Ordinal))
            {
                return BridgeOperationResponse.Create(
                    request.OperationId,
                    changed: false,
                    new { componentId = request.Arguments.GetProperty("componentId").GetGuid() },
                    afterFingerprint: "created-source-f0");
            }
            return null;
        });
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var resource = new ResourceAddress(
            ResourceKind.GrasshopperComponent,
            harness.CreatedComponentId.ToString("D"));
        var artifact = await harness.WritePayloadAsync(
            session,
            "ledger-create.json",
            new
            {
                bridgeOperation = "canvas.create",
                arguments = new
                {
                    operationId = "ledger-create",
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
                "ledger-create",
                OperationKind.CreateComponent,
                AdapterOwner.Canvas,
                [],
                [resource],
                true,
                artifact),
            [new ResourceExpectation(resource, ResourceExpectation.AbsentFingerprint)]);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "ledger-create-key", "Create component"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var view = await harness.ReadJobViewAsync(jobId);
        Assert.True(state == "committed", view.GetProperty("message").GetString());
    }

    /// <summary>
    /// The post-restart responder: python.inspect serves the live source fingerprint (snapshot
    /// enrichment AND commit-time ledger recording) and python.setSource echoes the resolved
    /// expectation as its before-fingerprint, exactly like the real Script adapter's chain.
    /// <paramref name="liveSourceFingerprint"/> overrides what the live document reports — a
    /// value other than the recorded baseline simulates a manual script edit.
    /// </summary>
    private static FakeBridgeResponder StartScriptResponder(
        LiveDocumentBackendHarness harness,
        string liveSourceFingerprint = "created-source-f0") =>
        harness.StartResponder(responseFactory: request => request.Operation switch
        {
            "python.inspect" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: false,
                new { componentId = request.Arguments.GetProperty("componentId").GetGuid() },
                afterFingerprint: liveSourceFingerprint),
            "python.setSource" => BridgeOperationResponse.Create(
                request.OperationId,
                changed: true,
                new { applied = true },
                beforeFingerprint: request.ExpectedFingerprint,
                afterFingerprint: "created-source-f1"),
            _ => null
        });

    /// <summary>
    /// Job 2: a python.setSource on the authored component whose writeSet expectation is
    /// gptino:auto — the exact shape the house rules teach for chained script work. With
    /// <paramref name="autoSnapshot"/> the submission anchors via the gptino:auto snapshot
    /// sentinel instead of a captured id (needed when two Grasshopper documents are registered
    /// and an unbound snapshot read would be ambiguous).
    /// </summary>
    private static async Task<(string State, JsonElement View)> SubmitAutoSetSourceAsync(
        LiveDocumentBackendHarness harness,
        SessionRecord session,
        string key,
        bool autoSnapshot = false)
    {
        var snapshot = autoSnapshot
            ? (Id: ResourceExpectation.AutoFingerprint, Revision: ResourceExpectation.AutoBaseRevision)
            : await harness.CaptureSnapshotViewAsync();
        var sourceResource = new ResourceAddress(
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
                    expectedSourceSha256 = ResourceExpectation.AutoFingerprint,
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
                [sourceResource],
                true,
                artifact),
            [new ResourceExpectation(sourceResource, ResourceExpectation.AutoFingerprint)]);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, key, "Update Python source"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var view = await harness.ReadJobViewAsync(jobId);
        return (state, view);
    }

    /// <summary>
    /// A canvas.move of the created component whose writeSet expectation AND payload fingerprint
    /// carry <paramref name="staleFingerprint"/> — the concrete-fingerprint shape gptino:auto
    /// cannot fill, exercised by ResolveSelfStaleConcreteRebase when the value differs from live.
    /// </summary>
    private static async Task<(string State, JsonElement View)> SubmitStaleMoveAsync(
        LiveDocumentBackendHarness harness,
        SessionRecord session,
        string key,
        string staleFingerprint)
    {
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var layoutResource = new ResourceAddress(
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
                        [harness.CreatedComponentId] = staleFingerprint
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
                [layoutResource],
                true,
                artifact),
            [new ResourceExpectation(layoutResource, staleFingerprint)]);

        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, key, "Move the created component"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();
        var state = await harness.WaitForJobStateAsync(jobId);
        var view = await harness.ReadJobViewAsync(jobId);
        return (state, view);
    }

    private static byte[] ReadLedgerDatabaseBytes(string databasePath)
    {
        // Pooled connections keep file handles; release them before touching the bytes.
        SqliteConnection.ClearAllPools();
        return File.ReadAllBytes(databasePath);
    }

    private static void SwapLedgerDatabaseBytes(string databasePath, byte[] content)
    {
        SqliteConnection.ClearAllPools();
        File.WriteAllBytes(databasePath, content);
        // A mismatched WAL/SHM pair would fight the swapped main file.
        File.Delete(databasePath + "-wal");
        File.Delete(databasePath + "-shm");
    }

    private static byte[] CorruptDatabaseBytes()
    {
        // A page of non-SQLite bytes: opening it raises SqliteException ("file is not a
        // database"), the transient-failure class hydration retries on.
        var bytes = new byte[4096];
        Array.Fill(bytes, (byte)0xAB);
        return bytes;
    }

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
