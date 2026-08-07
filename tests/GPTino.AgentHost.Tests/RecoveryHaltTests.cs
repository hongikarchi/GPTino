using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Data;
using GPTino.BridgeContract;
using GPTino.Contracts;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// Host-enforced session halt on RecoveryRequired: the incident session (and ONLY that session)
/// stops — queued jobs are swept to durable Cancelled, fresh submissions are refused with the
/// remediation path, the post-turn auto-tidy never fires on the wreckage, and interrupted
/// sessions come back halted after a restart. Resume is explicit and job-id-pinned.
/// </summary>
[Collection(LiveDocumentBackendCollection.Name)]
public sealed class RecoveryHaltTests
{
    private const string CrashOperationId = "halt-crash";

    /// <summary>Fails the CrashOperationId write with a non-refusal bridge failure -> RecoveryRequired.</summary>
    private static Func<BridgeOperationRequest, BridgeFailure?> CrashFailureFactory => request =>
        string.Equals(request.OperationId, CrashOperationId, StringComparison.Ordinal)
            ? new BridgeFailure("solver_crash", "Simulated mid-write crash.", Retryable: false)
            : null;

    [Fact]
    public async Task RecoveryRequiredHaltsSessionAndCancelsQueuedJobsDurably()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder(failureFactory: CrashFailureFactory);
        harness.Backend.SetPaused(true);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Halted"));
        var snapshot = await harness.CaptureSnapshotViewAsync();

        var crashChange = await harness.CreateChangeSetAsync(session, CrashOperationId, snapshot.Revision);
        var crash = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(crashChange, snapshot.Id, "halt-crash-key", "Crashing job"),
            CancellationToken.None));
        var queuedChange = await harness.CreateChangeSetAsync(session, "halt-queued", snapshot.Revision);
        var queued = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(queuedChange, snapshot.Id, "halt-queued-key", "Queued behind the crash"),
            CancellationToken.None));
        harness.Backend.SetPaused(false);

        var crashJobId = crash.GetProperty("jobId").GetGuid();
        var queuedJobId = queued.GetProperty("jobId").GetGuid();
        Assert.Equal("recoveryrequired", await harness.WaitForJobStateAsync(crashJobId));
        Assert.Equal("cancelled", await harness.WaitForJobStateAsync(queuedJobId));

        // Observation point 1: the queued job's DURABLE row is Cancelled with the teaching phase.
        var row = await ReadDurableRowAsync(harness, queuedJobId);
        Assert.Equal(JobState.Cancelled, row.State);
        Assert.Equal("halted-by-recovery", row.Phase);
        Assert.Contains("recovery_resume", row.Message, StringComparison.Ordinal);
        Assert.Contains(crashJobId.ToString("D"), row.Message, StringComparison.Ordinal);
        // Observation point 2: the queued job's write NEVER crossed the bridge.
        Assert.DoesNotContain("halt-queued", responder.WriteOperationIds);
        // The session is latched by the crashing job and projected as halted.
        var halt = harness.Backend.TryReadSessionHalt(session.Id);
        Assert.NotNull(halt);
        Assert.Equal(crashJobId, halt!.JobId);
    }

    [Fact]
    public async Task OtherSessionsKeepRunningWhileOneIsHalted()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder(failureFactory: CrashFailureFactory);
        harness.Backend.SetPaused(true);
        var halted = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Halted"));
        var healthy = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Healthy"));
        var snapshot = await harness.CaptureSnapshotViewAsync();

        var crashChange = await harness.CreateChangeSetAsync(halted, CrashOperationId, snapshot.Revision);
        var crash = ToElement(await harness.Backend.SubmitChangeAsync(
            halted,
            Submission(crashChange, snapshot.Id, "cross-crash-key", "Crashing job"),
            CancellationToken.None));
        var healthyChange = await harness.CreateChangeSetAsync(healthy, "cross-healthy", snapshot.Revision);
        var healthyJob = ToElement(await harness.Backend.SubmitChangeAsync(
            healthy,
            Submission(healthyChange, snapshot.Id, "cross-healthy-key", "Healthy job"),
            CancellationToken.None));
        harness.Backend.SetPaused(false);

        Assert.Equal("recoveryrequired", await harness.WaitForJobStateAsync(crash.GetProperty("jobId").GetGuid()));
        // No cross-session freeze: the OTHER session's queued job still executes and commits.
        Assert.Equal("committed", await harness.WaitForJobStateAsync(healthyJob.GetProperty("jobId").GetGuid()));
        Assert.Contains("cross-healthy", responder.WriteOperationIds);
        Assert.Null(harness.Backend.TryReadSessionHalt(healthy.Id));
    }

    [Fact]
    public async Task SubmitWhileHaltedIsRefusedWithTeachingMessageButReplaysStillAnswer()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder(failureFactory: CrashFailureFactory);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Halted"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var crashChange = await harness.CreateChangeSetAsync(session, CrashOperationId, snapshot.Revision);
        var crashSubmission = Submission(crashChange, snapshot.Id, "teach-crash-key", "Crashing job");
        var crash = ToElement(await harness.Backend.SubmitChangeAsync(session, crashSubmission, CancellationToken.None));
        var crashJobId = crash.GetProperty("jobId").GetGuid();
        Assert.Equal("recoveryrequired", await harness.WaitForJobStateAsync(crashJobId));

        // An idempotent replay of the already-accepted key still answers while halted. (Replayed
        // BEFORE the fresh draft below: the harness reuses one draft artifact name, and a replay
        // re-reads the draft to recompute the request hash.)
        var replay = ToElement(await harness.Backend.SubmitChangeAsync(session, crashSubmission, CancellationToken.None));
        Assert.True(replay.GetProperty("duplicate").GetBoolean());
        Assert.Equal(crashJobId, replay.GetProperty("jobId").GetGuid());
        Assert.Equal("recoveryrequired", replay.GetProperty("state").GetString());

        // A fresh submission is refused deterministically with the remediation path.
        var freshChange = await harness.CreateChangeSetAsync(session, "teach-fresh", snapshot.Revision);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Backend.SubmitChangeAsync(
                session,
                Submission(freshChange, snapshot.Id, "teach-fresh-key", "Fresh work"),
                CancellationToken.None));
        Assert.Contains("recovery_resume", exception.Message, StringComparison.Ordinal);
        Assert.Contains(crashJobId.ToString("D"), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResumeIsJobIdPinnedIdempotentAndReopensTheSession()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder(failureFactory: CrashFailureFactory);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Resumable"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var crashChange = await harness.CreateChangeSetAsync(session, CrashOperationId, snapshot.Revision);
        var crash = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(crashChange, snapshot.Id, "resume-crash-key", "Crashing job"),
            CancellationToken.None));
        var crashJobId = crash.GetProperty("jobId").GetGuid();
        Assert.Equal("recoveryrequired", await harness.WaitForJobStateAsync(crashJobId));

        // A wrong jobId does not resume: it returns the CURRENT halt so the model self-corrects.
        var wrong = ToElement(await harness.Backend.ResumeSessionAsync(
            session,
            Args(new { jobId = Guid.NewGuid().ToString("D") }),
            CancellationToken.None));
        Assert.False(wrong.GetProperty("resumed").GetBoolean());
        Assert.Equal(crashJobId, wrong.GetProperty("halt").GetProperty("jobId").GetGuid());
        Assert.NotNull(harness.Backend.TryReadSessionHalt(session.Id));

        // The correct jobId lifts the halt and acknowledges the job durably.
        var resumed = ToElement(await harness.Backend.ResumeSessionAsync(
            session,
            Args(new { jobId = crashJobId.ToString("D") }),
            CancellationToken.None));
        Assert.True(resumed.GetProperty("resumed").GetBoolean());
        Assert.Null(harness.Backend.TryReadSessionHalt(session.Id));
        var row = await ReadDurableRowAsync(harness, crashJobId);
        Assert.Equal(JobState.RecoveryRequired, row.State);
        Assert.Equal("recoveryrequired-acknowledged", row.Phase);

        // Submissions succeed again after the resume.
        var freshSnapshot = await harness.CaptureSnapshotViewAsync();
        var freshChange = await harness.CreateChangeSetAsync(session, "resume-fresh", freshSnapshot.Revision);
        var fresh = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(freshChange, freshSnapshot.Id, "resume-fresh-key", "Post-resume work"),
            CancellationToken.None));
        Assert.Equal("committed", await harness.WaitForJobStateAsync(fresh.GetProperty("jobId").GetGuid()));

        // Idempotent: resuming a session that is not halted also succeeds.
        var again = ToElement(await harness.Backend.ResumeSessionAsync(
            session,
            Args(new { jobId = crashJobId.ToString("D") }),
            CancellationToken.None));
        Assert.True(again.GetProperty("resumed").GetBoolean());
    }

    [Fact]
    public async Task TidyIsSkippedForHaltedSessionsAndAfterFailedOrBlockedTurns()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeNumberSliderValue = true;
        harness.WireFirstTwoObjects = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Tidy gates"));
        var snapshot = await harness.CaptureSnapshotViewAsync();

        // Control: after a COMMITTED job the post-turn tidy runs (the wired cluster arranges).
        var goodChange = await harness.CreateChangeSetAsync(session, "tidy-good", snapshot.Revision);
        var good = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(goodChange, snapshot.Id, "tidy-good-key", "Good job"),
            CancellationToken.None));
        Assert.Equal("committed", await harness.WaitForJobStateAsync(good.GetProperty("jobId").GetGuid()));
        harness.Backend.BeginTurn(session.Id);
        harness.Backend.SeedTurnCreatedComponents(session.Id, harness.CanvasObjectId, harness.SecondCanvasObjectId);
        Assert.Equal(2, await harness.Backend.TidyTurnCreationsAsync(session, CancellationToken.None));

        // A BLOCKED job (stale concrete fingerprint) marks the turn; the tidy must soft-skip.
        var blockedSnapshot = await harness.CaptureSnapshotViewAsync();
        var staleResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentLayout,
            harness.CanvasObjectId.ToString("D"));
        var blockedChange = await harness.CreateChangeSetAsync(
            session,
            "tidy-blocked",
            blockedSnapshot.Revision,
            writeSet: [new ResourceExpectation(staleResource, "stale-fingerprint")],
            payloadExpectedFingerprint: "stale-fingerprint");
        var blocked = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(blockedChange, blockedSnapshot.Id, "tidy-blocked-key", "Blocked job"),
            CancellationToken.None));
        Assert.Equal("blocked", await harness.WaitForJobStateAsync(blocked.GetProperty("jobId").GetGuid()));
        var movesBefore = CountMoves(responder);
        harness.Backend.BeginTurn(session.Id);
        harness.Backend.SeedTurnCreatedComponents(session.Id, harness.CanvasObjectId, harness.SecondCanvasObjectId);
        Assert.Equal(0, await harness.Backend.TidyTurnCreationsAsync(session, CancellationToken.None));
        Assert.Equal(movesBefore, CountMoves(responder));

        // Halt: seeds are discarded at latch time and the tidy is gated while halted...
        var haltJobId = Guid.NewGuid();
        harness.Backend.BeginTurn(session.Id);
        harness.Backend.SeedTurnCreatedComponents(session.Id, harness.CanvasObjectId, harness.SecondCanvasObjectId);
        await harness.Backend.HaltSessionForRecoveryAsync(session.Id, haltJobId, "Simulated halt.");
        Assert.Equal(0, await harness.Backend.TidyTurnCreationsAsync(session, CancellationToken.None));
        // ...and stay discarded even after the resume: the halt cleared them for good.
        Assert.True((await harness.Backend.TryResumeSessionAsync(session.Id, haltJobId)).Resumed);
        Assert.Equal(0, await harness.Backend.TidyTurnCreationsAsync(session, CancellationToken.None));
        Assert.Equal(movesBefore, CountMoves(responder));
    }

    [Fact]
    public async Task LatchSetBetweenDurableInsertAndEnqueueEndsTheJobCancelled()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        // Withhold every snapshot response after the first (the orientation read below), so the
        // submit parks INSIDE SubmitChangeAsync — after its entry halt check, before the durable
        // insert and enqueue — while the latch flips.
        await using var responder = harness.StartResponder(automaticSnapshotResponses: 1);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Raced"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        _ = await responder.WaitForSnapshotRequestAsync(); // drain the answered orientation read
        var changeSet = await harness.CreateChangeSetAsync(session, "race-op", snapshot.Revision);

        var submitTask = harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "race-key", "Raced submit"),
            CancellationToken.None);
        var parked = await responder.WaitForSnapshotRequestAsync();
        // The submit passed its entry halt check and is now waiting on this snapshot: flip the
        // latch, then let the submit proceed into insert + enqueue.
        await harness.Backend.HaltSessionForRecoveryAsync(session.Id, Guid.NewGuid(), "Raced halt.");
        await harness.SendOperationResponseAsync(
            parked.Frame,
            parked.Request,
            harness.CreateSnapshot(),
            parked.Frame.MessageId);

        var submitted = ToElement(await submitTask);
        var jobId = submitted.GetProperty("jobId").GetGuid();
        Assert.Equal("cancelled", await harness.WaitForJobStateAsync(jobId));
        var row = await ReadDurableRowAsync(harness, jobId);
        Assert.Equal(JobState.Cancelled, row.State);
        Assert.Equal("halted-by-recovery", row.Phase);
        Assert.Empty(responder.WriteOperationIds);
    }

    [Fact]
    public async Task RestartRestorePathLatchesInterruptedSessions()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        harness.Backend.SetPaused(true);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Interrupted"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var changeSet = await harness.CreateChangeSetAsync(session, "interrupted-op", snapshot.Revision);
        var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "interrupted-key", "Interrupted work"),
            CancellationToken.None));
        var jobId = submitted.GetProperty("jobId").GetGuid();

        // A second AgentHost over the same durable state: startup recovery converts the
        // interrupted job to RecoveryRequired, and the session comes back HALTED (honest state).
        var recovery = await harness.StartRecoveryReaderAsync();
        try
        {
            var restored = await LiveDocumentBackendHarness.ReadJobViewAsync(recovery, jobId);
            Assert.Equal("recoveryrequired", restored.GetProperty("state").GetString());
            var halt = recovery.TryReadSessionHalt(session.Id);
            Assert.NotNull(halt);
            Assert.Equal(jobId, halt!.JobId);
            // The latch carries the durable row's UpdatedAt (the conversion/incident moment), not
            // the registration wall clock, so the panel shows WHEN the job actually stopped.
            var row = await ReadDurableRowAsync(harness, jobId);
            Assert.Equal(row.UpdatedAt, halt.At);

            var freshChange = await harness.CreateChangeSetAsync(session, "post-restart", snapshot.Revision);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                recovery.SubmitChangeAsync(
                    session,
                    Submission(freshChange, ResourceExpectation.AutoFingerprint, "post-restart-key", "Post-restart work"),
                    CancellationToken.None));
            Assert.Contains("recovery_resume", exception.Message, StringComparison.Ordinal);
            Assert.Contains(jobId.ToString("D"), exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await recovery.StopAsync(CancellationToken.None);
            recovery.Dispose();
        }

        // A THIRD host over the same database: the row is terminal RecoveryRequired now, so this
        // startup converts nothing and must NOT re-latch — a historical incident halts a session
        // once, not on every boot forever.
        var second = await harness.StartRecoveryReaderAsync();
        try
        {
            var restored = await LiveDocumentBackendHarness.ReadJobViewAsync(second, jobId);
            Assert.Equal("recoveryrequired", restored.GetProperty("state").GetString());
            Assert.Null(second.TryReadSessionHalt(session.Id));
        }
        finally
        {
            await second.StopAsync(CancellationToken.None);
            second.Dispose();
        }
    }

    /// <summary>
    /// FINDING 1: an acknowledged (resumed) RecoveryRequired row must not re-halt its session on
    /// the next restart — the restore path latches only jobs the startup recovery itself
    /// converted, and the durable acknowledgment survives the restart untouched.
    /// </summary>
    [Fact]
    public async Task AcknowledgedHaltDoesNotRelatchOnRestart()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder(failureFactory: CrashFailureFactory);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Resumed then restarted"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var crashChange = await harness.CreateChangeSetAsync(session, CrashOperationId, snapshot.Revision);
        var crash = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(crashChange, snapshot.Id, "ack-restart-key", "Crashing job"),
            CancellationToken.None));
        var crashJobId = crash.GetProperty("jobId").GetGuid();
        Assert.Equal("recoveryrequired", await harness.WaitForJobStateAsync(crashJobId));
        Assert.NotNull(harness.Backend.TryReadSessionHalt(session.Id));
        var resumed = ToElement(await harness.Backend.ResumeSessionAsync(
            session,
            Args(new { jobId = crashJobId.ToString("D") }),
            CancellationToken.None));
        Assert.True(resumed.GetProperty("resumed").GetBoolean());

        var recovery = await harness.StartRecoveryReaderAsync();
        try
        {
            // The session boots un-halted, and the restored row still carries the acknowledgment.
            Assert.Null(recovery.TryReadSessionHalt(session.Id));
            var restored = await LiveDocumentBackendHarness.ReadJobViewAsync(recovery, crashJobId);
            Assert.Equal("recoveryrequired", restored.GetProperty("state").GetString());
            var row = await ReadDurableRowAsync(harness, crashJobId);
            Assert.Equal(JobState.RecoveryRequired, row.State);
            Assert.Equal("recoveryrequired-acknowledged", row.Phase);
        }
        finally
        {
            await recovery.StopAsync(CancellationToken.None);
            recovery.Dispose();
        }
    }

    /// <summary>
    /// FINDING 2/4: two halt paths racing over the SAME queued jobs (marker + broker TryCancel
    /// from both) still produce exactly one durable teaching record per job — the completion
    /// observer is the single marker consumer and single writer, and it records the terminal
    /// phase BEFORE resolving the entry, so watchers never observe a stale Queued projection.
    /// </summary>
    [Fact]
    public async Task ConcurrentHaltSweepsWriteOneDeterministicTeachingRecordPerJob()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        harness.Backend.SetPaused(true);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Raced sweeps"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var haltJobId = Guid.NewGuid();
        var jobIds = new List<Guid>();
        for (var index = 0; index < 4; index++)
        {
            var change = await harness.CreateChangeSetAsync(session, $"sweep-race-{index}", snapshot.Revision);
            var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
                session,
                Submission(change, snapshot.Id, $"sweep-race-key-{index}", $"Queued job {index}"),
                CancellationToken.None));
            jobIds.Add(submitted.GetProperty("jobId").GetGuid());
        }

        // Both sweeps enumerate, mark, and TryCancel the same queued jobs concurrently — the
        // exact concurrent-mark variant: for every job one caller wins the broker cancel and one
        // loses, in arbitrary order.
        await Task.WhenAll(
            Task.Run(() => harness.Backend.CancelQueuedSessionJobs(session.Id, haltJobId)),
            Task.Run(() => harness.Backend.CancelQueuedSessionJobs(session.Id, haltJobId)));

        foreach (var jobId in jobIds)
        {
            Assert.Equal("cancelled", await harness.WaitForJobStateAsync(jobId));
            var view = await harness.ReadJobViewAsync(jobId);
            Assert.Contains(haltJobId.ToString("D"), view.GetProperty("message").GetString(), StringComparison.Ordinal);
            var row = await ReadDurableRowAsync(harness, jobId);
            Assert.Equal(JobState.Cancelled, row.State);
            Assert.Equal("halted-by-recovery", row.Phase);
            Assert.Contains("recovery_resume", row.Message, StringComparison.Ordinal);
        }
        Assert.Empty(responder.WriteOperationIds);
    }

    /// <summary>
    /// FINDING 2 (sweep vs enqueue re-check): the latch flips CONCURRENTLY with a submit's
    /// insert+enqueue, so depending on the interleaving the job is cancelled by the latch sweep,
    /// by the submit's re-check, or by both racing over the same queued job. Every interleaving
    /// must end with the same single durable teaching record.
    /// </summary>
    [Fact]
    public async Task LatchRacingTheEnqueueStillEndsHaltedByRecoveryDeterministically()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder(automaticSnapshotResponses: 1);
        // Paused broker: whatever the interleaving, the raced job can never dispatch, so the only
        // way it terminates is through the halt cancellation.
        harness.Backend.SetPaused(true);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Raced latch"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        _ = await responder.WaitForSnapshotRequestAsync(); // drain the answered orientation read
        var changeSet = await harness.CreateChangeSetAsync(session, "latch-race-op", snapshot.Revision);

        var submitTask = harness.Backend.SubmitChangeAsync(
            session,
            Submission(changeSet, snapshot.Id, "latch-race-key", "Raced submit"),
            CancellationToken.None);
        var parked = await responder.WaitForSnapshotRequestAsync();
        // Release the parked submit and flip the latch CONCURRENTLY: the halt sweep and the
        // submit's post-enqueue re-check now race over the same freshly inserted job.
        var haltJobId = Guid.NewGuid();
        var haltTask = Task.Run(() =>
            harness.Backend.HaltSessionForRecoveryAsync(session.Id, haltJobId, "Raced halt."));
        await harness.SendOperationResponseAsync(
            parked.Frame,
            parked.Request,
            harness.CreateSnapshot(),
            parked.Frame.MessageId);
        await haltTask;

        var submitted = ToElement(await submitTask);
        var jobId = submitted.GetProperty("jobId").GetGuid();
        Assert.Equal("cancelled", await harness.WaitForJobStateAsync(jobId));
        var row = await ReadDurableRowAsync(harness, jobId);
        Assert.Equal(JobState.Cancelled, row.State);
        Assert.Equal("halted-by-recovery", row.Phase);
        Assert.Contains(haltJobId.ToString("D"), row.Message, StringComparison.Ordinal);
        Assert.Empty(responder.WriteOperationIds);
    }

    /// <summary>
    /// FINDING 3: a graceful stop with queued jobs records each as RecoveryRequired (the honest
    /// interruption) WITHOUT firing the halt sweep — no sibling gets cross-written to
    /// Cancelled/"halted-by-recovery" by another job's shutdown observer, so the final rows are
    /// deterministic.
    /// </summary>
    [Fact]
    public async Task GracefulStopRecordsInterruptionWithoutCrossCancellingSiblings()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder();
        harness.Backend.SetPaused(true);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Stopped"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var jobIds = new List<Guid>();
        for (var index = 0; index < 2; index++)
        {
            var change = await harness.CreateChangeSetAsync(session, $"stop-op-{index}", snapshot.Revision);
            var submitted = ToElement(await harness.Backend.SubmitChangeAsync(
                session,
                Submission(change, snapshot.Id, $"stop-key-{index}", $"Queued at stop {index}"),
                CancellationToken.None));
            jobIds.Add(submitted.GetProperty("jobId").GetGuid());
        }

        await harness.Backend.StopAsync(CancellationToken.None);

        // Shutdown never latches (the next startup decides from the durable rows), and every
        // sibling got its OWN observer's RecoveryRequired — not a cross-written halt cancel.
        Assert.Null(harness.Backend.TryReadSessionHalt(session.Id));
        foreach (var jobId in jobIds)
        {
            var row = await ReadDurableRowAsync(harness, jobId);
            Assert.Equal(JobState.RecoveryRequired, row.State);
            Assert.Equal("recoveryrequired", row.Phase);
            Assert.Contains("AgentHost stopped", row.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// FINDING 7: the completion observer's late terminal re-assert (same state+message) after a
    /// resume must neither re-latch the halt nor revert the durable
    /// "recoveryrequired-acknowledged" phase the resume recorded.
    /// </summary>
    [Fact]
    public async Task ObserverReassertAfterResumeKeepsAcknowledgmentAndDoesNotRelatch()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder(failureFactory: CrashFailureFactory);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Reasserted"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var crashChange = await harness.CreateChangeSetAsync(session, CrashOperationId, snapshot.Revision);
        var crash = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(crashChange, snapshot.Id, "reassert-key", "Crashing job"),
            CancellationToken.None));
        var crashJobId = crash.GetProperty("jobId").GetGuid();
        Assert.Equal("recoveryrequired", await harness.WaitForJobStateAsync(crashJobId));
        var resumed = ToElement(await harness.Backend.ResumeSessionAsync(
            session,
            Args(new { jobId = crashJobId.ToString("D") }),
            CancellationToken.None));
        Assert.True(resumed.GetProperty("resumed").GetBoolean());

        // Replay the observer's re-assert as if it lost the race against the resume above.
        await harness.Backend.SimulateCompletionReassertForTestAsync(crashJobId);

        Assert.Null(harness.Backend.TryReadSessionHalt(session.Id));
        var row = await ReadDurableRowAsync(harness, crashJobId);
        Assert.Equal(JobState.RecoveryRequired, row.State);
        Assert.Equal("recoveryrequired-acknowledged", row.Phase);
    }

    /// <summary>
    /// FINDING 8b: soft-deleting (or purging) a halted session drops its latch — otherwise the
    /// hidden session could never be resumed again. After the forget, the session runs normally.
    /// </summary>
    [Fact]
    public async Task ForgettingASessionClearsItsHaltLatchAndUnblocksIt()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        await using var responder = harness.StartResponder(failureFactory: CrashFailureFactory);
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Deleted while halted"));
        var snapshot = await harness.CaptureSnapshotViewAsync();
        var crashChange = await harness.CreateChangeSetAsync(session, CrashOperationId, snapshot.Revision);
        var crash = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(crashChange, snapshot.Id, "forget-crash-key", "Crashing job"),
            CancellationToken.None));
        Assert.Equal("recoveryrequired", await harness.WaitForJobStateAsync(crash.GetProperty("jobId").GetGuid()));
        Assert.NotNull(harness.Backend.TryReadSessionHalt(session.Id));

        harness.Backend.ForgetSessionRuntimeState(session.Id);

        Assert.Null(harness.Backend.TryReadSessionHalt(session.Id));
        // The un-latched session accepts and commits fresh work again (e.g. after a restore).
        var freshSnapshot = await harness.CaptureSnapshotViewAsync();
        var freshChange = await harness.CreateChangeSetAsync(session, "forget-fresh", freshSnapshot.Revision);
        var fresh = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(freshChange, freshSnapshot.Id, "forget-fresh-key", "Post-forget work"),
            CancellationToken.None));
        Assert.Equal("committed", await harness.WaitForJobStateAsync(fresh.GetProperty("jobId").GetGuid()));
    }

    /// <summary>
    /// FINDING 6: a PREVIOUS turn's auto-tidy/arrange job ending Blocked must not soft-skip the
    /// tidy of a later, fully committed turn — arrange jobs are excluded from the per-session
    /// last-terminal tracker.
    /// </summary>
    [Fact]
    public async Task BlockedAutoTidyJobDoesNotSuppressTheNextTurnsTidy()
    {
        await using var harness = await LiveDocumentBackendHarness.CreateAsync();
        harness.IncludeNumberSliderValue = true;
        harness.WireFirstTwoObjects = true;
        await using var responder = harness.StartResponder();
        var session = await harness.Store.CreateSessionAsync(new CreateSessionRequest("Tidy after arrange"));
        var snapshot = await harness.CaptureSnapshotViewAsync();

        // The turn's REAL work commits...
        var goodChange = await harness.CreateChangeSetAsync(session, "arrange-good", snapshot.Revision);
        var good = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(goodChange, snapshot.Id, "arrange-good-key", "Good job"),
            CancellationToken.None));
        Assert.Equal("committed", await harness.WaitForJobStateAsync(good.GetProperty("jobId").GetGuid()));

        // ...then an ARRANGE-tagged job (the async auto-tidy shape: "arrange-" key + "Auto-tidy
        // layout" summary) ends Blocked on a stale fingerprint.
        var blockedSnapshot = await harness.CaptureSnapshotViewAsync();
        var staleResource = new ResourceAddress(
            ResourceKind.GrasshopperComponentLayout,
            harness.CanvasObjectId.ToString("D"));
        var blockedArrange = await harness.CreateChangeSetAsync(
            session,
            "arrange-blocked",
            blockedSnapshot.Revision,
            writeSet: [new ResourceExpectation(staleResource, "stale-fingerprint")],
            payloadExpectedFingerprint: "stale-fingerprint");
        var blocked = ToElement(await harness.Backend.SubmitChangeAsync(
            session,
            Submission(
                blockedArrange,
                blockedSnapshot.Id,
                FormattableString.Invariant($"arrange-{Guid.NewGuid():N}"),
                "Auto-tidy layout (1 components)"),
            CancellationToken.None));
        Assert.Equal("blocked", await harness.WaitForJobStateAsync(blocked.GetProperty("jobId").GetGuid()));

        // The next turn still tidies: the blocked ARRANGE terminal did not poison the tracker.
        harness.Backend.BeginTurn(session.Id);
        harness.Backend.SeedTurnCreatedComponents(session.Id, harness.CanvasObjectId, harness.SecondCanvasObjectId);
        Assert.Equal(2, await harness.Backend.TidyTurnCreationsAsync(session, CancellationToken.None));
    }

    private static int CountMoves(FakeBridgeResponder responder) =>
        responder.Requests.Count(request =>
            string.Equals(request.Operation, "canvas.move", StringComparison.Ordinal));

    private static async Task<DurableJobRecord> ReadDurableRowAsync(
        LiveDocumentBackendHarness harness,
        Guid jobId)
    {
        // A separate read-side store over the same database file: both asserted rows are terminal,
        // so the startup-recovery sweep inside RecoverInterruptedAsync leaves them untouched.
        var store = new DurableJobStore(Path.Combine(harness.Options.ResolveDataDirectory(), "live-jobs.db"));
        await store.InitializeAsync();
        var rows = (await store.RecoverInterruptedAsync()).Records;
        return Assert.Single(rows, row => row.JobId == jobId);
    }

    private static JsonElement Args(object value) =>
        JsonSerializer.SerializeToElement(value, BridgeProtocol.JsonOptions);

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
