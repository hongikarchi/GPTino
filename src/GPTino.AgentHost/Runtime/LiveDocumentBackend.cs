using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Codex;
using GPTino.AgentHost.Data;
using GPTino.AgentHost.Hosting;
using GPTino.AgentHost.Security;
using GPTino.BridgeContract;
using GPTino.Contracts;
using GPTino.CordycepsAdapter;
using GPTino.Core;
using GPTino.History;
using GPTino.WireifyAdapter;

namespace GPTino.AgentHost.Runtime;

public interface ILiveDocumentQueueControl
{
    Task RefreshScheduleAsync(CancellationToken cancellationToken = default);

    void SetPaused(bool paused);

    IReadOnlyList<LiveQueueItem> ReadQueue();

    IReadOnlyList<LiveConflictItem> ReadConflicts();

    IReadOnlyList<LiveProblemItem> ReadRecentProblems(int limit = 20);
}

public sealed record LiveQueueItem(
    Guid JobId,
    Guid SessionId,
    string Summary,
    JobState State,
    long EnqueueSequence,
    DateTimeOffset EnqueuedAt,
    string? Target,
    string? TargetDoc = null);

/// <summary>
/// One registered Grasshopper document as the panel projector sees it: the durable docKey
/// (id) plus the current file path, in registration order (first = the default target).
/// </summary>
public sealed record RegisteredGrasshopperDocument(string Id, string File);

public sealed record LiveConflictItem(
    Guid JobId,
    Guid OtherJobId,
    ConflictKind Kind,
    ResourceAddress? Resource,
    string Message);

public sealed record LiveProblemItem(
    Guid JobId,
    Guid SessionId,
    string Summary,
    JobState State,
    string? Message,
    DateTimeOffset UpdatedAt,
    ResourceAddress? Resource = null,
    ConflictKind? ConflictKind = null);

/// <summary>
/// Owns the authenticated Rhino named-pipe connection and the only live-document writer.
/// Model turns may run concurrently, but every submitted ChangeSet crosses this broker.
/// </summary>
public sealed class LiveDocumentBackend : BackgroundService, ILiveDocumentBackend,
    ILiveDocumentQueueControl, IJobExecutor, ISelectionContextSource
{
    private static readonly TimeSpan BridgeRequestTimeout = TimeSpan.FromSeconds(45);
    // The optional change_submit wait must always finish inside the Codex dynamic-tool deadline
    // (30s, CodexAppServerClient.DynamicToolCallTimeout): the block is capped at SubmitWaitCap and
    // additionally bounded so the whole tool call stays under SubmitWaitDeadline, leaving headroom
    // to write the projection. Keep dynamic-tool budget < per-bridge-op budget (BridgeRequestTimeout).
    private static readonly TimeSpan SubmitWaitDeadline = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan SubmitWaitCap = TimeSpan.FromSeconds(15);
    private const int MaximumArtifactBytes = 2 * 1024 * 1024;
    private const int MaximumCanonicalNumberCharacters = 4096;

    private readonly object _connectionGate = new();
    private readonly object _scheduleGate = new();
    private readonly object _executionGate = new();
    private readonly SemaphoreSlim _submissionGate = new(1, 1);
    private readonly SemaphoreSlim _historyGate = new(1, 1);
    private readonly AsyncDocumentGate _documentGate = new();
    private readonly ConcurrentDictionary<Guid, PendingBridgeRequest> _pending = new();
    private readonly ConcurrentDictionary<Guid, LiveJobEntry> _jobs = new();
    private readonly ProblemLog? _problemLog;
    private readonly ConcurrentDictionary<string, Guid> _idempotency = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, Task> _completionObservers = new();
    private readonly SessionStore _store;
    private readonly AgentHostOptions _options;
    private readonly EventHub _events;
    private readonly ILogger<LiveDocumentBackend> _logger;
    private readonly ConflictDetector _conflictDetector = new();
    // Per-resource "last committed by whom, to what fingerprint" ledger used to resolve gptino:auto
    // expectations to a live fingerprint ONLY for a session's own self-sequential writes. Both the commit
    // write and the execute-time read run on the SingleWriterBroker's single worker thread (one job at a
    // time under the write lease), so access is fully serialized and needs no lock.
    private readonly Dictionary<string, ResourceLedgerEntry> _resourceLedger = new(StringComparer.Ordinal);
    private readonly SingleWriterBroker _broker;
    private readonly DurableJobStore _jobStore;
    private readonly string _dataRoot;
    private readonly string _artifactRoot;
    private readonly BridgeSecret? _bridgeSecret;
    private DocumentPipeConnection? _connection;
    // Per-registered-Grasshopper-document state, keyed by the target's StableTargetKey. Guarded by
    // _connectionGate for membership; the per-state snapshot cache follows the same (benign-race)
    // discipline the former singleton _snapshot field used. Registration order defines the DEFAULT
    // target: the only entry when one document is open, otherwise the first registered — so every
    // pre-existing single-document consumer keeps byte-for-byte behavior.
    private readonly Dictionary<string, TargetState> _targets = new(StringComparer.Ordinal);
    private long _targetSequence;
    // Monotonic receipt counter for SelectionChanged events, guarded by _connectionGate; drives
    // the "most recently updated target" selection surfaces.
    private long _selectionSequence;
    private SessionOrderSnapshot _sessionOrder;
    private IReadOnlyDictionary<Guid, SessionRunState> _sessionStates =
        new Dictionary<Guid, SessionRunState>();
    private CancellationTokenSource? _currentExecution;
    private Guid? _writerSessionId;
    private DateTimeOffset? _writerStartedAt;
    private long _enqueueSequence;

    public LiveDocumentBackend(
        SessionStore store,
        AgentHostOptions options,
        EventHub events,
        ILogger<LiveDocumentBackend> logger,
        ProblemLog? problemLog = null)
    {
        _store = store;
        _options = options;
        _events = events;
        _logger = logger;
        _problemLog = problemLog;
        _sessionOrder = new SessionOrderSnapshot(options.ProjectId, Array.Empty<Guid>(), 0);
        _broker = new SingleWriterBroker(this, ReadSessionOrder, ReadSessionStates);
        _dataRoot = options.ResolveDataDirectory();
        _artifactRoot = Path.Combine(_dataRoot, "artifacts");
        Directory.CreateDirectory(_artifactRoot);
        _jobStore = new DurableJobStore(Path.Combine(_dataRoot, "live-jobs.db"));

        if (!string.IsNullOrWhiteSpace(options.BridgePipe))
        {
            var encodedSecret = Environment.GetEnvironmentVariable("GPTINO_BRIDGE_SECRET")
                ?? throw new InvalidOperationException(
                    "GPTINO_BRIDGE_SECRET is required when a document bridge pipe is configured.");
            _bridgeSecret = BridgeSecret.FromBase64(encodedSecret);
            Environment.SetEnvironmentVariable("GPTINO_BRIDGE_SECRET", null);
        }
    }

    public bool IsConnected
    {
        get
        {
            lock (_connectionGate)
            {
                return _connection is { IsConnected: true } && _targets.Count > 0;
            }
        }
    }

    public DocumentRuntime? CurrentTarget
    {
        get
        {
            lock (_connectionGate)
            {
                return DefaultTargetStateUnsafe()?.Target;
            }
        }
    }

    /// <summary>
    /// Every registered Grasshopper document (durable docKey + current file path) in registration
    /// order — the first entry is the default target. Empty before the first registration.
    /// </summary>
    public IReadOnlyList<RegisteredGrasshopperDocument> RegisteredGrasshopperDocuments
    {
        get
        {
            lock (_connectionGate)
            {
                return _targets.Values
                    .OrderBy(state => state.Sequence)
                    .Select(state => new RegisteredGrasshopperDocument(
                        state.DocKey,
                        state.Target.GrasshopperPath))
                    .ToArray();
            }
        }
    }

    public int QueueLength => _jobs.Values.Count(entry => IsActive(entry.State));

    public long CurrentRevision => DefaultTargetStateOrNull()?.Snapshot?.State.Revision ?? 0;

    public string? CurrentGitCommit => DefaultTargetStateOrNull()?.Snapshot?.State.GitCommit;

    public string? WriterSessionId
    {
        get
        {
            lock (_executionGate)
            {
                return _writerSessionId?.ToString("D");
            }
        }
    }

    public DateTimeOffset? WriterStartedAt
    {
        get
        {
            lock (_executionGate)
            {
                return _writerStartedAt;
            }
        }
    }

    public Task<object> ReadSnapshotAsync(
        SessionRecord session,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return ReadSnapshotCoreAsync(session, arguments, cancellationToken);
    }

    public Task<object> ReadSnapshotAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        ReadSnapshotCoreAsync(session: null, arguments, cancellationToken);

    private async Task<object> ReadSnapshotCoreAsync(
        SessionRecord? session,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        using var documentRead = await _documentGate.EnterReadAsync(cancellationToken)
            .ConfigureAwait(false);
        // Sessionless callers (dev endpoints) read the default target; session calls route by the
        // session's Grasshopper-document binding with the shared resolution rule.
        var targetState = session is null
            ? RequireDefaultTargetState()
            : ResolveSessionTargetState(session);
        var sessionId = session?.Id;
        var scopes = arguments.TryGetProperty("scopes", out var scopeElement) &&
            scopeElement.ValueKind == JsonValueKind.Array
            ? scopeElement.EnumerateArray()
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : Array.Empty<string>();
        var inspectionTasks = scopes
            .Where(scope => !string.Equals(scope, "canvas", StringComparison.OrdinalIgnoreCase))
            .Select(scope => ReadInspectionScopeAsync(targetState, scope, cancellationToken))
            .ToArray();
        SnapshotEnvelope? cached;
        lock (_executionGate)
        {
            cached = _writerSessionId is not null ? targetState.Snapshot : null;
        }

        var snapshotTask = cached is not null
            ? Task.FromResult(cached)
            : CaptureSnapshotAsync(targetState, force: false, cancellationToken);
        await Task.WhenAll(inspectionTasks).ConfigureAwait(false);
        var snapshot = await snapshotTask.ConfigureAwait(false);
        var knownId = arguments.TryGetProperty("knownSnapshotId", out var knownElement)
            ? knownElement.GetString()
            : null;
        return new
        {
            sessionId,
            snapshotId = snapshot.SnapshotId,
            unchanged = string.Equals(knownId, snapshot.SnapshotId, StringComparison.Ordinal),
            staleWhileWrite = cached is not null,
            revision = snapshot.State.Revision,
            gitCommit = snapshot.State.GitCommit,
            capturedAt = snapshot.State.CapturedAt,
            target = snapshot.State.Target,
            resources = snapshot.State.Resources,
            canvas = snapshot.Canvas,
            inspections = inspectionTasks.Select(task => task.Result).ToArray()
        };
    }

    // Catalog and Rhino-scene reads are document-agnostic (the component library is per Rhino
    // process, the Rhino doc is shared across all Grasshopper targets), so they use default-target
    // resolution: any single registered target, first registered when several are open.
    public Task<object> SearchComponentCatalogAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        ReadBridgeQueryAsync(
            RequireDefaultTargetState(),
            BridgeAdapterOwner.CordycepsCanvas,
            "canvas.catalog",
            arguments,
            cancellationToken);

    public Task<object> ListRhinoObjectsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        ReadBridgeQueryAsync(
            RequireDefaultTargetState(),
            BridgeAdapterOwner.CordycepsRhino,
            "rhino.list",
            arguments,
            cancellationToken);

    public Task<object> InspectCanvasOutputsAsync(
        SessionRecord session,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        return InspectCanvasOutputsCoreAsync(session, arguments, cancellationToken);
    }

    public Task<object> InspectCanvasOutputsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        InspectCanvasOutputsCoreAsync(session: null, arguments, cancellationToken);

    private Task<object> InspectCanvasOutputsCoreAsync(
        SessionRecord? session,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        // The document gate is writer-preferring, so queuing behind an executing job would stall
        // this read for the whole write epoch and blow the Codex dynamic-tool deadline. Fail fast
        // with a recipe instead: committed jobs already carry their post-solve outputs inline.
        if (WriterSessionId is not null)
        {
            return Task.FromResult<object>(new
            {
                writerActive = true,
                message = "A writer session currently holds the document. Read the committed job's " +
                    "outputs from change_submit/job_status instead, or retry after the queue drains."
            });
        }
        var targetState = session is null
            ? RequireDefaultTargetState()
            : ResolveSessionTargetState(session);
        return ReadBridgeQueryAsync(
            targetState,
            BridgeAdapterOwner.CordycepsCanvas,
            "canvas.inspectOutputs",
            arguments,
            cancellationToken);
    }

    private async Task<object> ReadBridgeQueryAsync(
        TargetState targetState,
        BridgeAdapterOwner owner,
        string operation,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        using var documentRead = await _documentGate.EnterReadAsync(cancellationToken)
            .ConfigureAwait(false);
        RequireAdapter(targetState, owner);
        var request = new BridgeOperationRequest(
            $"read-{Guid.NewGuid():N}",
            owner,
            operation,
            BridgeOperationAccess.Read,
            targetState.Snapshot?.State.Revision ?? 0,
            ExpectedFingerprint: null,
            WriterLeaseToken: null,
            arguments.Clone());
        var response = await SendOperationAsync(targetState.Target, request, cancellationToken)
            .ConfigureAwait(false);
        return new
        {
            result = response.Result.Clone(),
            fingerprint = response.AfterFingerprint,
            diagnostics = response.Diagnostics
        };
    }

    private async Task<ScopedInspection> ReadInspectionScopeAsync(
        TargetState targetState,
        string scope,
        CancellationToken cancellationToken)
    {
        var separator = scope.IndexOf(':');
        if (separator <= 0 || separator == scope.Length - 1 ||
            !Guid.TryParse(scope[(separator + 1)..], out var objectId))
        {
            throw new InvalidOperationException(
                $"Invalid snapshot scope '{scope}'. Expected owner:<guid>.");
        }

        var prefix = scope[..separator].ToLowerInvariant();
        var (owner, operation, arguments) = prefix switch
        {
            "wireify" => (
                BridgeAdapterOwner.Wireify,
                "python.inspect",
                JsonSerializer.SerializeToElement(new { componentId = objectId }, BridgeProtocol.JsonOptions)),
            "wireify-messages" => (
                BridgeAdapterOwner.Wireify,
                "python.runtimeMessages",
                JsonSerializer.SerializeToElement(new { componentId = objectId }, BridgeProtocol.JsonOptions)),
            "rhino" => (
                BridgeAdapterOwner.CordycepsRhino,
                "rhino.inspect",
                JsonSerializer.SerializeToElement(new { objectId }, BridgeProtocol.JsonOptions)),
            _ => throw new InvalidOperationException($"Unsupported snapshot scope owner '{prefix}'.")
        };
        RequireAdapter(targetState, owner);
        var request = new BridgeOperationRequest(
            $"read-{Guid.NewGuid():N}",
            owner,
            operation,
            BridgeOperationAccess.Read,
            targetState.Snapshot?.State.Revision ?? 0,
            ExpectedFingerprint: null,
            WriterLeaseToken: null,
            arguments);
        var response = await SendOperationAsync(targetState.Target, request, cancellationToken)
            .ConfigureAwait(false);
        return new ScopedInspection(
            scope,
            owner,
            operation,
            response.AfterFingerprint,
            response.Result.Clone(),
            response.Diagnostics);
    }

    public async Task<object> SubmitChangeAsync(
        SessionRecord session,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        // Measured from method ENTRY: the pre-enqueue work below (payload preflight, a forced
        // snapshot behind the document read gate, the durable insert) can itself consume a large
        // share of the Codex dynamic-tool budget, so any post-enqueue wait must subtract it.
        var elapsed = Stopwatch.StartNew();
        var wait = arguments.TryGetProperty("wait", out var waitElement) &&
            waitElement.ValueKind == JsonValueKind.True;
        var changeSetElement = arguments.GetProperty("changeSet");
        var changeSet = changeSetElement.Deserialize<ChangeSet>(BridgeProtocol.JsonOptions)
            ?? throw new InvalidOperationException("changeSet cannot be null.");
        // Predicates are deterministic functions of the operation kinds; when the model omits
        // them the server attaches the standard set instead of rejecting. Applied BEFORE the
        // request hash so an identical retry dedups identically. Explicit predicates still win.
        changeSet = ApplyDefaultPredicates(changeSet);
        var expectedSnapshotId = RequiredString(arguments, "expectedSnapshotId");
        var idempotencyKey = RequiredString(arguments, "idempotencyKey");
        var summary = RequiredString(arguments, "summary");
        if (idempotencyKey.Length > 128)
        {
            throw new InvalidOperationException("idempotencyKey cannot exceed 128 characters.");
        }

        ValidateChangeSet(changeSet, session);
        var draftOperations = await PreflightDraftOperationsAsync(
            session.Id,
            changeSet,
            cancellationToken).ConfigureAwait(false);
        var requestHash = ComputeAcceptedRequestHash(
            changeSet,
            expectedSnapshotId,
            summary,
            draftOperations);
        var idempotencyScope = IdempotencyScope(session.Id, idempotencyKey);
        LiveJobEntry? duplicateEntry = null;
        await _submissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_idempotency.TryGetValue(idempotencyScope, out var existingId) &&
                _jobs.TryGetValue(existingId, out var existing))
            {
                RequireMatchingRequestHash(existing.RequestHash, requestHash, idempotencyKey);
                duplicateEntry = existing;
            }
        }
        finally
        {
            _submissionGate.Release();
        }
        if (duplicateEntry is not null)
        {
            // Never wait while holding the submission gate; the optional block happens out here.
            return await ProjectJobAfterOptionalWaitAsync(
                duplicateEntry,
                duplicate: true,
                wait,
                elapsed,
                cancellationToken).ConfigureAwait(false);
        }

        // Session -> Grasshopper document resolution happens once at submit and is frozen into the
        // job (durably, for restart recovery): the queue and executor never re-derive it. Resolved
        // AFTER the duplicate fast path above so an idempotent replay (a matching request hash
        // proves the request is byte-identical to the previously validated one) keeps answering
        // even when no target is registered — e.g. right after an AgentHost restart.
        var targetState = ResolveSessionTargetState(session);
        ValidateExpectationCoverage(
            changeSet,
            draftOperations,
            targetState.Target.GrasshopperDocumentId);

        SnapshotEnvelope snapshot;
        using (await _documentGate.EnterReadAsync(cancellationToken).ConfigureAwait(false))
        {
            snapshot = await CaptureSnapshotAsync(targetState, force: true, cancellationToken)
                .ConfigureAwait(false);
        }
        // "gptino:auto" opts out of the whole-document snapshot/revision gate; per-resource auto expectations
        // (resolved at execute time against this session's own last-committed fingerprints) then govern every
        // resource the ChangeSet touches, so a foreign change to an UNRELATED resource no longer false-rejects.
        if (!string.Equals(expectedSnapshotId, ResourceExpectation.AutoFingerprint, StringComparison.Ordinal) &&
            !string.Equals(expectedSnapshotId, snapshot.SnapshotId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Snapshot changed. Expected '{expectedSnapshotId}', current is '{snapshot.SnapshotId}'. " +
                "Resubmit with expectedSnapshotId set to the current id above — or use 'gptino:auto' so the " +
                "server anchors it for you. Do not restart discovery.");
        }
        if (changeSet.BaseSnapshotRevision != ResourceExpectation.AutoBaseRevision &&
            changeSet.BaseSnapshotRevision != snapshot.State.Revision)
        {
            throw new InvalidOperationException(
                $"ChangeSet base revision {changeSet.BaseSnapshotRevision} does not match current revision " +
                $"{snapshot.State.Revision}. Resubmit with baseSnapshotRevision set to -1 (auto) or to the " +
                "current revision above.");
        }

        await RefreshScheduleAsync(cancellationToken).ConfigureAwait(false);
        LiveJobEntry entry;
        var duplicate = false;
        await _submissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_idempotency.TryGetValue(idempotencyScope, out var existingId) &&
                _jobs.TryGetValue(existingId, out var existing))
            {
                RequireMatchingRequestHash(existing.RequestHash, requestHash, idempotencyKey);
                entry = existing;
                duplicate = true;
            }
            else
            {
                var jobId = Guid.NewGuid();
                ChangeSet frozenChangeSet;
                try
                {
                    frozenChangeSet = await FreezeOperationPayloadsAsync(
                        session.Id,
                        jobId,
                        changeSet,
                        draftOperations,
                        cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    DeleteUnacceptedReservedJob(session.Id, jobId);
                    throw;
                }

                var conflicts = DetectQueuedConflicts(frozenChangeSet, targetState.DocKey);
                foreach (var queuedConflict in conflicts)
                {
                    _problemLog?.RecordQueuedConflict(
                        jobId,
                        session.Id,
                        queuedConflict.OtherJobId,
                        queuedConflict.Conflict);
                }
                var enqueuedAt = DateTimeOffset.UtcNow;
                var queuedJob = new QueuedJob(
                    jobId,
                    frozenChangeSet,
                    Interlocked.Increment(ref _enqueueSequence),
                    enqueuedAt);
                entry = new LiveJobEntry(
                    queuedJob,
                    session,
                    summary,
                    idempotencyKey,
                    requestHash,
                    conflicts,
                    targetState.DocKey);
                DurableJobInsertResult insert;
                try
                {
                    insert = await _jobStore.InsertOrReadAsync(
                        new DurableJobRecord(
                            jobId,
                            session.Id,
                            idempotencyKey,
                            summary,
                            frozenChangeSet,
                            queuedJob.EnqueueSequence,
                            JobState.Queued,
                            "queued",
                            null,
                            enqueuedAt,
                            enqueuedAt,
                            enqueuedAt,
                            requestHash,
                            targetState.DocKey),
                        cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    DeleteUnacceptedReservedJob(session.Id, jobId);
                    throw;
                }
                if (!insert.Inserted)
                {
                    DeleteUnacceptedReservedJob(session.Id, jobId);
                    RequireMatchingRequestHash(
                        insert.Record.RequestHash,
                        requestHash,
                        idempotencyKey);
                    if (_jobs.TryGetValue(insert.Record.JobId, out existing))
                    {
                        _idempotency.TryAdd(idempotencyScope, existing.Job.JobId);
                        entry = existing;
                    }
                    else
                    {
                        await _jobStore.UpdateStateAsync(
                            insert.Record.JobId,
                            JobState.RecoveryRequired,
                            "recoveryrequired",
                            DurableJobStore.RestartRecoveryMessage,
                            cancellationToken).ConfigureAwait(false);
                        var recovered = insert.Record with
                        {
                            State = JobState.RecoveryRequired,
                            Phase = "recoveryrequired",
                            Message = DurableJobStore.RestartRecoveryMessage,
                            UpdatedAt = DateTimeOffset.UtcNow
                        };
                        entry = CreateRestoredEntry(recovered, session);
                        RegisterRestoredEntry(entry);
                    }
                    duplicate = true;
                }
                else if (!_jobs.TryAdd(jobId, entry) || !_idempotency.TryAdd(idempotencyScope, jobId))
                {
                    _jobs.TryRemove(jobId, out _);
                    _idempotency.TryRemove(idempotencyScope, out _);
                    throw new InvalidOperationException(
                        "The change was durably accepted but could not be registered in the live queue. " +
                        "Restart AgentHost to expose it as recovery-required.");
                }
            }
        }
        finally
        {
            _submissionGate.Release();
        }

        if (!duplicate)
        {
            var ticket = _broker.Enqueue(entry.Job);
            TrackCompletion(entry, ticket.Completion);
            _events.Publish();
        }
        return await ProjectJobAfterOptionalWaitAsync(
            entry,
            duplicate,
            wait,
            elapsed,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Optionally blocks on the job's completion before projecting, so fast jobs return their
    /// terminal state (diagnostics, committed view, observations) in the change_submit response
    /// itself. The wait is bounded well inside the Codex dynamic-tool deadline and measured from
    /// tool entry; on timeout the caller falls back to job_status polling — that is normal, not an
    /// error, especially when other sessions' jobs are ahead in the queue.
    /// </summary>
    private async Task<object> ProjectJobAfterOptionalWaitAsync(
        LiveJobEntry entry,
        bool duplicate,
        bool wait,
        Stopwatch elapsed,
        CancellationToken cancellationToken)
    {
        if (wait && IsActive(entry.State))
        {
            var remaining = SubmitWaitDeadline - elapsed.Elapsed;
            var cap = remaining < SubmitWaitCap ? remaining : SubmitWaitCap;
            if (cap > TimeSpan.Zero)
            {
                await Task.WhenAny(
                    entry.Completion,
                    Task.Delay(cap, cancellationToken)).ConfigureAwait(false);
            }
        }
        return ProjectJob(entry, duplicate);
    }

    public Task<object> ReadJobAsync(JsonElement arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var jobText = RequiredString(arguments, "jobId");
        if (!Guid.TryParse(jobText, out var jobId) || !_jobs.TryGetValue(jobId, out var entry))
        {
            throw new KeyNotFoundException($"Job '{jobText}' was not found.");
        }

        return Task.FromResult(ProjectJob(entry, duplicate: false));
    }

    public Task StopCurrentAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_executionGate)
        {
            _currentExecution?.Cancel();
        }
        return Task.CompletedTask;
    }

    public async Task RefreshScheduleAsync(CancellationToken cancellationToken = default)
    {
        var (sessions, version) = await _store.ReadStateAsync(cancellationToken).ConfigureAwait(false);
        var projectId = CurrentTarget?.ProjectId ?? _options.ProjectId;
        var order = new SessionOrderSnapshot(projectId, sessions.Select(item => item.Id).ToArray(), version);
        var states = sessions.ToDictionary(item => item.Id, item => item.State switch
        {
            SessionStates.Paused => SessionRunState.Paused,
            SessionStates.Failed => SessionRunState.Failed,
            SessionStates.Running => SessionRunState.Running,
            SessionStates.Waiting => SessionRunState.Ready,
            _ => SessionRunState.Idle
        });
        lock (_scheduleGate)
        {
            _sessionOrder = order;
            _sessionStates = states;
        }
        _broker.NotifyScheduleChanged();
    }

    public void SetPaused(bool paused)
    {
        if (paused)
        {
            _broker.Pause();
        }
        else
        {
            _broker.Resume();
        }
        _events.Publish();
    }

    public IReadOnlyList<LiveQueueItem> ReadQueue()
    {
        var order = ReadSessionOrder();
        var rank = order.OrderedSessionIds
            .Select((sessionId, index) => (sessionId, index))
            .ToDictionary(item => item.sessionId, item => item.index);
        return _jobs.Values
            .Select(entry => new LiveQueueItem(
                entry.Job.JobId,
                entry.Job.ChangeSet.SessionId,
                entry.Summary,
                entry.State,
                entry.Job.EnqueueSequence,
                entry.Job.EnqueuedAt,
                DeriveQueueTarget(entry.Job.ChangeSet),
                entry.TargetDoc))
            .Where(item => item.State is
                JobState.Queued or JobState.Validating or JobState.Executing or JobState.Verifying)
            .OrderBy(item => item.State is JobState.Executing or JobState.Verifying ? 0 : 1)
            .ThenBy(item => rank.GetValueOrDefault(item.SessionId, int.MaxValue))
            .ThenBy(item => item.EnqueueSequence)
            .ToArray();
    }

    // Which document a queued job writes, so the node-graph animates the correct orchestrator->document wire.
    // Derived from the write resource kinds (Grasshopper* vs Rhino*); null when a job writes neither or both
    // in a way the UI should animate together (the panel treats a missing target as "animate both").
    private static string? DeriveQueueTarget(ChangeSet changeSet)
    {
        var grasshopper = false;
        var rhino = false;
        foreach (var resource in changeSet.WriteSet.Select(expectation => expectation.Resource)
            .Concat(changeSet.Operations.SelectMany(operation => operation.Writes)))
        {
            var kind = resource.Kind.ToString();
            if (kind.StartsWith("Grasshopper", StringComparison.Ordinal))
            {
                grasshopper = true;
            }
            else if (kind.StartsWith("Rhino", StringComparison.Ordinal))
            {
                rhino = true;
            }
        }
        return (grasshopper, rhino) switch
        {
            (true, true) => "both",
            (true, false) => "grasshopper",
            (false, true) => "rhino",
            _ => null,
        };
    }

    public IReadOnlyList<LiveConflictItem> ReadConflicts()
    {
        var active = ReadQueue().Select(item => item.JobId).ToHashSet();
        return _jobs.Values
            .Where(entry => active.Contains(entry.Job.JobId))
            .SelectMany(entry => entry.Conflicts.Select(conflict => new LiveConflictItem(
                entry.Job.JobId,
                conflict.OtherJobId,
                conflict.Conflict.Kind,
                conflict.Conflict.Resource,
                conflict.Conflict.Message)))
            .Where(item => active.Contains(item.OtherJobId))
            .ToArray();
    }

    public IReadOnlyList<LiveProblemItem> ReadRecentProblems(int limit = 20)
    {
        var boundedLimit = Math.Clamp(limit, 1, 100);
        return _jobs.Values
            .Where(entry => entry.State is
                JobState.RecoveryRequired or JobState.Blocked or JobState.Failed)
            .OrderByDescending(entry => entry.UpdatedAt)
            .Take(boundedLimit)
            .Select(entry =>
            {
                var blocking = entry.BlockingConflicts?.FirstOrDefault(conflict => conflict.Resource is not null)
                    ?? entry.BlockingConflicts?.FirstOrDefault();
                return new LiveProblemItem(
                    entry.Job.JobId,
                    entry.Job.ChangeSet.SessionId,
                    entry.Summary,
                    entry.State,
                    entry.Message,
                    entry.UpdatedAt,
                    blocking?.Resource,
                    blocking?.Kind);
            })
            .ToArray();
    }

    public async ValueTask<JobExecutionResult> ExecuteAsync(
        QueuedJob job,
        CancellationToken cancellationToken)
    {
        if (!_jobs.TryGetValue(job.JobId, out var entry))
        {
            return new JobExecutionResult(job.JobId, JobState.Failed, "Queued job metadata was not found.");
        }

        using var documentWrite = await _documentGate.EnterWriteAsync(cancellationToken)
            .ConfigureAwait(false);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_executionGate)
        {
            _currentExecution = execution;
            _writerSessionId = job.ChangeSet.SessionId;
            _writerStartedAt = DateTimeOffset.UtcNow;
        }
        await SetJobPhaseAsync(
            entry,
            JobState.Validating,
            "Validating the current immutable snapshot.").ConfigureAwait(false);
        _broker.RecordJobState(job.JobId, JobState.Validating);
        _events.Publish();

        var liveChanged = false;
        var writeMayHaveChanged = false;
        var diagnostics = new List<JobDiagnostic>();
        try
        {
            // The docKey was frozen at submit time; a document closed between enqueue and execution
            // fails deterministically here (no write happened) with the registered-document listing.
            var targetState = ResolveJobTargetState(entry.TargetDoc);
            var before = await CaptureSnapshotAsync(targetState, force: true, execution.Token)
                .ConfigureAwait(false);
            var preparedOperations = await PreflightFrozenOperationsAsync(
                entry,
                targetState,
                execution.Token).ConfigureAwait(false);
            before = await EnrichSnapshotForConflictValidationAsync(
                before,
                job.ChangeSet,
                targetState,
                execution.Token).ConfigureAwait(false);
            // Resolve any gptino:auto expectations against live state (self-sequential only) BEFORE conflict
            // validation, then validate and execute the RESOLVED ChangeSet so ValidateAgainstSnapshot and the
            // bridge requests see concrete fingerprints. A declined auto returns a Stale-class conflict here.
            var (resolvedChangeSet, autoConflicts) = ResolveAutoExpectations(
                job.ChangeSet,
                before.State,
                job.ChangeSet.SessionId,
                _resourceLedger);
            if (autoConflicts.Count > 0)
            {
                var autoMessage = string.Join(" ", autoConflicts);
                await SetJobPhaseAsync(entry, JobState.Blocked, autoMessage).ConfigureAwait(false);
                return new JobExecutionResult(job.JobId, JobState.Blocked, autoMessage);
            }
            var conflicts = _conflictDetector.ValidateAgainstSnapshot(resolvedChangeSet, before.State);
            if (conflicts.Count > 0)
            {
                var message = string.Join(" ", conflicts.Select(conflict => conflict.Message));
                await SetJobPhaseAsync(entry, JobState.Blocked, message, conflicts).ConfigureAwait(false);
                return new JobExecutionResult(job.JobId, JobState.Blocked, message);
            }

            await PreflightBridgePayloadsAsync(
                targetState,
                preparedOperations,
                before.State.Revision,
                execution.Token).ConfigureAwait(false);
            PreflightPythonSchemas(preparedOperations, before);

            await EnsureHistoryBaselineAsync(targetState, before, execution.Token).ConfigureAwait(false);
            var lease = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await SetJobPhaseAsync(
                entry,
                JobState.Executing,
                "Applying typed operations through the document bridge.").ConfigureAwait(false);
            _broker.RecordJobState(job.JobId, JobState.Executing);
            _events.Publish();

            var operationObservations = new List<ResourceObservation>();
            var rollingPythonFingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prepared in preparedOperations)
            {
                var operation = prepared.Operation;
                var bridgeOwner = prepared.Owner;
                var access = OperationSemantics.IsWrite(operation.Kind)
                    ? BridgeOperationAccess.Write
                    : BridgeOperationAccess.Read;
                var pythonWrite = bridgeOwner == BridgeAdapterOwner.Wireify &&
                    access == BridgeOperationAccess.Write
                    ? PythonStateWrite(operation)
                    : null;
                var expectedFingerprint = FindExpectedFingerprint(resolvedChangeSet, operation);
                if (pythonWrite is not null &&
                    rollingPythonFingerprints.TryGetValue(pythonWrite.Id, out var rollingFingerprint))
                {
                    expectedFingerprint = rollingFingerprint;
                }
                var request = new BridgeOperationRequest(
                    operation.OperationId,
                    bridgeOwner,
                    prepared.BridgeOperation,
                    access,
                    before.State.Revision,
                    expectedFingerprint,
                    access == BridgeOperationAccess.Write ? lease : null,
                    prepared.Arguments);
                request.Validate();
                writeMayHaveChanged |= access == BridgeOperationAccess.Write;
                var response = await SendOperationAsync(targetState.Target, request, execution.Token)
                    .ConfigureAwait(false);
                liveChanged |= response.Changed;
                diagnostics.AddRange(response.Diagnostics.Select(item =>
                    new JobDiagnostic(operation.OperationId, item.Severity, item.Code, item.Message)));
                if (pythonWrite is not null)
                {
                    if (string.IsNullOrWhiteSpace(expectedFingerprint) ||
                        !string.Equals(
                            response.BeforeFingerprint,
                            expectedFingerprint,
                            StringComparison.Ordinal) ||
                        string.IsNullOrWhiteSpace(response.AfterFingerprint))
                    {
                        throw new InvalidOperationException(
                            $"Wireify operation '{operation.OperationId}' returned an invalid fingerprint chain.");
                    }
                    rollingPythonFingerprints[pythonWrite.Id] = response.AfterFingerprint;
                }
                if (bridgeOwner is BridgeAdapterOwner.Wireify or BridgeAdapterOwner.CordycepsRhino)
                {
                    operationObservations.AddRange(operation.Writes.Select(resource =>
                        new ResourceObservation(resource, response.AfterFingerprint)));
                }
                var error = response.Diagnostics.FirstOrDefault(item =>
                    item.Severity == BridgeDiagnosticSeverity.Error);
                if (error is not null && !IsScriptContentOperation(operation.Kind))
                {
                    // For non-script operations an Error diagnostic means the operation itself
                    // failed — abort. Script-content errors (compile/runtime) mean the write
                    // LANDED and the errors describe the script: finish the loop so the after
                    // snapshot reflects the complete application and Verify reports every error.
                    throw new InvalidOperationException(
                        $"Operation '{operation.OperationId}' reported {error.Code}: {error.Message}");
                }
            }

            await SetJobPhaseAsync(
                entry,
                JobState.Verifying,
                "Capturing and verifying the resulting document state.").ConfigureAwait(false);
            _broker.RecordJobState(job.JobId, JobState.Verifying);
            _events.Publish();
            var after = await CaptureSnapshotAsync(targetState, force: true, execution.Token)
                .ConfigureAwait(false);
            var verificationProblems = Verify(
                job.ChangeSet,
                after,
                diagnostics,
                operationObservations);
            if (verificationProblems.Count > 0)
            {
                // Deterministic failure: every operation completed and the after-snapshot is in
                // hand, so the post-state is fully known even though writes landed. The job still
                // never commits (no history revision for a red state — a model's success claim is
                // refuted structurally), but the session gets everything it needs to iterate: the
                // full diagnostics, the actual post-write fingerprints under `applied`, and a
                // ledger updated to live state so its next gptino:auto submission is not blocked
                // as stale. RecoveryRequired stays reserved for genuinely unknown outcomes
                // (mid-write throws, cancellation, history-commit failures, restarts).
                entry.Diagnostics = diagnostics;
                try
                {
                    entry.Applied = BuildCommittedJobView(job.ChangeSet, after);
                    entry.Sockets = CollectComponentSockets(job.ChangeSet, after);
                    entry.Outputs = await CollectComponentOutputsAsync(
                        targetState.Target,
                        job.ChangeSet,
                        after,
                        execution.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        exception,
                        "Could not build the applied view for job {JobId}.",
                        job.JobId);
                }
                UpdateResourceLedger(before, after, job.ChangeSet.SessionId, job.JobId);
                var message = string.Join(" ", verificationProblems);
                await SetJobPhaseAsync(entry, JobState.Failed, message).ConfigureAwait(false);
                return new JobExecutionResult(job.JobId, JobState.Failed, message);
            }

            try
            {
                await CommitHistoryAsync(entry, targetState, after, execution.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var message = $"Live change verified, but provenance commit failed: {exception.Message}";
                await SetJobPhaseAsync(entry, JobState.RecoveryRequired, message).ConfigureAwait(false);
                return new JobExecutionResult(job.JobId, JobState.RecoveryRequired, message);
            }

            try
            {
                entry.Committed = BuildCommittedJobView(job.ChangeSet, after);
                entry.Applied = entry.Committed;
                entry.Diagnostics = diagnostics;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Chaining data is observability sugar. The live change is verified and
                // committed at this point; a projection bug must never demote the job.
                _logger.LogWarning(exception, "Could not build the committed chaining view for job {JobId}.", job.JobId);
            }
            try
            {
                // Post-solve observations, captured while this job still holds the write lease so
                // they are consistent with the commit: the Grasshopper-assigned socket identities of
                // reshaped components (from the after-snapshot; kills the follow-up snapshot_read)
                // and a live output inspection per written component (counts/types/bounds/samples).
                // Same never-demote discipline as the committed view above.
                entry.Sockets = CollectComponentSockets(job.ChangeSet, after);
                entry.Outputs = await CollectComponentOutputsAsync(
                    targetState.Target,
                    job.ChangeSet,
                    after,
                    execution.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Could not capture post-solve observations for job {JobId}.", job.JobId);
            }
            UpdateResourceLedger(before, after, job.ChangeSet.SessionId, job.JobId);
            await SetJobPhaseAsync(
                entry,
                JobState.Committed,
                "Verified and committed to managed history.").ConfigureAwait(false);
            return new JobExecutionResult(job.JobId, JobState.Committed, "Verified and committed.");
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            entry.Diagnostics ??= diagnostics;
            var state = liveChanged || writeMayHaveChanged ? JobState.RecoveryRequired : JobState.Cancelled;
            var message = liveChanged || writeMayHaveChanged
                ? "Execution stopped after a live change; review or recovery is required."
                : "Execution stopped before a live change was applied.";
            await SetJobPhaseAsync(entry, state, message).ConfigureAwait(false);
            return new JobExecutionResult(job.JobId, state, message);
        }
        catch (Exception exception)
        {
            entry.Diagnostics ??= diagnostics;
            var state = liveChanged || writeMayHaveChanged ? JobState.RecoveryRequired : JobState.Failed;
            await SetJobPhaseAsync(entry, state, exception.Message).ConfigureAwait(false);
            return new JobExecutionResult(job.JobId, state, exception.Message);
        }
        finally
        {
            lock (_executionGate)
            {
                if (ReferenceEquals(_currentExecution, execution))
                {
                    _currentExecution = null;
                    _writerSessionId = null;
                    _writerStartedAt = null;
                }
            }
            _events.Publish();
        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await _jobStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var durableJobs = await _jobStore.RecoverInterruptedAsync(cancellationToken)
            .ConfigureAwait(false);
        var (sessions, _) = await _store.ReadStateAsync(cancellationToken).ConfigureAwait(false);
        var sessionsById = sessions.ToDictionary(session => session.Id);
        foreach (var durable in durableJobs)
        {
            if (durable.ChangeSet.SessionId != durable.SessionId)
            {
                throw new InvalidDataException(
                    $"Durable job '{durable.JobId:D}' has inconsistent session identity.");
            }

            var session = sessionsById.GetValueOrDefault(durable.SessionId)
                ?? CreateRecoveredSession(durable);
            RegisterRestoredEntry(CreateRestoredEntry(durable, session));
            _enqueueSequence = Math.Max(_enqueueSequence, durable.EnqueueSequence);
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
        if (durableJobs.Count > 0)
        {
            _events.Publish();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        await _broker.DisposeAsync().ConfigureAwait(false);
        var completionObservers = _completionObservers.Values.ToArray();
        if (completionObservers.Length > 0)
        {
            await Task.WhenAll(completionObservers).ConfigureAwait(false);
        }
        DocumentPipeConnection? connection;
        lock (_connectionGate)
        {
            connection = _connection;
            _connection = null;
            _targets.Clear();
        }
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        _historyGate.Dispose();
        _submissionGate.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BridgePipe) || _bridgeSecret is null)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
            return;
        }

        var endpoint = PipeEndpoint.FromName(_options.BridgePipe);
        var server = new DocumentPipeServer(endpoint, _bridgeSecret, $"agenthost-{Environment.ProcessId}");
        while (!stoppingToken.IsCancellationRequested)
        {
            DocumentPipeConnection? connection = null;
            try
            {
                connection = await server.AcceptAsync(stoppingToken).ConfigureAwait(false);
                lock (_connectionGate)
                {
                    _connection = connection;
                }
                await ReceiveLoopAsync(connection, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or BridgeProtocolException)
            {
                _logger.LogWarning(exception, "GPTino document bridge connection ended.");
            }
            finally
            {
                Disconnect(connection, "Document bridge disconnected.");
                if (connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ReceiveLoopAsync(
        DocumentPipeConnection connection,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && connection.IsConnected)
        {
            var frame = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            frame.Validate();
            if (frame.Kind is BridgeMessageKind.Response or BridgeMessageKind.Error)
            {
                CompletePending(frame);
                continue;
            }

            if (frame.Kind == BridgeMessageKind.Event &&
                string.Equals(frame.PayloadType, BridgeMessageTypes.RegisterDocument, StringComparison.Ordinal))
            {
                await RegisterTargetAsync(connection, frame, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (frame.Kind == BridgeMessageKind.Event &&
                string.Equals(frame.PayloadType, BridgeMessageTypes.DocumentClosed, StringComparison.Ordinal))
            {
                CloseTarget(frame);
                continue;
            }

            if (frame.Kind == BridgeMessageKind.Event &&
                string.Equals(frame.PayloadType, BridgeMessageTypes.SelectionChanged, StringComparison.Ordinal))
            {
                CacheSelection(frame);
            }
        }
    }

    // Two selection events whose backend receipt times are at most this far apart are treated as
    // one plugin fan-out burst (the plugin sends one event per sibling target per settled
    // selection, well inside this window) when picking which target's selection to surface.
    private static readonly TimeSpan SelectionBurstWindow = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The selection of the MOST RECENTLY updated target, or null before the first push. Within
    /// one plugin fan-out burst (one event per sibling target) the event carrying a non-empty
    /// Grasshopper canvas selection wins — sibling events share the same Rhino ids, so the one
    /// that names canvas objects identifies the document the user actually worked in. A
    /// discovery hint for turn context and the panel — never concurrency control.
    /// </summary>
    public SelectionChangedEvent? CurrentSelection
    {
        get
        {
            lock (_connectionGate)
            {
                return LatestSelectionStateUnsafe()?.Selection;
            }
        }
    }

    /// <summary>
    /// Durable docKey of the document the surfaced <see cref="CurrentSelection"/> belongs to,
    /// or null when no selection has been observed.
    /// </summary>
    public string? CurrentSelectionDocId
    {
        get
        {
            lock (_connectionGate)
            {
                return LatestSelectionStateUnsafe()?.DocKey;
            }
        }
    }

    /// <summary>Digest of the default target's last captured snapshot; null before the first capture.</summary>
    public CanvasDigest? CurrentCanvasDigest
    {
        get
        {
            lock (_connectionGate)
            {
                return CanvasDigestUnsafe(DefaultTargetStateUnsafe());
            }
        }
    }

    /// <summary>
    /// The cached selection of one document, routed by docKey with the shared non-throwing
    /// default rule: null docKey resolves to the only registered target when exactly one is
    /// open, otherwise (unknown key, or unbound among several) the answer is null.
    /// </summary>
    public SelectionChangedEvent? SelectionFor(string? docKey)
    {
        lock (_connectionGate)
        {
            return ResolveContextTargetUnsafe(docKey)?.Selection;
        }
    }

    /// <summary>Per-document canvas digest, with the same non-throwing resolution as <see cref="SelectionFor"/>.</summary>
    public CanvasDigest? CanvasDigestFor(string? docKey)
    {
        lock (_connectionGate)
        {
            return CanvasDigestUnsafe(ResolveContextTargetUnsafe(docKey));
        }
    }

    private static CanvasDigest? CanvasDigestUnsafe(TargetState? targetState)
    {
        var snapshot = targetState?.Snapshot;
        return snapshot is null
            ? null
            : new CanvasDigest(snapshot.State.Revision, snapshot.Canvas.Objects.Count);
    }

    // Non-throwing docKey resolution for ambient context (selection/digest hints): unlike tool
    // routing this must never fail a turn, so unknown/ambiguous simply yields nothing.
    private TargetState? ResolveContextTargetUnsafe(string? docKey)
    {
        var normalized = string.IsNullOrWhiteSpace(docKey) ? null : docKey.Trim();
        if (normalized is null)
        {
            return _targets.Count == 1 ? _targets.Values.First() : null;
        }
        return _targets.Values.FirstOrDefault(state =>
            string.Equals(state.DocKey, normalized, StringComparison.OrdinalIgnoreCase));
    }

    // The most recently updated selection across targets: newest receipt wins; within the
    // newest burst (see SelectionBurstWindow) an event with canvas objects beats the siblings'
    // Rhino-only echoes, and among several such events the latest wins.
    private TargetState? LatestSelectionStateUnsafe()
    {
        TargetState? newest = null;
        foreach (var state in _targets.Values)
        {
            if (state.Selection is not null &&
                (newest is null || state.SelectionSequence > newest.SelectionSequence))
            {
                newest = state;
            }
        }
        if (newest is null)
        {
            return null;
        }
        TargetState? bestWithCanvas = null;
        foreach (var state in _targets.Values)
        {
            if (state.Selection?.GrasshopperObjects is { Count: > 0 } &&
                newest.SelectionStamp - state.SelectionStamp <= SelectionBurstWindow &&
                (bestWithCanvas is null || state.SelectionSequence > bestWithCanvas.SelectionSequence))
            {
                bestWithCanvas = state;
            }
        }
        return bestWithCanvas ?? newest;
    }

    private void CacheSelection(BridgeFrame frame)
    {
        var target = frame.Target;
        if (target is null)
        {
            return;
        }
        // Selections are cached per registered target; events for unknown targets are dropped.
        var selection = frame.DeserializePayload<SelectionChangedEvent>();
        lock (_connectionGate)
        {
            if (!_targets.TryGetValue(target.StableTargetKey(), out var state))
            {
                return;
            }
            state.Selection = selection;
            // Receipt order + receipt time drive the "most recently updated" surfaces above.
            state.SelectionSequence = ++_selectionSequence;
            state.SelectionStamp = DateTimeOffset.UtcNow;
        }
        _events.Publish();
    }

    private async Task RegisterTargetAsync(
        DocumentPipeConnection connection,
        BridgeFrame frame,
        CancellationToken cancellationToken)
    {
        var requestedTarget = frame.Target
            ?? throw new BridgeProtocolException("target_required", "Document registration requires a target.");
        requestedTarget.Validate();
        var request = frame.DeserializePayload<RegisterDocumentRequest>();
        try
        {
            ValidateRegistration(requestedTarget);
            var key = requestedTarget.StableTargetKey();
            TargetState? renamedState = null;
            string? renamedFromDocKey = null;
            lock (_connectionGate)
            {
                // Sibling targets (same ProjectId — one Rhino document, N Grasshopper documents)
                // register side by side; the former one_target_only rejection applied only to a
                // different ProjectId, which project_mismatch above already covers.
                if (_targets.TryGetValue(key, out var existing))
                {
                    if (requestedTarget.Generation < existing.Target.Generation)
                    {
                        throw new BridgeProtocolException(
                            "stale_generation",
                            "Document registration generation is older than the current target.");
                    }

                    existing.Target = requestedTarget;
                    // Save As changes the Grasshopper path without changing the stable key; the
                    // durable docKey is path-derived, so recompute it on every re-registration.
                    var recomputedDocKey = AgentHostOptions.ComputeDocumentKey(requestedTarget.GrasshopperPath);
                    if (!string.Equals(recomputedDocKey, existing.DocKey, StringComparison.OrdinalIgnoreCase))
                    {
                        // The same live document (unchanged StableTargetKey) now derives a new
                        // docKey: everything frozen to the old key must follow the rename or it
                        // resolves "not registered" for a document that never closed. In-memory
                        // queued/active jobs are re-keyed here, atomically with the DocKey swap
                        // (ResolveTargetStateByDocKey serializes on this same gate); history and
                        // durable session/job rows migrate right after the lock.
                        renamedState = existing;
                        renamedFromDocKey = existing.DocKey;
                        foreach (var jobEntry in _jobs.Values)
                        {
                            if (IsActive(jobEntry.State) &&
                                string.Equals(jobEntry.TargetDoc, renamedFromDocKey, StringComparison.OrdinalIgnoreCase))
                            {
                                jobEntry.RemapTargetDoc(recomputedDocKey);
                            }
                        }
                    }
                    existing.DocKey = recomputedDocKey;
                    existing.Adapters = request.AvailableAdapters.ToHashSet();
                    if (existing.Snapshot is not null &&
                        !string.Equals(
                            existing.Snapshot.State.Target.Identity,
                            requestedTarget.Identity,
                            StringComparison.Ordinal))
                    {
                        existing.Snapshot = null;
                    }
                }
                else
                {
                    _targets[key] = new TargetState(
                        requestedTarget,
                        AgentHostOptions.ComputeDocumentKey(requestedTarget.GrasshopperPath),
                        ++_targetSequence)
                    {
                        Adapters = request.AvailableAdapters.ToHashSet()
                    };
                }
            }

            if (renamedState is not null && renamedFromDocKey is not null)
            {
                await MigrateRenamedDocumentKeyAsync(
                    renamedState,
                    renamedFromDocKey,
                    renamedState.DocKey,
                    cancellationToken).ConfigureAwait(false);
            }

            await RefreshScheduleAsync(cancellationToken).ConfigureAwait(false);
            var response = new DocumentRegisteredResponse(
                request.InstanceId,
                requestedTarget.StableTargetKey(),
                requestedTarget.Generation,
                request.AvailableAdapters);
            await connection.SendAsync(
                BridgeFrame.Create(
                    BridgeMessageKind.Response,
                    BridgeMessageTypes.DocumentRegistered,
                    response,
                    requestedTarget,
                    frame.MessageId),
                cancellationToken).ConfigureAwait(false);
            _events.Publish();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var code = exception is BridgeProtocolException protocol ? protocol.Code : "registration_rejected";
            await connection.SendAsync(
                BridgeFrame.Create(
                    BridgeMessageKind.Error,
                    "bridge.failure",
                    new BridgeFailure(code, exception.Message, Retryable: false),
                    requestedTarget,
                    frame.MessageId) with
                {
                    ErrorCode = code
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Follows a Save As rename through every store keyed by the path-derived docKey: the managed
    /// history folder moves from histories\&lt;oldKey&gt; to histories\&lt;newKey&gt; (continuity —
    /// no fork on the next launch) and the cached repository handle is dropped so GetHistory
    /// reopens at the new path; persisted session bindings (sessions.gh_doc) and frozen durable
    /// jobs (live_jobs.target_doc) are rewritten old→new. In-memory queue entries were already
    /// re-keyed under _connectionGate by the caller. Best-effort by design: a partial migration
    /// must never reject the registration itself (the target is live either way).
    /// </summary>
    private async Task MigrateRenamedDocumentKeyAsync(
        TargetState targetState,
        string oldDocKey,
        string newDocKey,
        CancellationToken cancellationToken)
    {
        await _historyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var oldRoot = Path.Combine(_dataRoot, "histories", oldDocKey);
            var newRoot = Path.Combine(_dataRoot, "histories", newDocKey);
            try
            {
                if (Directory.Exists(oldRoot) && !Directory.Exists(newRoot))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(newRoot)!);
                    Directory.Move(oldRoot, newRoot);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The rename itself stays valid; the doc re-baselines under the new key instead.
                _logger.LogWarning(
                    exception,
                    "Could not move managed history {OldRoot} to {NewRoot} after a Save As.",
                    oldRoot,
                    newRoot);
            }
            lock (targetState)
            {
                // Drop the cached repository so the next GetHistory reopens under the new docKey.
                targetState.History = null;
            }
        }
        finally
        {
            _historyGate.Release();
        }

        try
        {
            await _store.RemapGrasshopperDocAsync(oldDocKey, newDocKey, cancellationToken)
                .ConfigureAwait(false);
            await _jobStore.RemapTargetDocAsync(oldDocKey, newDocKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Could not remap persisted bindings from docKey {OldDocKey} to {NewDocKey}.",
                oldDocKey,
                newDocKey);
        }
        _events.Publish();
    }

    private void ValidateRegistration(DocumentRuntime target)
    {
        // Identity is the opaque ProjectId (derived on the plugin side from the stable runtime tuple:
        // Rhino process + RhinoDoc serial + GH DocumentID). File paths are mutable metadata and are NOT
        // gated here — a Save As / rename re-registers the SAME pair with updated paths and must be accepted
        // so the live binding survives. Stable-identity enforcement lives in StableTargetKey / one_target_only
        // and the document resolvers; the persistent data directory stays frozen to the launch-time paths.
        if (target.ProjectId != _options.ProjectId)
        {
            throw new BridgeProtocolException(
                "project_mismatch",
                $"Bridge project {target.ProjectId:D} does not match AgentHost project {_options.ProjectId:D}.");
        }
    }

    private void CloseTarget(BridgeFrame frame)
    {
        var target = frame.Target;
        if (target is null)
        {
            return;
        }
        var key = target.StableTargetKey();
        bool removed;
        lock (_connectionGate)
        {
            removed = _targets.Remove(key);
        }
        if (removed)
        {
            // Only calls addressed to the closed document fail; siblings keep running.
            FailPendingFor(key, new IOException("The bound document was closed."));
        }
        _events.Publish();
    }

    private void Disconnect(DocumentPipeConnection? connection, string reason)
    {
        lock (_connectionGate)
        {
            if (connection is null || ReferenceEquals(_connection, connection))
            {
                _connection = null;
                _targets.Clear();
            }
        }
        FailPending(new IOException(reason));
        _events.Publish();
    }

    private void CompletePending(BridgeFrame frame)
    {
        if (frame.CorrelationId is not { } correlationId ||
            !_pending.TryRemove(correlationId, out var pending))
        {
            _logger.LogWarning("Ignoring bridge response without a known correlation id.");
            return;
        }

        try
        {
            // Each pending call remembers the exact target it was sent for; a response stamped with
            // any other target (or generation) fails only that call — the former singleton guard
            // would misattribute responses once several documents share the pipe.
            DocumentTargetGuard.RequireCurrent(pending.ExpectedTarget, frame.Target!);
            if (frame.Kind == BridgeMessageKind.Error)
            {
                var failure = frame.DeserializePayload<BridgeFailure>();
                pending.Completion.TrySetException(new BridgeProtocolException(failure.Code, failure.Message));
            }
            else
            {
                pending.Completion.TrySetResult(frame);
            }
        }
        catch (Exception exception)
        {
            pending.Completion.TrySetException(exception);
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var pair in _pending.ToArray())
        {
            if (_pending.TryRemove(pair.Key, out var pending))
            {
                pending.Completion.TrySetException(exception);
            }
        }
    }

    private void FailPendingFor(string targetKey, Exception exception)
    {
        foreach (var pair in _pending.ToArray())
        {
            if (string.Equals(pair.Value.ExpectedTargetKey, targetKey, StringComparison.Ordinal) &&
                _pending.TryRemove(pair.Key, out var pending))
            {
                pending.Completion.TrySetException(exception);
            }
        }
    }

    private async Task<BridgeFrame> SendRequestAsync(
        DocumentRuntime target,
        string payloadType,
        object payload,
        CancellationToken cancellationToken)
    {
        DocumentPipeConnection connection;
        DocumentRuntime current;
        lock (_connectionGate)
        {
            connection = _connection is { IsConnected: true } active
                ? active
                : throw new InvalidOperationException("The Rhino/Grasshopper bridge is not connected.");
            // Stamp the freshest registered instance for this key (a re-registration may have
            // bumped Generation or renamed paths since the caller resolved its target).
            current = _targets.TryGetValue(target.StableTargetKey(), out var state)
                ? state.Target
                : throw new InvalidOperationException("No explicit document target is registered.");
        }

        var frame = BridgeFrame.Create(
            BridgeMessageKind.Request,
            payloadType,
            payload,
            current);
        var completion = new TaskCompletionSource<BridgeFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingBridgeRequest(completion, current, current.StableTargetKey());
        if (!_pending.TryAdd(frame.MessageId, pending))
        {
            throw new InvalidOperationException("Bridge request identifier collision.");
        }

        try
        {
            await connection.SendAsync(frame, cancellationToken).ConfigureAwait(false);
            return await completion.Task.WaitAsync(BridgeRequestTimeout, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(frame.MessageId, out _);
        }
    }

    private async Task<BridgeOperationResponse> SendOperationAsync(
        DocumentRuntime target,
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        var frame = await SendRequestAsync(
            target,
            BridgeMessageTypes.OperationRequest,
            request,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(frame.PayloadType, BridgeMessageTypes.OperationResponse, StringComparison.Ordinal))
        {
            throw new BridgeProtocolException(
                "operation_response",
                $"Expected operation response, received '{frame.PayloadType}'.");
        }
        var response = frame.DeserializePayload<BridgeOperationResponse>();
        if (!string.Equals(response.OperationId, request.OperationId, StringComparison.Ordinal))
        {
            throw new BridgeProtocolException(
                "operation_correlation",
                "Bridge operation response has the wrong operation id.");
        }
        return response;
    }

    private async Task<SnapshotEnvelope> CaptureSnapshotAsync(
        TargetState targetState,
        bool force,
        CancellationToken cancellationToken)
    {
        if (!force && targetState.Snapshot is { } existing &&
            DateTimeOffset.UtcNow - existing.State.CapturedAt < TimeSpan.FromMilliseconds(250))
        {
            return existing;
        }

        await targetState.SnapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && targetState.Snapshot is { } cached &&
                DateTimeOffset.UtcNow - cached.State.CapturedAt < TimeSpan.FromMilliseconds(250))
            {
                return cached;
            }

            RequireAdapter(targetState, BridgeAdapterOwner.CordycepsCanvas);
            var currentTarget = targetState.Target;
            var request = BridgeOperationRequest.Create(
                $"snapshot-{Guid.NewGuid():N}",
                BridgeAdapterOwner.CordycepsCanvas,
                "canvas.snapshot",
                BridgeOperationAccess.Read,
                targetState.Snapshot?.State.Revision ?? 0,
                new { });
            var response = await SendOperationAsync(currentTarget, request, cancellationToken)
                .ConfigureAwait(false);
            var canvas = response.Result.Deserialize<CanvasSnapshot>(BridgeProtocol.JsonOptions)
                ?? throw new BridgeProtocolException("snapshot_payload", "Canvas snapshot payload was null.");
            if (canvas.GrasshopperDocumentId != currentTarget.GrasshopperDocumentId)
            {
                throw new BridgeProtocolException(
                    "snapshot_target",
                    "Canvas snapshot belongs to a different Grasshopper document.");
            }

            var previous = targetState.Snapshot;
            var sameTarget = previous is not null &&
                string.Equals(previous.State.Target.Identity, currentTarget.Identity, StringComparison.Ordinal);
            var sameFingerprint = sameTarget &&
                string.Equals(
                    previous!.Canvas.DocumentFingerprint,
                    canvas.DocumentFingerprint,
                    StringComparison.Ordinal);
            var revision = previous is null || !sameTarget
                ? 1
                : sameFingerprint
                    ? previous.State.Revision
                    : checked(previous.State.Revision + 1);
            var state = new StateSnapshot(
                currentTarget.ProjectId,
                revision,
                GetHistory(targetState).ReadHead(),
                DateTimeOffset.UtcNow,
                currentTarget,
                BuildResources(currentTarget, canvas));
            var snapshotId = BuildSnapshotId(state, canvas.DocumentFingerprint);
            var envelope = new SnapshotEnvelope(snapshotId, state, canvas);
            targetState.Snapshot = envelope;
            if (!sameFingerprint)
            {
                _events.Publish();
            }
            return envelope;
        }
        finally
        {
            targetState.SnapshotGate.Release();
        }
    }

    private static IReadOnlyList<ResourceFingerprint> BuildResources(
        DocumentRuntime target,
        CanvasSnapshot canvas)
    {
        var resources = new List<ResourceFingerprint>
        {
            // The whole-document resource is addressed by the runtime Grasshopper DocumentID (an
            // in-memory scope), never by the now Rhino-scoped ProjectId, which would collide the
            // Document rows of sibling documents in the snapshot and the ledger.
            new(
                new ResourceAddress(ResourceKind.Document, target.GrasshopperDocumentId.ToString("D")),
                canvas.DocumentFingerprint)
        };
        foreach (var item in canvas.Objects)
        {
            var id = item.ObjectId.ToString("D");
            // Per-domain fingerprints: independent user edits must not invalidate each other's
            // expectations (moving a component cannot stale a pending value write). Empty domain
            // hashes fall back to the whole-object hash for older adapters/test fakes.
            var structureFingerprint = string.IsNullOrEmpty(item.StructureFingerprint)
                ? item.Fingerprint
                : item.StructureFingerprint;
            var layoutFingerprint = string.IsNullOrEmpty(item.LayoutFingerprint)
                ? item.Fingerprint
                : item.LayoutFingerprint;
            resources.Add(new ResourceFingerprint(
                new ResourceAddress(ResourceKind.GrasshopperComponent, id),
                structureFingerprint));
            resources.Add(new ResourceFingerprint(
                new ResourceAddress(ResourceKind.GrasshopperComponentLayout, id),
                layoutFingerprint));
            if (item.ValueJson is not null)
            {
                resources.Add(new ResourceFingerprint(
                    new ResourceAddress(ResourceKind.GrasshopperComponentValue, id),
                    string.IsNullOrEmpty(item.ValueFingerprint) ? item.Fingerprint : item.ValueFingerprint));
            }
        }
        foreach (var wire in canvas.Wires)
        {
            var id = FormattableString.Invariant(
                $"{wire.SourceObjectId:N}/{wire.SourceParameterId:N}>{wire.TargetObjectId:N}/{wire.TargetParameterId:N}");
            resources.Add(new ResourceFingerprint(
                new ResourceAddress(ResourceKind.GrasshopperWire, id),
                Sha256(id)));
        }
        foreach (var group in canvas.Groups)
        {
            var canonical = JsonSerializer.Serialize(group, BridgeProtocol.JsonOptions);
            resources.Add(new ResourceFingerprint(
                new ResourceAddress(ResourceKind.GrasshopperGroup, group.GroupId.ToString("D")),
                Sha256(canonical)));
        }
        return resources;
    }

    private async Task<SnapshotEnvelope> EnrichSnapshotForConflictValidationAsync(
        SnapshotEnvelope snapshot,
        ChangeSet changeSet,
        TargetState targetState,
        CancellationToken cancellationToken)
    {
        var expectations = changeSet.ReadSet.Concat(changeSet.WriteSet).Distinct().ToArray();
        var missing = expectations.Where(expectation =>
            !snapshot.State.Resources.Any(resource =>
                ExactDomainOverlaps(resource.Resource, expectation.Resource))).ToArray();
        var rhinoAbsenceChecks = missing
            .Where(expectation =>
                expectation.ExpectsAbsence &&
                expectation.Resource.Kind == ResourceKind.RhinoObject &&
                Guid.TryParse(expectation.Resource.Id, out _))
            .ToArray();
        var scoped = missing
            .Except(rhinoAbsenceChecks)
            .Select(expectation => (Expectation: expectation, Scope: InspectionScope(expectation.Resource)))
            .Where(item => item.Scope is not null)
            .ToArray();
        if (scoped.Length == 0 && rhinoAbsenceChecks.Length == 0)
        {
            return snapshot;
        }

        var inspections = await Task.WhenAll(scoped
            .Select(item => item.Scope!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(scope => ReadInspectionScopeAsync(targetState, scope, cancellationToken))).ConfigureAwait(false);
        var byScope = inspections.ToDictionary(item => item.Scope, StringComparer.OrdinalIgnoreCase);
        var resources = snapshot.State.Resources.ToList();
        foreach (var item in scoped)
        {
            var inspection = byScope[item.Scope!];
            if (!string.IsNullOrWhiteSpace(inspection.Fingerprint))
            {
                resources.Add(new ResourceFingerprint(
                    item.Expectation.Resource,
                    inspection.Fingerprint!));
            }
        }
        foreach (var expectation in rhinoAbsenceChecks)
        {
            var existing = await ReadRhinoObjectForAbsenceCheckAsync(
                targetState,
                expectation.Resource,
                cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                resources.Add(existing);
            }
        }
        return snapshot with { State = snapshot.State with { Resources = resources } };
    }

    private async Task<ResourceFingerprint?> ReadRhinoObjectForAbsenceCheckAsync(
        TargetState targetState,
        ResourceAddress resource,
        CancellationToken cancellationToken)
    {
        var objectId = Guid.Parse(resource.Id);
        RequireAdapter(targetState, BridgeAdapterOwner.CordycepsRhino);
        var request = new BridgeOperationRequest(
            $"absence-{Guid.NewGuid():N}",
            BridgeAdapterOwner.CordycepsRhino,
            "rhino.list",
            BridgeOperationAccess.Read,
            targetState.Snapshot?.State.Revision ?? 0,
            ExpectedFingerprint: null,
            WriterLeaseToken: null,
            JsonSerializer.SerializeToElement(
                new RhinoListObjectsRequest(Limit: 1, ObjectId: objectId),
                BridgeProtocol.JsonOptions));
        var response = await SendOperationAsync(targetState.Target, request, cancellationToken)
            .ConfigureAwait(false);
        var result = response.Result.Deserialize<RhinoSceneListResult>(BridgeProtocol.JsonOptions)
            ?? throw new BridgeProtocolException(
                "rhino_absence_payload",
                "Rhino absence check returned an empty list payload.");
        var existing = result.Objects.SingleOrDefault(item => item.ObjectId == objectId);
        return existing is null ? null : new ResourceFingerprint(resource, existing.Fingerprint);
    }

    private static string? InspectionScope(ResourceAddress resource) => resource.Kind switch
    {
        ResourceKind.GrasshopperComponentSource or
        ResourceKind.GrasshopperComponentIo or
        ResourceKind.GrasshopperComponentValue => Guid.TryParse(resource.Id, out var componentId)
            ? $"wireify:{componentId:D}"
            : null,
        ResourceKind.RhinoObject or
        ResourceKind.RhinoObjectGeometry or
        ResourceKind.RhinoObjectAttributes => Guid.TryParse(resource.Id, out var objectId)
            ? $"rhino:{objectId:D}"
            : null,
        _ => null
    };

    private async Task EnsureHistoryBaselineAsync(
        TargetState targetState,
        SnapshotEnvelope snapshot,
        CancellationToken cancellationToken)
    {
        await _historyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var history = GetHistory(targetState);
            if (history.IsInitialized)
            {
                var verification = history.Verify();
                if (!verification.IsValid)
                {
                    throw new InvalidOperationException(
                        $"Managed history is invalid: {string.Join("; ", verification.Problems)}");
                }
                return;
            }

            await history.InitializeBaselineAsync(
                new Dictionary<string, ReadOnlyMemory<byte>>
                {
                    ["state/snapshot.json"] = JsonSerializer.SerializeToUtf8Bytes(
                        snapshot,
                        BridgeProtocol.JsonOptions),
                    ["state/target.json"] = JsonSerializer.SerializeToUtf8Bytes(
                        snapshot.State.Target,
                        BridgeProtocol.JsonOptions)
                },
                snapshot.State.ProjectId,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _historyGate.Release();
        }
    }

    private async Task CommitHistoryAsync(
        LiveJobEntry entry,
        TargetState targetState,
        SnapshotEnvelope snapshot,
        CancellationToken cancellationToken)
    {
        await _historyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var history = GetHistory(targetState);
            var changeJson = JsonSerializer.Serialize(entry.Job.ChangeSet, BridgeProtocol.JsonOptions);
            var request = HistoryCommitRequest.Create(
                history.ReadHead(),
                new Dictionary<string, string>
                {
                    ["state/snapshot.json"] = JsonSerializer.Serialize(snapshot, BridgeProtocol.JsonOptions),
                    ["changes/latest.json"] = changeJson
                },
                new HistoryCommitMetadata(
                    checked((int)snapshot.State.Revision),
                    snapshot.State.ProjectId,
                    entry.Session.Id,
                    entry.Job.JobId,
                    snapshot.SnapshotId,
                    Sha256(changeJson),
                    entry.Session.ModelProfile,
                    entry.Summary));
            var result = await history.CommitAsync(request, cancellationToken).ConfigureAwait(false);
            var committedState = snapshot.State with { GitCommit = result.Head };
            targetState.Snapshot = snapshot with { State = committedState };
        }
        finally
        {
            _historyGate.Release();
        }
    }

    private async Task<IReadOnlyList<PreparedOperation>> PreflightDraftOperationsAsync(
        Guid sessionId,
        ChangeSet changeSet,
        CancellationToken cancellationToken)
    {
        var prepared = new List<PreparedOperation>(changeSet.Operations.Count);
        foreach (var operation in changeSet.Operations)
        {
            var bytes = await ReadOperationPayloadBytesAsync(
                sessionId,
                operation,
                allowReserved: false,
                cancellationToken).ConfigureAwait(false);
            prepared.Add(PrepareOperation(operation, bytes));
        }
        return prepared;
    }

    private async Task<IReadOnlyList<PreparedOperation>> PreflightFrozenOperationsAsync(
        LiveJobEntry entry,
        TargetState targetState,
        CancellationToken cancellationToken)
    {
        var operations = entry.Job.ChangeSet.Operations;
        var prepared = new List<PreparedOperation>(operations.Count);
        for (var index = 0; index < operations.Count; index++)
        {
            var operation = operations[index];
            var expectedRelative = ReservedArtifactStorage.JobRelativePath(
                entry.Job.JobId,
                index);
            var sessionRoot = Path.Combine(_artifactRoot, entry.Session.Id.ToString("N"));
            var actualPath = ConstrainedPath.Resolve(
                sessionRoot,
                operation.PayloadArtifact,
                "Frozen operation payload");
            var expectedPath = ConstrainedPath.Resolve(
                sessionRoot,
                expectedRelative,
                "Frozen operation payload");
            if (!string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Operation '{operation.OperationId}' does not reference its job-owned frozen payload.");
            }
            if (string.IsNullOrWhiteSpace(operation.PayloadSha256))
            {
                throw new InvalidDataException(
                    $"Operation '{operation.OperationId}' has no frozen payload digest.");
            }

            var bytes = await ReadOperationPayloadBytesAsync(
                entry.Session.Id,
                operation,
                allowReserved: true,
                cancellationToken).ConfigureAwait(false);
            var actualHash = Sha256(bytes);
            if (!string.Equals(actualHash, operation.PayloadSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Frozen payload for operation '{operation.OperationId}' failed its immutable digest check.");
            }
            prepared.Add(PrepareOperation(operation, bytes));
        }

        ValidateExpectationCoverage(
            entry.Job.ChangeSet,
            prepared,
            targetState.Target.GrasshopperDocumentId);
        foreach (var owner in prepared.Select(item => item.Owner).Distinct())
        {
            RequireAdapter(targetState, owner);
        }
        return prepared;
    }

    private async Task PreflightBridgePayloadsAsync(
        TargetState targetState,
        IReadOnlyList<PreparedOperation> prepared,
        long snapshotRevision,
        CancellationToken cancellationToken)
    {
        foreach (var item in prepared.Where(item =>
                     string.Equals(item.BridgeOperation, "rhino.upsert", StringComparison.Ordinal)))
        {
            var arguments = item.Arguments.Deserialize<UpsertRhinoObjectRequest>(BridgeProtocol.JsonOptions)
                ?? throw new InvalidOperationException(
                    $"Operation '{item.Operation.OperationId}' has an empty Rhino upsert payload.");
            var request = new BridgeOperationRequest(
                item.Operation.OperationId,
                BridgeAdapterOwner.CordycepsRhino,
                "rhino.validateUpsert",
                BridgeOperationAccess.Read,
                snapshotRevision,
                ExpectedFingerprint: null,
                WriterLeaseToken: null,
                item.Arguments.Clone());
            request.Validate();
            var response = await SendOperationAsync(targetState.Target, request, cancellationToken)
                .ConfigureAwait(false);
            var error = response.Diagnostics.FirstOrDefault(diagnostic =>
                diagnostic.Severity == BridgeDiagnosticSeverity.Error);
            if (response.Changed || error is not null)
            {
                throw new InvalidOperationException(
                    $"Rhino preflight for '{item.Operation.OperationId}' was not read-only and successful.");
            }
            var result = response.Result.Deserialize<RhinoUpsertValidationResult>(BridgeProtocol.JsonOptions)
                ?? throw new InvalidOperationException(
                    $"Rhino preflight for '{item.Operation.OperationId}' returned no validation result.");
            var expectedExisting = !string.IsNullOrWhiteSpace(arguments.ExpectedFingerprint);
            if (!result.IsValid ||
                !string.Equals(result.OperationId, item.Operation.OperationId, StringComparison.Ordinal) ||
                result.ObjectId != arguments.ObjectId ||
                !string.Equals(
                    result.ActualGeometryType,
                    arguments.GeometryType,
                    StringComparison.OrdinalIgnoreCase) ||
                result.ExistingObject != expectedExisting ||
                expectedExisting && !string.Equals(
                    result.ExistingFingerprint,
                    arguments.ExpectedFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Rhino preflight for '{item.Operation.OperationId}' did not match its frozen payload.");
            }
        }
    }

    // A setComponentIo schema is append-only: the adapter rejects a socket-count reduction with a
    // NotSupportedException at execute time, which — because the same ChangeSet's source write has
    // already landed — dead-ends the job in RecoveryRequired. Catch it here, BEFORE any write, by
    // comparing the requested socket counts against the component's live sockets in the pre-write
    // snapshot, so a removal is a clean deterministic failure with no partial state.
    private static void PreflightPythonSchemas(
        IReadOnlyList<PreparedOperation> prepared,
        SnapshotEnvelope before)
    {
        foreach (var item in prepared.Where(item =>
                     string.Equals(item.BridgeOperation, "python.setSchema", StringComparison.Ordinal)))
        {
            if (!item.Arguments.TryGetProperty("componentId", out var componentIdElement) ||
                !componentIdElement.TryGetGuid(out var componentId))
            {
                continue;
            }
            var component = before.Canvas.Objects.FirstOrDefault(obj => obj.ObjectId == componentId);
            if (component is null)
            {
                continue;
            }
            var requestedInputs = CountSchemaSockets(item.Arguments, "inputs");
            var requestedOutputs = CountSchemaSockets(item.Arguments, "outputs");
            if (requestedInputs < component.Inputs.Count || requestedOutputs < component.Outputs.Count)
            {
                throw new InvalidOperationException(
                    $"Operation '{item.Operation.OperationId}' would remove sockets from component " +
                    $"{componentId:D} (schema is append-only): it has {component.Inputs.Count} input(s) and " +
                    $"{component.Outputs.Count} output(s), but the request declares {requestedInputs} input(s) " +
                    $"and {requestedOutputs} output(s). List every existing socket in order, then appended " +
                    "ones; you may rename or retype existing sockets but not remove them.");
            }
        }
    }

    private static int CountSchemaSockets(JsonElement arguments, string property) =>
        arguments.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Array
            ? element.GetArrayLength()
            : 0;

    private async Task<byte[]> ReadOperationPayloadBytesAsync(
        Guid sessionId,
        TypedOperation operation,
        bool allowReserved,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operation.PayloadArtifact))
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' requires a JSON payload artifact.");
        }

        var sessionRoot = Path.Combine(_artifactRoot, sessionId.ToString("N"));
        var path = ConstrainedPath.Resolve(sessionRoot, operation.PayloadArtifact, "Operation payload");
        if (!allowReserved)
        {
            ReservedArtifactStorage.RejectUserPath(sessionRoot, path);
        }
        else if (!ReservedArtifactStorage.IsReservedPath(sessionRoot, path))
        {
            throw new InvalidDataException("An accepted operation payload escaped reserved storage.");
        }
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Operation payload artifact was not found.", operation.PayloadArtifact);
        }
        var info = new FileInfo(path);
        if (info.Length > MaximumArtifactBytes)
        {
            throw new InvalidOperationException("Operation payload artifact exceeds 2 MiB.");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.Length > MaximumArtifactBytes)
        {
            throw new InvalidOperationException("Operation payload artifact exceeds 2 MiB.");
        }
        return bytes;
    }

    private static PreparedOperation PrepareOperation(
        TypedOperation operation,
        byte[] frozenPayload)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(frozenPayload);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' payload is not valid JSON: {exception.Message}",
                exception);
        }
        using var parsedDocument = document;
        var payload = parsedDocument.RootElement;
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' payload must be a JSON object.");
        }
        var properties = payload.EnumerateObject().Select(item => item.Name).ToArray();
        if (properties.Length != 2 ||
            !properties.Contains("bridgeOperation", StringComparer.Ordinal) ||
            !properties.Contains("arguments", StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' payload must contain exactly bridgeOperation and arguments.");
        }
        if (!payload.TryGetProperty("arguments", out var arguments) ||
            arguments.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' payload arguments must be a JSON object.");
        }

        var owner = ResolveOwner(operation);
        var bridgeOperation = ResolveBridgeOperation(operation, payload);
        ValidateOperationArguments(operation, bridgeOperation, arguments);
        ValidateOperationResourceAlignment(operation, bridgeOperation, arguments);
        return new PreparedOperation(
            operation,
            owner,
            bridgeOperation,
            arguments.Clone(),
            frozenPayload,
            Sha256(frozenPayload));
    }

    private async Task<ChangeSet> FreezeOperationPayloadsAsync(
        Guid sessionId,
        Guid jobId,
        ChangeSet changeSet,
        IReadOnlyList<PreparedOperation> prepared,
        CancellationToken cancellationToken)
    {
        var sessionRoot = Path.Combine(_artifactRoot, sessionId.ToString("N"));
        Directory.CreateDirectory(sessionRoot);
        ConstrainedPath.RejectExistingReparsePoints(sessionRoot, sessionRoot, "Artifact");
        var jobsRoot = ConstrainedPath.Resolve(
            sessionRoot,
            Path.Combine(ReservedArtifactStorage.Namespace, "jobs"),
            "Reserved artifact");
        Directory.CreateDirectory(jobsRoot);
        ConstrainedPath.RejectExistingReparsePoints(sessionRoot, jobsRoot, "Reserved artifact");
        var finalRoot = ReservedArtifactStorage.JobRoot(sessionRoot, jobId);
        var stagingRoot = ConstrainedPath.Resolve(
            sessionRoot,
            Path.Combine(
                ReservedArtifactStorage.Namespace,
                "jobs",
                $".pending-{jobId:N}-{Guid.NewGuid():N}"),
            "Reserved artifact");
        if (Directory.Exists(finalRoot))
        {
            throw new InvalidOperationException($"Reserved payload storage for job '{jobId:D}' already exists.");
        }

        var frozen = new TypedOperation[prepared.Count];
        try
        {
            Directory.CreateDirectory(stagingRoot);
            File.WriteAllText(
                Path.Combine(stagingRoot, ".gptino-owned-reserved-job"),
                jobId.ToString("D"));
            var stagingOperations = Path.Combine(stagingRoot, "operations");
            Directory.CreateDirectory(stagingOperations);
            for (var index = 0; index < prepared.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagingPath = Path.Combine(stagingOperations, $"{index:D4}.json");
                await using (var stream = new FileStream(
                    stagingPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(prepared[index].FrozenPayload, cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                frozen[index] = prepared[index].Operation with
                {
                    PayloadArtifact = ReservedArtifactStorage.JobRelativePath(jobId, index)
                        .Replace('\\', '/'),
                    PayloadSha256 = prepared[index].PayloadSha256
                };
            }
            Directory.Move(stagingRoot, finalRoot);
        }
        catch (Exception primaryException)
        {
            if (Directory.Exists(stagingRoot))
            {
                try
                {
                    DeleteOwnedReservedJob(sessionRoot, stagingRoot);
                }
                catch (Exception cleanupException)
                {
                    throw new AggregateException(
                        "The reserved payload operation failed and its owned staging directory could not be removed safely.",
                        primaryException,
                        cleanupException);
                }
            }
            throw;
        }
        return changeSet with { Operations = frozen };
    }

    private void DeleteUnacceptedReservedJob(Guid sessionId, Guid jobId)
    {
        var sessionRoot = Path.Combine(_artifactRoot, sessionId.ToString("N"));
        if (!Directory.Exists(sessionRoot))
        {
            return;
        }
        var jobRoot = ReservedArtifactStorage.JobRoot(sessionRoot, jobId);
        if (Directory.Exists(jobRoot))
        {
            DeleteOwnedReservedJob(sessionRoot, jobRoot);
        }
    }

    private static void DeleteOwnedReservedJob(string sessionRoot, string candidate)
    {
        var safePath = ConstrainedPath.Resolve(
            sessionRoot,
            Path.GetRelativePath(sessionRoot, candidate),
            "Reserved artifact cleanup");
        ConstrainedPath.RejectExistingReparsePoints(
            sessionRoot,
            safePath,
            "Reserved artifact cleanup");
        if (!File.Exists(Path.Combine(safePath, ".gptino-owned-reserved-job")))
        {
            throw new InvalidOperationException(
                "Refusing to remove an unmarked reserved artifact directory.");
        }
        Directory.Delete(safePath, recursive: true);
    }

    private static BridgeAdapterOwner ResolveOwner(TypedOperation operation)
    {
        var expected = operation.Kind switch
        {
            OperationKind.UpdatePythonSource or OperationKind.SetComponentIo or
                OperationKind.ConvertSocket or OperationKind.ExecutePython or
                OperationKind.ReadRuntimeMessages => AdapterOwner.Wireify,
            _ when IsRhinoOperation(operation.Kind) => AdapterOwner.RhinoBridge,
            OperationKind.Read => operation.Owner,
            _ => AdapterOwner.Cordyceps
        };
        if (operation.Owner != expected)
        {
            throw new InvalidOperationException(
                $"Operation kind '{operation.Kind}' belongs to owner '{expected}', not '{operation.Owner}'.");
        }
        return operation.Owner switch
        {
            AdapterOwner.Wireify => BridgeAdapterOwner.Wireify,
            AdapterOwner.Cordyceps => BridgeAdapterOwner.CordycepsCanvas,
            AdapterOwner.RhinoBridge => BridgeAdapterOwner.CordycepsRhino,
            _ => throw new InvalidOperationException($"Unsupported adapter owner '{operation.Owner}'.")
        };
    }

    private static bool IsRhinoOperation(OperationKind kind) => kind is
        OperationKind.CreateRhinoPrimitive or OperationKind.TransformRhinoObject or
        OperationKind.CreateRhinoObject or OperationKind.ModifyRhinoObject or
        OperationKind.DeleteRhinoObject or OperationKind.BakeGeometry or
        OperationKind.UpdateRhinoAttributes or OperationKind.UpdateRhinoLayer;

    private static string ResolveBridgeOperation(TypedOperation operation, JsonElement payload)
    {
        var inferred = operation.Kind switch
        {
            OperationKind.MoveComponent or OperationKind.SetLayout => "canvas.move",
            OperationKind.SetValue => "canvas.setNumberSlider",
            OperationKind.ConnectWire or OperationKind.DisconnectWire => "canvas.setWire",
            OperationKind.CreateComponent => "canvas.create",
            OperationKind.DeleteComponent => "canvas.delete",
            OperationKind.SetGroup => "canvas.setGroup",
            OperationKind.UpdatePythonSource => "python.setSource",
            OperationKind.SetComponentIo => "python.setSchema",
            OperationKind.ConvertSocket => "python.setTyping",
            OperationKind.ExecutePython => "python.execute",
            OperationKind.ReadRuntimeMessages => "python.runtimeMessages",
            OperationKind.CreateRhinoPrimitive => "rhino.createPrimitive",
            OperationKind.TransformRhinoObject => "rhino.transform",
            OperationKind.CreateRhinoObject or OperationKind.ModifyRhinoObject or
                OperationKind.BakeGeometry or OperationKind.UpdateRhinoAttributes => "rhino.upsert",
            OperationKind.DeleteRhinoObject => "rhino.delete",
            OperationKind.UpdateRhinoLayer => throw new InvalidOperationException(
                "UpdateRhinoLayer is reserved until deterministic layer inspection is available."),
            OperationKind.Read when operation.Owner == AdapterOwner.Wireify => "python.inspect",
            OperationKind.Read when operation.Owner == AdapterOwner.RhinoBridge => "rhino.inspect",
            OperationKind.Read => "canvas.inspect",
            _ => throw new InvalidOperationException(
                $"Operation kind '{operation.Kind}' has no safe bridge mapping.")
        };
        if (!payload.TryGetProperty("bridgeOperation", out var explicitElement) ||
            explicitElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(explicitElement.GetString()))
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' requires an explicit bridgeOperation.");
        }
        var explicitOperation = explicitElement.GetString();
        if (!string.Equals(explicitOperation, inferred, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Payload bridgeOperation '{explicitOperation}' does not match typed operation '{inferred}'.");
        }
        return inferred;
    }

    private static void ValidateOperationArguments(
        TypedOperation operation,
        string bridgeOperation,
        JsonElement arguments)
    {
        var required = bridgeOperation switch
        {
            "canvas.move" => new[] { "operationId", "pivots", "expectedFingerprints" },
            "canvas.setNumberSlider" => new[]
            {
                "operationId", "objectId", "expectedFingerprint", "value", "minimum", "maximum",
                "decimalPlaces"
            },
            "canvas.setWire" => new[] { "operationId", "wire", "action", "rejectCycles" },
            "canvas.create" => new[] { "operationId", "objectId", "componentTypeId", "pivot" },
            "canvas.delete" => new[] { "operationId", "objectId", "expectedFingerprint" },
            "canvas.setGroup" => new[] { "operationId", "groupId", "name", "objectIds", "argbColor" },
            "python.setSource" => new[]
            {
                "operationId", "componentId", "expectedSourceSha256", "source", "runtime", "expireSolution"
            },
            "python.setSchema" => new[]
            {
                "operationId", "componentId", "inputs", "outputs", "preserveIncidentWires"
            },
            "python.setTyping" => new[]
            {
                "operationId", "componentId", "inputParameterId", "typeHint", "access"
            },
            "python.execute" => new[]
            {
                "operationId", "componentId", "expireUpstream", "recomputeDocument"
            },
            "python.runtimeMessages" or "python.inspect" => new[] { "componentId" },
            "canvas.inspect" or "rhino.inspect" => new[] { "objectId" },
            "rhino.createPrimitive" => new[]
            {
                "operationId", "objectId", "logicalEntityId", "kind"
            },
            "rhino.transform" => new[]
            {
                "operationId", "objectId", "expectedFingerprint", "matrix"
            },
            "rhino.upsert" => new[]
            {
                "operationId", "objectId", "logicalEntityId", "geometryType", "geometryJson",
                "attributesJson", "expectedFingerprint"
            },
            "rhino.delete" => new[] { "operationId", "objectId", "expectedFingerprint" },
            _ => throw new InvalidOperationException(
                $"Bridge operation '{bridgeOperation}' is not supported by the preflight validator.")
        };
        foreach (var property in required)
        {
            var nullableCreateFingerprint =
                property == "expectedFingerprint" &&
                operation.Kind is OperationKind.CreateRhinoObject or OperationKind.BakeGeometry;
            if (!arguments.TryGetProperty(property, out var value) ||
                (value.ValueKind == JsonValueKind.Null && !nullableCreateFingerprint))
            {
                throw new InvalidOperationException(
                    $"Operation '{operation.OperationId}' payload is missing required argument '{property}'.");
            }
        }

        if (bridgeOperation == "rhino.upsert")
        {
            var expected = arguments.GetProperty("expectedFingerprint");
            var isCreate = operation.Kind is OperationKind.CreateRhinoObject or OperationKind.BakeGeometry;
            if (isCreate != (expected.ValueKind == JsonValueKind.Null))
            {
                throw new InvalidOperationException(
                    $"Operation '{operation.OperationId}' must use a null expectedFingerprint only for an exact Rhino create.");
            }
        }

        if (OperationSemantics.IsWrite(operation.Kind))
        {
            var payloadOperationId = RequireArgumentString(arguments, "operationId", operation.OperationId);
            if (!string.Equals(payloadOperationId, operation.OperationId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Typed operation id '{operation.OperationId}' does not match payload operationId '{payloadOperationId}'.");
            }
        }
        else if (arguments.TryGetProperty("operationId", out var optionalId) &&
            optionalId.ValueKind == JsonValueKind.String &&
            !string.Equals(optionalId.GetString(), operation.OperationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Typed operation id '{operation.OperationId}' does not match payload operationId '{optionalId.GetString()}'.");
        }

        foreach (var guidProperty in GuidArguments(bridgeOperation))
        {
            _ = RequireArgumentGuid(arguments, guidProperty, operation.OperationId);
        }
        ValidateDeserializableArguments(operation, bridgeOperation, arguments);
    }

    private static void ValidateDeserializableArguments(
        TypedOperation operation,
        string bridgeOperation,
        JsonElement arguments)
    {
        try
        {
            switch (bridgeOperation)
            {
                case "canvas.move":
                    ValidateCanvasPivotsShape(
                        arguments.GetProperty("pivots"),
                        operation.OperationId);
                    ValidateMoveArguments(
                        DeserializeArguments<MoveCanvasObjectsRequest>(arguments, operation.OperationId));
                    return;
                case "canvas.setNumberSlider":
                    var slider = DeserializeArguments<SetNumberSliderValueRequest>(
                        arguments,
                        operation.OperationId);
                    if (slider.ObjectId == Guid.Empty ||
                        string.IsNullOrWhiteSpace(slider.ExpectedFingerprint) ||
                        slider.Minimum >= slider.Maximum || slider.Value < slider.Minimum ||
                        slider.Value > slider.Maximum || slider.DecimalPlaces is < 0 or > 12 ||
                        decimal.Round(slider.Value, slider.DecimalPlaces) != slider.Value ||
                        decimal.Round(slider.Minimum, slider.DecimalPlaces) != slider.Minimum ||
                        decimal.Round(slider.Maximum, slider.DecimalPlaces) != slider.Maximum)
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' has an invalid Number Slider payload.");
                    }
                    return;
                case "canvas.setWire":
                    ValidateWireArguments(
                        DeserializeArguments<SetWireRequest>(arguments, operation.OperationId));
                    return;
                case "canvas.create":
                    RequireOnlyProperties(
                        arguments.GetProperty("pivot"),
                        operation.OperationId,
                        "x", "y");
                    var create = DeserializeArguments<CreateCanvasObjectRequest>(arguments, operation.OperationId);
                    if (create.ObjectId == Guid.Empty || create.ComponentTypeId == Guid.Empty ||
                        !float.IsFinite(create.Pivot.X) || !float.IsFinite(create.Pivot.Y))
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' has an invalid canvas create payload.");
                    }
                    return;
                case "canvas.delete":
                    var delete = DeserializeArguments<DeleteCanvasObjectRequest>(arguments, operation.OperationId);
                    if (delete.ObjectId == Guid.Empty || string.IsNullOrWhiteSpace(delete.ExpectedFingerprint))
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' has an invalid canvas delete payload.");
                    }
                    return;
                case "canvas.setGroup":
                    var group = DeserializeArguments<SetGroupRequest>(arguments, operation.OperationId);
                    if (group.GroupId == Guid.Empty || string.IsNullOrWhiteSpace(group.Name) ||
                        group.ObjectIds is null || group.ObjectIds.Count == 0 ||
                        group.ObjectIds.Any(id => id == Guid.Empty) ||
                        group.ObjectIds.Distinct().Count() != group.ObjectIds.Count)
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' has an invalid canvas group payload.");
                    }
                    return;
                case "python.setSource":
                    var source = DeserializeArguments<SetPythonSourceRequest>(arguments, operation.OperationId);
                    if (source.ComponentId == Guid.Empty ||
                        string.IsNullOrWhiteSpace(source.ExpectedSourceSha256) || source.Source is null)
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' has an invalid Python source payload.");
                    }
                    return;
                case "python.setSchema":
                    ValidatePythonSchema(
                        DeserializeArguments<SetParameterSchemaRequest>(arguments, operation.OperationId),
                        operation.OperationId);
                    return;
                case "python.setTyping":
                    var typing = DeserializeArguments<SetInputTypingRequest>(arguments, operation.OperationId);
                    if (typing.ComponentId == Guid.Empty || typing.InputParameterId == Guid.Empty ||
                        string.IsNullOrWhiteSpace(typing.TypeHint))
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' has an invalid Python typing payload.");
                    }
                    return;
                case "python.execute":
                    if (DeserializeArguments<ExecutePythonComponentRequest>(arguments, operation.OperationId)
                        .ComponentId == Guid.Empty)
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' requires a Python component UUID.");
                    }
                    return;
                case "python.runtimeMessages":
                case "python.inspect":
                    RequireOnlyProperties(arguments, operation.OperationId, "componentId");
                    return;
                case "canvas.inspect":
                case "rhino.inspect":
                    RequireOnlyProperties(arguments, operation.OperationId, "objectId");
                    return;
                case "rhino.createPrimitive":
                    var primitive = DeserializeArguments<CreateRhinoPrimitiveRequest>(
                        arguments,
                        operation.OperationId);
                    ValidatePrimitiveCoordinateShapes(primitive, arguments, operation.OperationId);
                    ValidatePrimitiveArguments(primitive, operation.OperationId);
                    return;
                case "rhino.transform":
                    RequireOnlyProperties(
                        arguments.GetProperty("matrix"),
                        operation.OperationId,
                        "m00", "m01", "m02", "m03", "m10", "m11", "m12", "m13",
                        "m20", "m21", "m22", "m23", "m30", "m31", "m32", "m33");
                    ValidateTransformArguments(
                        DeserializeArguments<TransformRhinoObjectRequest>(arguments, operation.OperationId),
                        operation.OperationId);
                    return;
                case "rhino.upsert":
                    ValidateUpsertArguments(
                        DeserializeArguments<UpsertRhinoObjectRequest>(arguments, operation.OperationId),
                        operation.OperationId);
                    return;
                case "rhino.delete":
                    var rhinoDelete = DeserializeArguments<DeleteRhinoObjectRequest>(arguments, operation.OperationId);
                    if (rhinoDelete.ObjectId == Guid.Empty ||
                        string.IsNullOrWhiteSpace(rhinoDelete.ExpectedFingerprint))
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' has an invalid Rhino delete payload.");
                    }
                    return;
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' payload does not match the typed bridge schema: " +
                exception.Message,
                exception);
        }
        catch (KeyNotFoundException exception)
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' payload is missing a required nested value.",
                exception);
        }
    }

    private static T DeserializeArguments<T>(JsonElement arguments, string operationId) =>
        arguments.Deserialize<T>(BridgeProtocol.JsonOptions)
        ?? throw new InvalidOperationException(
            $"Operation '{operationId}' payload deserialized to an empty request.");

    private static void ValidateMoveArguments(MoveCanvasObjectsRequest request)
    {
        if (request.Pivots is null || request.ExpectedFingerprints is null ||
            request.Pivots.Count == 0 ||
            !request.Pivots.Keys.ToHashSet().SetEquals(request.ExpectedFingerprints.Keys) ||
            request.Pivots.Any(item => item.Key == Guid.Empty ||
                !float.IsFinite(item.Value.X) || !float.IsFinite(item.Value.Y)) ||
            request.ExpectedFingerprints.Any(item =>
                item.Key == Guid.Empty || string.IsNullOrWhiteSpace(item.Value)))
        {
            throw new InvalidOperationException(
                $"Operation '{request.OperationId}' has invalid canvas move targets or fingerprints.");
        }
    }

    private static void ValidateCanvasPivotsShape(JsonElement pivots, string operationId)
    {
        if (pivots.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' pivots must be a component-to-point object.");
        }
        foreach (var pivot in pivots.EnumerateObject())
        {
            RequireOnlyProperties(pivot.Value, operationId, "x", "y");
        }
    }

    private static void ValidateWireArguments(SetWireRequest request)
    {
        if (request.Wire is null ||
            request.Wire.SourceObjectId == Guid.Empty || request.Wire.SourceParameterId == Guid.Empty ||
            request.Wire.TargetObjectId == Guid.Empty || request.Wire.TargetParameterId == Guid.Empty ||
            (request.Wire.SourceObjectId == request.Wire.TargetObjectId &&
             request.Wire.SourceParameterId == request.Wire.TargetParameterId))
        {
            throw new InvalidOperationException(
                $"Operation '{request.OperationId}' has invalid wire endpoints.");
        }
    }

    private static void ValidatePythonSchema(SetParameterSchemaRequest request, string operationId)
    {
        if (request.ComponentId == Guid.Empty || request.Inputs is null || request.Outputs is null)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' has an invalid Python parameter schema.");
        }
        // The model only owns each socket's name/access/typeHint. ParameterId, nickName, and
        // typeHint are server-normalized by the adapter (placeholder ids generated, nickName
        // defaults to name, typeHint defaults to object), so only names are validated here — and
        // the error names the offender instead of a blanket rejection.
        var parameters = request.Inputs.Concat(request.Outputs).ToArray();
        if (parameters.Any(parameter => parameter is null || string.IsNullOrWhiteSpace(parameter.Name)))
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' has a Python socket without a name; every input and " +
                "output needs a script variable name.");
        }
        var duplicateNames = parameters
            .GroupBy(parameter => parameter.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateNames.Length > 0)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' declares duplicate Python socket names: " +
                $"{string.Join(", ", duplicateNames)}. Socket variable names must be unique " +
                "across inputs and outputs.");
        }
        var explicitIds = parameters
            .Where(parameter => parameter.ParameterId != Guid.Empty)
            .Select(parameter => parameter.ParameterId)
            .ToArray();
        if (explicitIds.Distinct().Count() != explicitIds.Length)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' declares duplicate Python socket ids; omit " +
                "parameterId entirely (the server assigns and reconciles socket ids).");
        }
    }

    private static void ValidatePrimitiveArguments(
        CreateRhinoPrimitiveRequest request,
        string operationId)
    {
        if (request.ObjectId == Guid.Empty || string.IsNullOrWhiteSpace(request.LogicalEntityId))
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' has an invalid Rhino primitive identity.");
        }
        var definitions = new object?[]
        {
            request.Point, request.Line, request.Polyline,
            request.Circle, request.Box, request.Sphere
        };
        if (definitions.Count(item => item is not null) != 1 ||
            request.Kind switch
            {
                RhinoPrimitiveKind.Point => request.Point is null,
                RhinoPrimitiveKind.Line => request.Line is null,
                RhinoPrimitiveKind.Polyline => request.Polyline is null,
                RhinoPrimitiveKind.Circle => request.Circle is null,
                RhinoPrimitiveKind.Box => request.Box is null,
                RhinoPrimitiveKind.Sphere => request.Sphere is null,
                _ => true
            })
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' must supply exactly one primitive definition matching kind.");
        }
        var points = request.Kind switch
        {
            RhinoPrimitiveKind.Point => new[] { request.Point!.Location },
            RhinoPrimitiveKind.Line => new[] { request.Line!.From, request.Line.To },
            RhinoPrimitiveKind.Polyline => request.Polyline!.Vertices?.ToArray() ?? [],
            RhinoPrimitiveKind.Circle => new[] { request.Circle!.Center },
            RhinoPrimitiveKind.Box => new[] { request.Box!.Minimum, request.Box.Maximum },
            RhinoPrimitiveKind.Sphere => new[] { request.Sphere!.Center },
            _ => []
        };
        if (points.Length == 0 || points.Any(point => point is null ||
                !double.IsFinite(point.X) || !double.IsFinite(point.Y) || !double.IsFinite(point.Z)) ||
            request.Polyline is { } polyline &&
                (polyline.Vertices is null || polyline.Vertices.Count < (polyline.Closed ? 3 : 2) ||
                 polyline.Vertices.Count > 10_000) ||
            request.Circle is { } circle &&
                (!double.IsFinite(circle.Radius) || circle.Radius <= 0 || circle.Normal is null ||
                 !double.IsFinite(circle.Normal.X) || !double.IsFinite(circle.Normal.Y) ||
                 !double.IsFinite(circle.Normal.Z) ||
                 (circle.Normal.X == 0 && circle.Normal.Y == 0 && circle.Normal.Z == 0)) ||
            request.Sphere is { } sphere &&
                (!double.IsFinite(sphere.Radius) || sphere.Radius <= 0) ||
            request.Box is { } box &&
                (box.Maximum.X <= box.Minimum.X || box.Maximum.Y <= box.Minimum.Y ||
                 box.Maximum.Z <= box.Minimum.Z))
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' has invalid Rhino primitive geometry.");
        }
    }

    private static void ValidatePrimitiveCoordinateShapes(
        CreateRhinoPrimitiveRequest request,
        JsonElement arguments,
        string operationId)
    {
        switch (request.Kind)
        {
            case RhinoPrimitiveKind.Point:
                RequirePoint3(
                    arguments.GetProperty("point").GetProperty("location"),
                    operationId);
                return;
            case RhinoPrimitiveKind.Line:
                var line = arguments.GetProperty("line");
                RequirePoint3(line.GetProperty("from"), operationId);
                RequirePoint3(line.GetProperty("to"), operationId);
                return;
            case RhinoPrimitiveKind.Polyline:
                var vertices = arguments.GetProperty("polyline").GetProperty("vertices");
                if (vertices.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException(
                        $"Operation '{operationId}' polyline vertices must be an array.");
                }
                foreach (var vertex in vertices.EnumerateArray())
                {
                    RequirePoint3(vertex, operationId);
                }
                return;
            case RhinoPrimitiveKind.Circle:
                var circle = arguments.GetProperty("circle");
                RequirePoint3(circle.GetProperty("center"), operationId);
                RequirePoint3(circle.GetProperty("normal"), operationId);
                return;
            case RhinoPrimitiveKind.Box:
                var box = arguments.GetProperty("box");
                RequirePoint3(box.GetProperty("minimum"), operationId);
                RequirePoint3(box.GetProperty("maximum"), operationId);
                return;
            case RhinoPrimitiveKind.Sphere:
                RequirePoint3(
                    arguments.GetProperty("sphere").GetProperty("center"),
                    operationId);
                return;
            default:
                throw new InvalidOperationException(
                    $"Operation '{operationId}' has an unsupported Rhino primitive kind.");
        }
    }

    private static void RequirePoint3(JsonElement value, string operationId) =>
        RequireOnlyProperties(value, operationId, "x", "y", "z");

    private static void ValidateTransformArguments(
        TransformRhinoObjectRequest request,
        string operationId)
    {
        if (request.ObjectId == Guid.Empty || string.IsNullOrWhiteSpace(request.ExpectedFingerprint) ||
            request.Matrix is null)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' has an invalid Rhino transform payload.");
        }
        var matrix = request.Matrix;
        var values = new[]
        {
            matrix.M00, matrix.M01, matrix.M02, matrix.M03,
            matrix.M10, matrix.M11, matrix.M12, matrix.M13,
            matrix.M20, matrix.M21, matrix.M22, matrix.M23,
            matrix.M30, matrix.M31, matrix.M32, matrix.M33
        };
        if (values.Any(value => !double.IsFinite(value)) ||
            matrix.M30 != 0 || matrix.M31 != 0 || matrix.M32 != 0 || matrix.M33 != 1)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' matrix must be a finite affine 4x4 transform.");
        }
    }

    private static void ValidateUpsertArguments(UpsertRhinoObjectRequest request, string operationId)
    {
        if (request.ObjectId == Guid.Empty || string.IsNullOrWhiteSpace(request.LogicalEntityId) ||
            string.IsNullOrWhiteSpace(request.GeometryType) || string.IsNullOrWhiteSpace(request.GeometryJson) ||
            request.AttributesJson is null)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' has an invalid Rhino upsert payload.");
        }
        try
        {
            using var geometry = JsonDocument.Parse(request.GeometryJson);
            if (!string.IsNullOrWhiteSpace(request.AttributesJson))
            {
                using var attributes = JsonDocument.Parse(request.AttributesJson);
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' contains malformed Rhino JSON.",
                exception);
        }
    }

    private static void RequireOnlyProperties(
        JsonElement value,
        string operationId,
        params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            !value.EnumerateObject().Select(item => item.Name)
                .OrderBy(item => item, StringComparer.Ordinal)
                .SequenceEqual(names.OrderBy(item => item, StringComparer.Ordinal), StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' payload has missing or unsupported properties.");
        }
    }

    private static void ValidateOperationResourceAlignment(
        TypedOperation operation,
        string bridgeOperation,
        JsonElement arguments)
    {
        switch (bridgeOperation)
        {
            case "canvas.setNumberSlider":
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "objectId", operation.OperationId),
                    write: true,
                    ResourceKind.GrasshopperComponentValue);
                return;

            case "canvas.move":
                var pivotIds = ReadGuidPropertyNames(
                    arguments.GetProperty("pivots"),
                    operation.OperationId,
                    "pivots");
                var fingerprintIds = ReadGuidPropertyNames(
                    arguments.GetProperty("expectedFingerprints"),
                    operation.OperationId,
                    "expectedFingerprints");
                if (!pivotIds.SetEquals(fingerprintIds))
                {
                    throw new InvalidOperationException(
                        $"Operation '{operation.OperationId}' pivots and expectedFingerprints target different components.");
                }
                RequireExactDeclaredGuidTargets(
                    operation,
                    pivotIds,
                    write: true,
                    ResourceKind.GrasshopperComponentLayout);
                return;

            case "canvas.setWire":
                var wire = arguments.GetProperty("wire");
                var sourceObject = RequireArgumentGuid(wire, "sourceObjectId", operation.OperationId);
                var sourceParameter = RequireArgumentGuid(wire, "sourceParameterId", operation.OperationId);
                var targetObject = RequireArgumentGuid(wire, "targetObjectId", operation.OperationId);
                var targetParameter = RequireArgumentGuid(wire, "targetParameterId", operation.OperationId);
                var wireId = FormattableString.Invariant(
                    $"{sourceObject:N}/{sourceParameter:N}>{targetObject:N}/{targetParameter:N}");
                var expectedAction = operation.Kind == OperationKind.ConnectWire ? "connect" : "disconnect";
                if (!string.Equals(
                        RequireArgumentString(arguments, "action", operation.OperationId),
                        expectedAction,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Operation '{operation.OperationId}' wire action does not match typed kind '{operation.Kind}'.");
                }
                if (operation.Kind == OperationKind.ConnectWire &&
                    (!arguments.TryGetProperty("rejectCycles", out var rejectCycles) ||
                     rejectCycles.ValueKind != JsonValueKind.True))
                {
                    throw new InvalidOperationException(
                        $"Operation '{operation.OperationId}' must reject wire cycles.");
                }
                RequireExactDeclaredStringTarget(
                    operation,
                    wireId,
                    write: true,
                    ResourceKind.GrasshopperWire);
                return;

            case "canvas.create":
            case "canvas.delete":
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "objectId", operation.OperationId),
                    write: true,
                    ResourceKind.GrasshopperComponent);
                return;

            case "canvas.setGroup":
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "groupId", operation.OperationId),
                    write: true,
                    ResourceKind.GrasshopperGroup);
                return;

            case "python.setSource":
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "componentId", operation.OperationId),
                    write: true,
                    ResourceKind.GrasshopperComponentSource);
                return;
            case "python.setSchema":
            case "python.setTyping":
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "componentId", operation.OperationId),
                    write: true,
                    ResourceKind.GrasshopperComponentIo);
                return;
            case "python.execute":
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "componentId", operation.OperationId),
                    write: true,
                    ResourceKind.GrasshopperComponentValue);
                return;
            case "python.runtimeMessages":
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "componentId", operation.OperationId),
                    write: false,
                    ResourceKind.GrasshopperComponentValue);
                return;
            case "python.inspect":
                RequireSingleDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "componentId", operation.OperationId),
                    write: false,
                    ResourceKind.GrasshopperComponent,
                    ResourceKind.GrasshopperComponentSource,
                    ResourceKind.GrasshopperComponentIo,
                    ResourceKind.GrasshopperComponentValue);
                return;

            case "canvas.inspect":
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "objectId", operation.OperationId),
                    write: false,
                    ResourceKind.GrasshopperComponent);
                return;

            case "rhino.inspect":
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "objectId", operation.OperationId),
                    write: false,
                    ResourceKind.RhinoObject);
                return;

            case "rhino.createPrimitive":
            case "rhino.transform":
            case "rhino.upsert":
            case "rhino.delete":
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "objectId", operation.OperationId),
                    write: true,
                    ResourceKind.RhinoObject);
                return;
        }
    }

    private static HashSet<Guid> ReadGuidPropertyNames(
        JsonElement value,
        string operationId,
        string property)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' argument '{property}' must be an object keyed by component UUID.");
        }
        HashSet<Guid> result = [];
        foreach (var item in value.EnumerateObject())
        {
            if (!Guid.TryParse(item.Name, out var id) || id == Guid.Empty || !result.Add(id))
            {
                throw new InvalidOperationException(
                    $"Operation '{operationId}' argument '{property}' contains an invalid or duplicate UUID key.");
            }
        }
        if (result.Count == 0)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' argument '{property}' cannot be empty.");
        }
        return result;
    }

    private static void RequireExactDeclaredGuidTarget(
        TypedOperation operation,
        Guid target,
        bool write,
        ResourceKind kind) =>
        RequireExactDeclaredGuidTargets(operation, new HashSet<Guid> { target }, write, kind);

    private static void RequireExactDeclaredGuidTargets(
        TypedOperation operation,
        IReadOnlySet<Guid> targets,
        bool write,
        ResourceKind kind)
    {
        var declared = (write ? operation.Writes : operation.Reads)
            .ToArray();
        if (declared.Length != targets.Count ||
            declared.Any(resource =>
                resource.Kind != kind ||
                resource.Field != "*" ||
                !Guid.TryParse(resource.Id, out var id) ||
                !string.Equals(resource.Id, id.ToString("D"), StringComparison.Ordinal) ||
                !targets.Contains(id)) ||
            targets.Any(target => !declared.Any(resource =>
                Guid.TryParse(resource.Id, out var id) && id == target)))
        {
            var expected = string.Join(", ", targets.Select(id => $"{kind} id='{id:D}' field='*'"));
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' payload targets do not match its declared " +
                $"{(write ? "write" : "read")} resources. Declare exactly: {expected}.");
        }
    }

    private static void RequireSingleDeclaredGuidTarget(
        TypedOperation operation,
        Guid target,
        bool write,
        params ResourceKind[] allowedKinds)
    {
        var declared = write ? operation.Writes : operation.Reads;
        if (declared.Count != 1 ||
            !allowedKinds.Contains(declared[0].Kind) ||
            declared[0].Field != "*" ||
            !string.Equals(declared[0].Id, target.ToString("D"), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' payload target does not match its declared " +
                $"{(write ? "write" : "read")} resource. Declare exactly one {allowedKinds[0]} resource with " +
                $"id='{target:D}' and field='*'.");
        }
    }

    private static void RequireExactDeclaredStringTarget(
        TypedOperation operation,
        string target,
        bool write,
        ResourceKind kind)
    {
        var declared = (write ? operation.Writes : operation.Reads)
            .ToArray();
        if (declared.Length != 1 ||
            declared[0].Kind != kind ||
            declared[0].Field != "*" ||
            !string.Equals(declared[0].Id, target, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' payload target does not match its declared " +
                $"{(write ? "write" : "read")} resource. Declare exactly one {kind} resource with " +
                $"id='{target}' and field='*' (this exact string, derived from the payload).");
        }
    }

    private static IReadOnlyList<string> GuidArguments(string bridgeOperation) => bridgeOperation switch
    {
        "canvas.create" => ["objectId", "componentTypeId"],
        "canvas.delete" => ["objectId"],
        "canvas.setNumberSlider" => ["objectId"],
        "canvas.setGroup" => ["groupId"],
        "python.setSource" or "python.setSchema" or "python.execute" or
            "python.runtimeMessages" or "python.inspect" => ["componentId"],
        "python.setTyping" => ["componentId", "inputParameterId"],
        "canvas.inspect" or "rhino.inspect" or "rhino.createPrimitive" or
            "rhino.transform" or "rhino.upsert" or "rhino.delete" => ["objectId"],
        _ => Array.Empty<string>()
    };

    private static string RequireArgumentString(
        JsonElement arguments,
        string property,
        string operationId)
    {
        if (!arguments.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' argument '{property}' must be a non-empty string.");
        }
        return value.GetString()!;
    }

    private static Guid RequireArgumentGuid(
        JsonElement arguments,
        string property,
        string operationId)
    {
        var text = RequireArgumentString(arguments, property, operationId);
        if (!Guid.TryParse(text, out var value) || value == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' argument '{property}' must be a non-empty UUID.");
        }
        return value;
    }

    private void RequireAdapter(TargetState targetState, BridgeAdapterOwner owner)
    {
        lock (_connectionGate)
        {
            if (_targets.Count == 0 || _connection is not { IsConnected: true })
            {
                throw new InvalidOperationException("The Rhino/Grasshopper bridge is not connected.");
            }
            if (!targetState.Adapters.Contains(owner))
            {
                throw new InvalidOperationException(
                    $"The bound document does not advertise adapter '{owner}'.");
            }
        }
    }

    // The DEFAULT target: the only registered target when exactly one Grasshopper document is
    // open (today's single-document behavior, byte-for-byte), otherwise the first registered.
    private TargetState? DefaultTargetStateUnsafe() =>
        _targets.Count == 0 ? null : _targets.Values.MinBy(state => state.Sequence);

    private TargetState? DefaultTargetStateOrNull()
    {
        lock (_connectionGate)
        {
            return DefaultTargetStateUnsafe();
        }
    }

    private TargetState RequireDefaultTargetState()
    {
        lock (_connectionGate)
        {
            return DefaultTargetStateUnsafe()
                ?? throw new InvalidOperationException("No explicit document target is registered.");
        }
    }

    /// <summary>
    /// Shared session-to-Grasshopper-document resolution rule: a NULL binding resolves to the only
    /// registered target when exactly one document is open; a set binding must match a registered
    /// docKey; every other combination fails with an actionable listing of the registered
    /// documents (file name + docKey) so the caller can bind or rebind the session.
    /// </summary>
    private TargetState ResolveSessionTargetState(SessionRecord session) =>
        ResolveTargetStateByDocKey(
            string.IsNullOrWhiteSpace(session.GrasshopperDoc) ? null : session.GrasshopperDoc.Trim(),
            $"session '{session.Name}'");

    private TargetState ResolveJobTargetState(string? frozenDocKey) =>
        ResolveTargetStateByDocKey(
            string.IsNullOrWhiteSpace(frozenDocKey) ? null : frozenDocKey.Trim(),
            "this job");

    private TargetState ResolveTargetStateByDocKey(string? docKey, string subject)
    {
        lock (_connectionGate)
        {
            if (_targets.Count == 0)
            {
                throw new InvalidOperationException("No explicit document target is registered.");
            }
            if (docKey is null)
            {
                if (_targets.Count == 1)
                {
                    return _targets.Values.First();
                }
                throw new InvalidOperationException(
                    $"{char.ToUpperInvariant(subject[0])}{subject[1..]} is not bound to a Grasshopper document and " +
                    $"{_targets.Count} are registered. Bind the session to one document (or create sessions " +
                    $"with a grasshopperDoc). Registered documents: {DescribeRegisteredDocumentsUnsafe()}.");
            }
            var match = _targets.Values.FirstOrDefault(state =>
                string.Equals(state.DocKey, docKey, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
            throw new InvalidOperationException(
                $"{char.ToUpperInvariant(subject[0])}{subject[1..]} is bound to Grasshopper document " +
                $"'{docKey}', which is not registered. Registered documents: " +
                $"{DescribeRegisteredDocumentsUnsafe()}.");
        }
    }

    private string DescribeRegisteredDocumentsUnsafe() =>
        _targets.Count == 0
            ? "none"
            : string.Join(
                ", ",
                _targets.Values
                    .OrderBy(state => state.Sequence)
                    .Select(state =>
                        $"{Path.GetFileName(state.Target.GrasshopperPath)} (docKey {state.DocKey})"));

    /// <summary>Lazily created per-document managed history under dataRoot\histories\&lt;docKey&gt;.</summary>
    private ManagedHistoryRepository GetHistory(TargetState targetState)
    {
        lock (targetState)
        {
            return targetState.History ??= new ManagedHistoryRepository(
                Path.Combine(_dataRoot, "histories", targetState.DocKey));
        }
    }

    // Resolves gptino:auto read/write expectations against the live snapshot, gated by the per-session
    // resource ledger: an auto expectation is filled with the live fingerprint ONLY when THIS session wrote
    // the resource and it has not changed since (self-sequential). A foreign-session write, a manual
    // Grasshopper edit, an absent resource, or a resource this session never wrote is REFUSED and returned as
    // a conflict so the existing Blocked path stops it. Runs on the single broker worker thread, so the
    // ledger read cannot race a commit.
    internal static (ChangeSet Resolved, IReadOnlyList<string> Conflicts) ResolveAutoExpectations(
        ChangeSet changeSet,
        StateSnapshot liveState,
        Guid sessionId,
        IReadOnlyDictionary<string, ResourceLedgerEntry> resourceLedger)
    {
        if (!changeSet.ReadSet.Concat(changeSet.WriteSet).Any(expectation => expectation.IsAuto))
        {
            return (changeSet, Array.Empty<string>());
        }

        var conflicts = new List<string>();

        ResourceExpectation Resolve(ResourceExpectation expectation)
        {
            if (!expectation.IsAuto)
            {
                return expectation;
            }
            var key = $"{expectation.Resource.Kind}:{expectation.Resource.Id}:{expectation.Resource.Field}";
            var live = liveState.Resources.FirstOrDefault(item =>
                ExactDomainOverlaps(item.Resource, expectation.Resource));
            if (live is null || string.IsNullOrWhiteSpace(live.Fingerprint))
            {
                conflicts.Add(
                    $"gptino:auto declined for {key}: the resource is absent from the live document. " +
                    "Create it first, or supply a concrete fingerprint.");
                return expectation;
            }
            if (!resourceLedger.TryGetValue(key, out var ledger))
            {
                // Fallback: a Python/Rhino sub-domain may lack its own ledger row (e.g. the first setComponentIo
                // right after createComponent), yet the parent component/object this session created still has a
                // ledger row. If this session owns the parent AND the parent's own fingerprint is unchanged
                // (no foreign session write and no manual edit touched the component or any sub-domain since),
                // resolve the sub-domain auto to its own live fingerprint. A foreign change moves the parent
                // fingerprint, so this still declines.
                var parent = ParentResource(expectation.Resource);
                if (parent is not null)
                {
                    var parentLive = liveState.Resources.FirstOrDefault(item =>
                        ExactDomainOverlaps(item.Resource, parent));
                    var parentEntry = resourceLedger.Values.FirstOrDefault(entry =>
                        entry.SessionId == sessionId && ExactDomainOverlaps(entry.Resource, parent));
                    if (parentLive is not null &&
                        parentEntry.Resource is not null &&
                        string.Equals(parentEntry.Fingerprint, parentLive.Fingerprint, StringComparison.Ordinal))
                    {
                        return expectation with { ExpectedFingerprint = live.Fingerprint };
                    }
                }
                conflicts.Add(
                    $"gptino:auto declined for {key}: this session has not written it, so there is no " +
                    $"baseline to fill (editing a pre-existing component). Current fingerprint: {live.Fingerprint}. " +
                    "Resubmit that resource with this concrete value directly.");
                return expectation;
            }
            if (ledger.SessionId != sessionId)
            {
                conflicts.Add(
                    $"gptino:auto declined for {key}: another session wrote it after this session last did. " +
                    $"Current fingerprint: {live.Fingerprint}. Re-read and resubmit with this value.");
                return expectation;
            }
            if (!string.Equals(ledger.Fingerprint, live.Fingerprint, StringComparison.Ordinal))
            {
                conflicts.Add(
                    $"gptino:auto declined for {key}: it drifted (a manual Grasshopper edit) since this session " +
                    $"last wrote it. Current fingerprint: {live.Fingerprint}. Re-read and resubmit with this value.");
                return expectation;
            }
            return expectation with { ExpectedFingerprint = live.Fingerprint };
        }

        var readSet = changeSet.ReadSet.Select(Resolve).ToArray();
        var writeSet = changeSet.WriteSet.Select(Resolve).ToArray();
        if (conflicts.Count > 0)
        {
            return (changeSet, conflicts);
        }
        return (changeSet with { ReadSet = readSet, WriteSet = writeSet }, Array.Empty<string>());
    }

    // The parent component/object of a Python/Rhino sub-domain, or null when the resource is already a
    // top-level domain. A freshly created component has no source/io/value snapshot rows yet, but its parent
    // exists; the parent's own fingerprint moves if anyone (foreign session or manual edit) touches the
    // component or its sub-domains, so the parent's unchanged fingerprint is a sound self-ownership proof.
    private static ResourceAddress? ParentResource(ResourceAddress resource) => resource.Kind switch
    {
        ResourceKind.GrasshopperComponentSource or ResourceKind.GrasshopperComponentIo or
        ResourceKind.GrasshopperComponentValue or ResourceKind.GrasshopperComponentLayout =>
            new ResourceAddress(ResourceKind.GrasshopperComponent, resource.Id, "*"),
        ResourceKind.RhinoObjectGeometry or ResourceKind.RhinoObjectAttributes =>
            new ResourceAddress(ResourceKind.RhinoObject, resource.Id, "*"),
        _ => null,
    };

    private static string? FindExpectedFingerprint(ChangeSet changeSet, TypedOperation operation)
    {
        foreach (var address in operation.Writes.Concat(operation.Reads))
        {
            var expectation = changeSet.WriteSet.Concat(changeSet.ReadSet)
                .FirstOrDefault(candidate => ExactDomainOverlaps(candidate.Resource, address));
            if (expectation is not null && !expectation.ExpectsAbsence)
            {
                return expectation.ExpectedFingerprint;
            }
        }
        return null;
    }

    private static IReadOnlyList<string> Verify(
        ChangeSet changeSet,
        SnapshotEnvelope snapshot,
        IReadOnlyList<JobDiagnostic> diagnostics,
        IReadOnlyList<ResourceObservation> operationObservations)
    {
        var problems = diagnostics
            .Where(item => item.Severity == BridgeDiagnosticSeverity.Error)
            .Select(item => $"{item.OperationId}: {item.Code}: {item.Message}")
            .ToList();
        foreach (var predicate in changeSet.AcceptancePredicates)
        {
            var observation = predicate.Resource is null
                ? null
                : operationObservations.LastOrDefault(item =>
                    ExactDomainOverlaps(item.Resource, predicate.Resource) ||
                    ConflictDetector.SharesPythonStateFingerprint(item.Resource, predicate.Resource));
            var resource = predicate.Resource is null || observation is not null
                ? null
                : snapshot.State.Resources.FirstOrDefault(item =>
                    ExactDomainOverlaps(item.Resource, predicate.Resource));
            var observedFingerprint = observation?.Fingerprint ?? resource?.Fingerprint;
            var exists = observation is not null
                ? observation.Fingerprint is not null
                : resource is not null;
            var passed = predicate.Kind switch
            {
                PredicateKind.FingerprintEquals => observedFingerprint is not null &&
                    string.Equals(observedFingerprint, predicate.ExpectedValue, StringComparison.Ordinal),
                PredicateKind.RuntimeErrorAbsent => diagnostics.All(item =>
                    item.Severity != BridgeDiagnosticSeverity.Error),
                PredicateKind.WireExists => exists,
                PredicateKind.WireAbsent => !exists,
                PredicateKind.ObjectExists => exists,
                PredicateKind.ObjectAbsent => !exists,
                _ => false
            };
            if (!passed)
            {
                problems.Add(
                    $"Acceptance predicate '{predicate.Name}' ({predicate.Kind}) was not satisfied. " +
                    "Omit acceptancePredicates ([]) to let the server attach the standard set instead of " +
                    "predicting outcomes.");
            }
        }
        return problems;
    }

    private IReadOnlyList<QueuedConflict> DetectQueuedConflicts(ChangeSet changeSet, string targetDocKey)
    {
        // Only jobs writing the SAME Grasshopper document can genuinely contend: sibling docs
        // share the Rhino-scoped ProjectId, so without this scope an Exclusive/overlap check
        // would flag phantom conflicts across unrelated documents. A null frozen TargetDoc is a
        // legacy/recovered row, which resolves to the default document at execute time.
        var defaultDocKey = DefaultTargetStateOrNull()?.DocKey;
        return _jobs.Values
            .Where(entry => IsActive(entry.State))
            .Where(entry => string.Equals(
                entry.TargetDoc ?? defaultDocKey,
                targetDocKey,
                StringComparison.OrdinalIgnoreCase))
            .SelectMany(entry => _conflictDetector.Detect(changeSet, entry.Job.ChangeSet)
                .Select(conflict => new QueuedConflict(entry.Job.JobId, conflict)))
            .ToArray();
    }

    private SessionOrderSnapshot ReadSessionOrder()
    {
        lock (_scheduleGate)
        {
            return _sessionOrder;
        }
    }

    private IReadOnlyDictionary<Guid, SessionRunState> ReadSessionStates()
    {
        lock (_scheduleGate)
        {
            return _sessionStates;
        }
    }

    private async Task SetJobPhaseAsync(
        LiveJobEntry entry,
        JobState state,
        string? message,
        IReadOnlyList<ChangeConflict>? blockingConflicts = null)
    {
        var phase = state.ToString().ToLowerInvariant();
        // Terminal states can be re-asserted (executor sets them, then the broker's completion
        // observer sets the same state again); only genuine transitions go to the problem log.
        var isRepeat = state == entry.State &&
            string.Equals(message, entry.Message, StringComparison.Ordinal);
        await _jobStore.UpdateStateAsync(
            entry.Job.JobId,
            state,
            phase,
            message,
            CancellationToken.None).ConfigureAwait(false);
        if (blockingConflicts is not null)
        {
            entry.BlockingConflicts = blockingConflicts;
        }
        entry.SetPhase(state, phase, message);
        if (!isRepeat)
        {
            _problemLog?.RecordJobState(
                entry.Job.JobId,
                entry.Session.Id,
                entry.Summary,
                state,
                message,
                blockingConflicts);
        }
    }

    private static LiveJobEntry CreateRestoredEntry(
        DurableJobRecord record,
        SessionRecord session)
    {
        var job = new QueuedJob(
            record.JobId,
            record.ChangeSet,
            record.EnqueueSequence,
            record.EnqueuedAt);
        var entry = new LiveJobEntry(
            job,
            session,
            record.Summary,
            record.IdempotencyKey,
            record.RequestHash,
            Array.Empty<QueuedConflict>(),
            record.TargetDoc);
        entry.SetPhase(record.State, record.Phase, record.Message, record.UpdatedAt);
        // Restored entries are always terminal (RecoveryRequired); resolve the completion task so a
        // waiting duplicate submission returns immediately instead of blocking on a job that will
        // never run again.
        entry.CompleteWith(new JobExecutionResult(record.JobId, record.State, record.Message));
        return entry;
    }

    private void RegisterRestoredEntry(LiveJobEntry entry)
    {
        var scope = IdempotencyScope(entry.Session.Id, entry.IdempotencyKey);
        if (!_jobs.TryAdd(entry.Job.JobId, entry))
        {
            throw new InvalidDataException(
                $"Duplicate durable job id '{entry.Job.JobId:D}'.");
        }
        if (!_idempotency.TryAdd(scope, entry.Job.JobId))
        {
            _jobs.TryRemove(entry.Job.JobId, out _);
            throw new InvalidDataException(
                $"Duplicate durable idempotency key for session '{entry.Session.Id:D}'.");
        }
        _broker.RecordJobState(entry.Job.JobId, entry.State);
    }

    private static SessionRecord CreateRecoveredSession(DurableJobRecord record) =>
        new(
            record.SessionId,
            "Recovered session",
            "modeler",
            "auto",
            null,
            SessionStates.Failed,
            int.MaxValue,
            null,
            "Review interrupted durable job",
            record.CreatedAt,
            record.UpdatedAt);

    private static string IdempotencyScope(Guid sessionId, string idempotencyKey) =>
        $"{sessionId:N}:{idempotencyKey}";

    private static bool IsActive(JobState state) => state is
        JobState.Queued or JobState.Validating or JobState.Executing or JobState.Verifying;

    private async Task ObserveCompletionAsync(LiveJobEntry entry, Task<JobExecutionResult> completion)
    {
        try
        {
            var result = await completion.ConfigureAwait(false);
            await SetJobPhaseAsync(entry, result.State, result.Message).ConfigureAwait(false);
            entry.CompleteWith(result);
        }
        catch (OperationCanceledException)
        {
            const string message =
                "AgentHost stopped before this job reached a durable terminal state. " +
                "No operations will be replayed automatically; inspect the document before recovery.";
            await SetJobPhaseAsync(entry, JobState.RecoveryRequired, message).ConfigureAwait(false);
            entry.CompleteWith(new JobExecutionResult(entry.Job.JobId, JobState.RecoveryRequired, message));
        }
        finally
        {
            _events.Publish();
        }
    }

    private void TrackCompletion(LiveJobEntry entry, Task<JobExecutionResult> completion)
    {
        var observer = ObserveCompletionAsync(entry, completion);
        _completionObservers[entry.Job.JobId] = observer;
        _ = RemoveCompletionObserverAsync(entry.Job.JobId, observer);
    }

    private async Task RemoveCompletionObserverAsync(Guid jobId, Task observer)
    {
        try
        {
            await observer.ConfigureAwait(false);
        }
        catch
        {
            // Keep the faulted observer registered so StopAsync surfaces the
            // durability failure instead of silently discarding it.
            return;
        }

        _completionObservers.TryRemove(
            new KeyValuePair<Guid, Task>(jobId, observer));
    }

    private object ProjectJob(LiveJobEntry entry, bool duplicate)
    {
        var state = entry.State;
        // Diagnostics and observations are complete only at a terminal state; non-terminal
        // job_status polls arrive every few seconds and must stay slim.
        var terminal = !IsActive(state);
        return new
        {
            jobId = entry.Job.JobId,
            sessionId = entry.Job.ChangeSet.SessionId,
            changeSetId = entry.Job.ChangeSet.ChangeSetId,
            state = state.ToString().ToLowerInvariant(),
            phase = entry.Phase,
            message = entry.Message,
            duplicate,
            enqueueSequence = entry.Job.EnqueueSequence,
            committed = ProjectJobView(entry.Committed, entry),
            // Present whenever the writes landed and the post-state is known — on commit
            // (identical to committed) and on deterministic failure. A failed job with applied
            // means: the change is live but NOT committed; fix and resubmit against these
            // fingerprints (or gptino:auto, which the ledger already tracks).
            applied = ProjectJobView(entry.Applied, entry),
            diagnostics = terminal
                ? (entry.Diagnostics ?? Array.Empty<JobDiagnostic>()).Select(item => new
                {
                    operationId = item.OperationId,
                    severity = item.Severity.ToString().ToLowerInvariant(),
                    code = item.Code,
                    message = item.Message
                }).ToArray()
                : null,
            conflictsWith = entry.Conflicts.Select(item => new
            {
                jobId = item.OtherJobId,
                kind = item.Conflict.Kind.ToString().ToLowerInvariant(),
                resource = item.Conflict.Resource,
                item.Conflict.Message
            }).ToArray()
        };
    }

    private static object? ProjectJobView(CommittedJobView? view, LiveJobEntry entry) =>
        view is null
            ? null
            : new
            {
                snapshotId = view.SnapshotId,
                revision = view.Revision,
                resources = view.Resources.Select(item => new
                {
                    kind = item.Resource.Kind,
                    id = item.Resource.Id,
                    field = item.Resource.Field,
                    fingerprint = item.Fingerprint
                }).ToArray(),
                sockets = entry.Sockets?.Select(component => new
                {
                    componentId = component.ComponentId,
                    inputs = component.Inputs.Select(ProjectSocket).ToArray(),
                    outputs = component.Outputs.Select(ProjectSocket).ToArray()
                }).ToArray(),
                outputs = entry.Outputs?.Select(component => new
                {
                    componentId = component.ComponentId,
                    inspection = component.Inspection
                }).ToArray()
            };

    private static object ProjectSocket(JobSocket socket) => new
    {
        id = socket.Id,
        name = socket.Name,
        nickName = socket.NickName,
        typeHint = socket.TypeHint,
        access = socket.Access
    };

    private static CommittedJobView BuildCommittedJobView(ChangeSet changeSet, SnapshotEnvelope after)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var resources = new List<CommittedResourceFingerprint>();
        void Add(ResourceAddress resource, string? fingerprint)
        {
            var key = $"{resource.Kind}:{resource.Id}:{resource.Field}";
            if (seen.Add(key))
            {
                resources.Add(new CommittedResourceFingerprint(resource, fingerprint));
            }
        }
        foreach (var expectation in changeSet.WriteSet)
        {
            var current = after.State.Resources.FirstOrDefault(item =>
                ExactDomainOverlaps(item.Resource, expectation.Resource));
            Add(expectation.Resource, current?.Fingerprint);
            // A freshly created component's sibling domains (layout/value) have fingerprints the
            // model cannot know yet is about to need — a slider is created, then its value is set.
            // Project the siblings so the next ChangeSet chains directly instead of paying one
            // Blocked round trip to learn the value-domain hash.
            if (expectation.Resource.Kind == ResourceKind.GrasshopperComponent)
            {
                foreach (var sibling in after.State.Resources.Where(item =>
                    item.Resource.Kind is
                        ResourceKind.GrasshopperComponentLayout or
                        ResourceKind.GrasshopperComponentValue &&
                    string.Equals(item.Resource.Id, expectation.Resource.Id, StringComparison.Ordinal)))
                {
                    Add(sibling.Resource, sibling.Fingerprint);
                }
            }
        }
        return new CommittedJobView(after.SnapshotId, after.State.Revision, resources);
    }

    private const int MaximumOutputInspectionComponents = 4;

    /// <summary>
    /// Records each resource this job actually changed (new, or a moved fingerprint) with this
    /// session as its last writer — including SIDE EFFECTS that never appear in the writeSet, such
    /// as a wire moving the target component's fingerprint. A later gptino:auto expectation from
    /// the SAME session then self-resolves against the true live state, and a foreign write flips
    /// ledger ownership so that session's auto Blocks. Runs on both the commit path and the
    /// deterministic-failure path: the ledger tracks the last OBSERVED-AND-OWNED write, committed
    /// or not, because the write physically landed either way. Never-demote discipline; runs on
    /// the broker worker thread, so no lock is needed.
    /// </summary>
    private void UpdateResourceLedger(
        SnapshotEnvelope before,
        SnapshotEnvelope after,
        Guid sessionId,
        Guid jobId)
    {
        try
        {
            var beforeFingerprints = before.State.Resources.ToDictionary(
                item => $"{item.Resource.Kind}:{item.Resource.Id}:{item.Resource.Field}",
                item => item.Fingerprint,
                StringComparer.Ordinal);
            foreach (var resource in after.State.Resources.Where(item =>
                !string.IsNullOrWhiteSpace(item.Fingerprint)))
            {
                var key = $"{resource.Resource.Kind}:{resource.Resource.Id}:{resource.Resource.Field}";
                var changed = !beforeFingerprints.TryGetValue(key, out var beforeFingerprint) ||
                    !string.Equals(beforeFingerprint, resource.Fingerprint, StringComparison.Ordinal);
                if (changed)
                {
                    _resourceLedger[key] = new ResourceLedgerEntry(
                        resource.Resource,
                        resource.Fingerprint!,
                        sessionId,
                        after.State.Revision);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not update the resource ledger for job {JobId}.", jobId);
        }
    }

    private static IReadOnlyList<JobComponentSockets> CollectComponentSockets(
        ChangeSet changeSet,
        SnapshotEnvelope after)
    {
        var components = changeSet.WriteSet
            .Where(expectation => expectation.Resource.Kind == ResourceKind.GrasshopperComponentIo)
            .Select(expectation => Guid.TryParse(expectation.Resource.Id, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
        if (components.Length == 0)
        {
            return Array.Empty<JobComponentSockets>();
        }

        var sockets = new List<JobComponentSockets>(components.Length);
        foreach (var componentId in components)
        {
            var state = after.Canvas.Objects.FirstOrDefault(item => item.ObjectId == componentId);
            if (state is null)
            {
                continue;
            }
            sockets.Add(new JobComponentSockets(
                componentId,
                state.Inputs.Select(ToJobSocket).ToArray(),
                state.Outputs.Select(ToJobSocket).ToArray()));
        }
        return sockets;
    }

    private static JobSocket ToJobSocket(CanvasParameterState parameter) =>
        new(
            parameter.ParameterId,
            parameter.Name,
            parameter.NickName,
            parameter.TypeHint,
            parameter.Access.ToString().ToLowerInvariant());

    private async Task<IReadOnlyList<JobComponentOutputs>> CollectComponentOutputsAsync(
        DocumentRuntime target,
        ChangeSet changeSet,
        SnapshotEnvelope after,
        CancellationToken cancellationToken)
    {
        var components = changeSet.WriteSet
            .Where(expectation => expectation.Resource.Kind is
                ResourceKind.GrasshopperComponent or
                ResourceKind.GrasshopperComponentSource or
                ResourceKind.GrasshopperComponentIo or
                ResourceKind.GrasshopperComponentValue)
            .Select(expectation => Guid.TryParse(expectation.Resource.Id, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .Take(MaximumOutputInspectionComponents)
            .ToArray();
        if (components.Length == 0)
        {
            return Array.Empty<JobComponentOutputs>();
        }

        var outputs = new List<JobComponentOutputs>(components.Length);
        foreach (var componentId in components)
        {
            try
            {
                // Direct bridge read: this runs while the executor holds the document WRITE gate, so
                // going through ReadBridgeQueryAsync (which enters the read gate) would deadlock.
                var request = new BridgeOperationRequest(
                    $"read-{Guid.NewGuid():N}",
                    BridgeAdapterOwner.CordycepsCanvas,
                    "canvas.inspectOutputs",
                    BridgeOperationAccess.Read,
                    after.State.Revision,
                    ExpectedFingerprint: null,
                    WriterLeaseToken: null,
                    JsonSerializer.SerializeToElement(
                        new { objectId = componentId },
                        BridgeProtocol.JsonOptions));
                var response = await SendOperationAsync(target, request, cancellationToken)
                    .ConfigureAwait(false);
                outputs.Add(new JobComponentOutputs(componentId, response.Result.Clone()));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                // Objects without component outputs (e.g. sliders) or transient bridge issues must
                // not cost the job its other observations.
                _logger.LogDebug(
                    exception,
                    "Output inspection skipped for component {ComponentId}.",
                    componentId);
            }
        }
        return outputs;
    }

    private void ValidateChangeSet(ChangeSet changeSet, SessionRecord session)
    {
        var projectId = CurrentTarget?.ProjectId ?? _options.ProjectId;
        if (changeSet.ChangeSetId == Guid.Empty)
        {
            throw new InvalidOperationException("ChangeSetId is required.");
        }
        if (changeSet.ProjectId != projectId)
        {
            throw new InvalidOperationException("ChangeSet belongs to another project.");
        }
        if (changeSet.SessionId != session.Id)
        {
            throw new InvalidOperationException("ChangeSet session does not match the calling Codex thread.");
        }
        if (changeSet.Operations is null || changeSet.Operations.Count == 0)
        {
            throw new InvalidOperationException("ChangeSet must contain at least one typed operation.");
        }
        if (changeSet.ReadSet is null || changeSet.WriteSet is null ||
            changeSet.Dependencies is null || changeSet.AcceptancePredicates is null ||
            changeSet.RollbackBeforeImages is null)
        {
            throw new InvalidOperationException("ChangeSet collections cannot be null.");
        }
        if (changeSet.Operations.Any(operation => OperationSemantics.IsWrite(operation.Kind)) &&
            changeSet.AcceptancePredicates.Count == 0)
        {
            throw new InvalidOperationException(
                "A live write ChangeSet requires at least one explicit acceptance predicate.");
        }
        if (changeSet.Operations.Any(operation =>
                string.IsNullOrWhiteSpace(operation.OperationId) ||
                operation.Reads is null || operation.Writes is null))
        {
            throw new InvalidOperationException("Every typed operation requires an id and resource sets.");
        }
        if (changeSet.Operations.Select(operation => operation.OperationId).Distinct().Count() !=
            changeSet.Operations.Count)
        {
            throw new InvalidOperationException("Typed operation ids must be unique within a ChangeSet.");
        }
        if (changeSet.Operations.Any(operation => !string.IsNullOrWhiteSpace(operation.PayloadSha256)))
        {
            throw new InvalidOperationException(
                "payloadSha256 is reserved broker-owned metadata and must be omitted from submissions.");
        }
        foreach (var predicate in changeSet.AcceptancePredicates)
        {
            ValidateAcceptancePredicate(predicate);
        }
    }

    /// <summary>
    /// Attaches the standard acceptance predicate per write kind when the model declared none:
    /// creates/bakes verify the object exists, deletes verify absence, wires verify presence or
    /// absence, and everything else (values, moves, script writes) verifies runtimeErrorAbsent.
    /// Explicit model-declared predicates are left untouched.
    /// </summary>
    private static ChangeSet ApplyDefaultPredicates(ChangeSet changeSet)
    {
        if (changeSet.AcceptancePredicates is not { Count: 0 } ||
            changeSet.Operations is null ||
            !changeSet.Operations.Any(operation => OperationSemantics.IsWrite(operation.Kind)))
        {
            return changeSet;
        }

        var predicates = new List<VerificationPredicate>();
        var runtimeErrorAbsent = false;
        foreach (var operation in changeSet.Operations.Where(item => OperationSemantics.IsWrite(item.Kind)))
        {
            var added = operation.Kind switch
            {
                OperationKind.CreateComponent or
                OperationKind.CreateRhinoPrimitive or
                OperationKind.CreateRhinoObject or
                OperationKind.BakeGeometry =>
                    TryAddDefaultObjectPredicate(predicates, operation, PredicateKind.ObjectExists),
                OperationKind.DeleteComponent or
                OperationKind.DeleteRhinoObject =>
                    TryAddDefaultObjectPredicate(predicates, operation, PredicateKind.ObjectAbsent),
                OperationKind.ConnectWire =>
                    TryAddDefaultWirePredicate(predicates, operation, PredicateKind.WireExists),
                OperationKind.DisconnectWire =>
                    TryAddDefaultWirePredicate(predicates, operation, PredicateKind.WireAbsent),
                _ => false
            };
            runtimeErrorAbsent |= !added;
        }
        if (runtimeErrorAbsent || predicates.Count == 0)
        {
            predicates.Add(new VerificationPredicate(
                "gptino:default runtimeErrorAbsent",
                PredicateKind.RuntimeErrorAbsent,
                null,
                null));
        }
        return changeSet with { AcceptancePredicates = predicates };
    }

    private static bool TryAddDefaultObjectPredicate(
        List<VerificationPredicate> predicates,
        TypedOperation operation,
        PredicateKind kind)
    {
        var resource = operation.Writes.FirstOrDefault(item => item.Kind is
            ResourceKind.GrasshopperComponent or
            ResourceKind.GrasshopperGroup or
            ResourceKind.RhinoObject);
        if (resource is null)
        {
            return false;
        }
        predicates.Add(new VerificationPredicate(
            $"gptino:default {kind.ToString().ToLowerInvariant()} {operation.OperationId}",
            kind,
            resource,
            null));
        return true;
    }

    private static bool TryAddDefaultWirePredicate(
        List<VerificationPredicate> predicates,
        TypedOperation operation,
        PredicateKind kind)
    {
        var resource = operation.Writes.FirstOrDefault(item =>
            item.Kind == ResourceKind.GrasshopperWire);
        if (resource is null)
        {
            return false;
        }
        predicates.Add(new VerificationPredicate(
            $"gptino:default {kind.ToString().ToLowerInvariant()} {operation.OperationId}",
            kind,
            resource,
            null));
        return true;
    }

    private static void ValidateAcceptancePredicate(VerificationPredicate predicate)
    {
        if (string.IsNullOrWhiteSpace(predicate.Name))
        {
            throw new InvalidOperationException("Acceptance predicate names cannot be empty.");
        }
        switch (predicate.Kind)
        {
            case PredicateKind.RuntimeErrorAbsent:
                if (predicate.Resource is not null || predicate.ExpectedValue is not null)
                {
                    throw new InvalidOperationException(
                        "RuntimeErrorAbsent does not accept a resource or expectedValue.");
                }
                return;
            case PredicateKind.FingerprintEquals:
                if (predicate.Resource is null || string.IsNullOrWhiteSpace(predicate.ExpectedValue))
                {
                    throw new InvalidOperationException(
                        "FingerprintEquals requires a resource and expectedValue.");
                }
                return;
            case PredicateKind.WireExists:
            case PredicateKind.WireAbsent:
                if (predicate.Resource?.Kind != ResourceKind.GrasshopperWire ||
                    predicate.ExpectedValue is not null)
                {
                    throw new InvalidOperationException(
                        $"{predicate.Kind} requires a GrasshopperWire resource and no expectedValue.");
                }
                return;
            case PredicateKind.ObjectExists:
            case PredicateKind.ObjectAbsent:
                if (predicate.Resource is null || predicate.ExpectedValue is not null ||
                    predicate.Resource.Kind is not (
                        ResourceKind.GrasshopperComponent or ResourceKind.GrasshopperGroup or
                        ResourceKind.RhinoObject))
                {
                    throw new InvalidOperationException(
                        $"{predicate.Kind} requires a supported object resource and no expectedValue.");
                }
                return;
            default:
                throw new InvalidOperationException(
                    $"Acceptance predicate kind '{predicate.Kind}' is reserved and unsupported.");
        }
    }

    private static void ValidateExpectationCoverage(
        ChangeSet changeSet,
        IReadOnlyList<PreparedOperation> prepared,
        Guid grasshopperDocumentId)
    {
        foreach (var expectation in changeSet.ReadSet.Concat(changeSet.WriteSet))
        {
            ValidateResourceAddress(expectation.Resource, grasshopperDocumentId);
            if (string.IsNullOrWhiteSpace(expectation.ExpectedFingerprint))
            {
                throw new InvalidOperationException("Resource expectations require a fingerprint.");
            }
        }
        if (changeSet.ReadSet.Any(expectation => expectation.ExpectsAbsence))
        {
            throw new InvalidOperationException(
                $"'{ResourceExpectation.AbsentFingerprint}' is not valid in readSet.");
        }
        RejectAmbiguousExpectations(changeSet.ReadSet, "readSet");
        RejectAmbiguousExpectations(changeSet.WriteSet, "writeSet");

        foreach (var preparedOperation in prepared)
        {
            var operation = preparedOperation.Operation;
            foreach (var resource in operation.Reads)
            {
                ValidateResourceAddress(resource, grasshopperDocumentId);
                var expectation = FindExpectation(changeSet.ReadSet, resource);
                if (expectation is null || expectation.ExpectsAbsence)
                {
                    throw new InvalidOperationException(
                        $"Operation '{operation.OperationId}' read '{resource.Kind}:{resource.Id}:{resource.Field}' " +
                        "requires an actual fingerprint in readSet.");
                }
            }
            foreach (var resource in operation.Writes)
            {
                ValidateResourceAddress(resource, grasshopperDocumentId);
                if (FindExpectation(changeSet.WriteSet, resource) is null)
                {
                    throw new InvalidOperationException(
                        $"Operation '{operation.OperationId}' write '{resource.Kind}:{resource.Id}:{resource.Field}' " +
                        "requires an optimistic expectation in writeSet.");
                }
            }
            if (OperationSemantics.IsWrite(operation.Kind) && operation.Writes.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Write operation '{operation.OperationId}' must declare at least one write resource.");
            }
            if (!OperationSemantics.IsWrite(operation.Kind) && operation.Writes.Count != 0)
            {
                throw new InvalidOperationException(
                    $"Read operation '{operation.OperationId}' cannot declare write resources.");
            }
            ValidatePayloadExpectationAlignment(changeSet, preparedOperation);
        }

        RejectUnusedExpectations(changeSet, prepared);
        RejectOverlappingOperationWrites(prepared);
        RejectInterleavedPythonFingerprintSequences(prepared);

        foreach (var predicate in changeSet.AcceptancePredicates.Where(item => item.Resource is not null))
        {
            ValidateResourceAddress(predicate.Resource!, grasshopperDocumentId);
            if (!prepared.SelectMany(item => item.Operation.Reads.Concat(item.Operation.Writes))
                    .Any(resource => ExactDomainOverlaps(resource, predicate.Resource!)))
            {
                throw new InvalidOperationException(
                    $"Acceptance predicate '{predicate.Name}' targets a resource not declared by any operation.");
            }
        }
        foreach (var beforeImage in changeSet.RollbackBeforeImages)
        {
            ValidateResourceAddress(beforeImage.Resource, grasshopperDocumentId);
            if (string.IsNullOrWhiteSpace(beforeImage.ArtifactReference) ||
                string.IsNullOrWhiteSpace(beforeImage.Fingerprint) ||
                !prepared.SelectMany(item => item.Operation.Writes)
                    .Any(resource => ExactDomainOverlaps(resource, beforeImage.Resource)))
            {
                throw new InvalidOperationException(
                    "Rollback before images require a declared write resource, artifact reference, and fingerprint.");
            }
        }

        foreach (var expectation in changeSet.WriteSet.Where(item => item.ExpectsAbsence))
        {
            var creator = prepared.FirstOrDefault(item =>
                TryGetCreatedResource(item, out var created) &&
                ExactDomainOverlaps(created, expectation.Resource));
            if (creator is null)
            {
                throw new InvalidOperationException(
                    $"'{ResourceExpectation.AbsentFingerprint}' is allowed only for an exact createComponent, " +
                    "createRhinoPrimitive, createRhinoObject, bakeGeometry, connectWire, or new setGroup target.");
            }
        }

        foreach (var preparedOperation in prepared.Where(item => item.Operation.Kind is
                     OperationKind.CreateComponent or OperationKind.CreateRhinoPrimitive or
                     OperationKind.CreateRhinoObject or OperationKind.BakeGeometry or
                     OperationKind.ConnectWire))
        {
            if (!TryGetCreatedResource(preparedOperation, out var created) ||
                !changeSet.WriteSet.Any(expectation =>
                    expectation.ExpectsAbsence &&
                    ExactDomainOverlaps(expectation.Resource, created)))
            {
                throw new InvalidOperationException(
                    $"Create operation '{preparedOperation.Operation.OperationId}' requires writeSet " +
                    $"expectation '{ResourceExpectation.AbsentFingerprint}' for its exact target.");
            }
        }
        RejectConflictingRhinoLogicalEntityClaims(prepared);
    }

    private static void RejectConflictingRhinoLogicalEntityClaims(
        IReadOnlyList<PreparedOperation> prepared)
    {
        var claims = new Dictionary<string, (string OperationId, Guid ObjectId, string Role)>(
            StringComparer.Ordinal);
        foreach (var item in prepared.Where(item => item.Operation.Kind is
                     OperationKind.CreateRhinoPrimitive or OperationKind.CreateRhinoObject or
                     OperationKind.BakeGeometry or OperationKind.ModifyRhinoObject or
                     OperationKind.UpdateRhinoAttributes))
        {
            var operation = item.Operation;
            var objectId = RequireArgumentGuid(item.Arguments, "objectId", operation.OperationId);
            var logicalEntityId = RequireArgumentString(
                item.Arguments,
                "logicalEntityId",
                operation.OperationId);
            var role = operation.Kind is
                OperationKind.CreateRhinoPrimitive or OperationKind.CreateRhinoObject or
                OperationKind.BakeGeometry
                ? "create"
                : "existing";
            if (claims.TryGetValue(logicalEntityId, out var prior) && prior.ObjectId != objectId)
            {
                throw new InvalidOperationException(
                    $"Rhino logical entity '{logicalEntityId}' is claimed by both " +
                    $"'{prior.OperationId}' ({prior.Role}, {prior.ObjectId:D}) and " +
                    $"'{operation.OperationId}' ({role}, {objectId:D}) in one ChangeSet.");
            }
            claims[logicalEntityId] = (operation.OperationId, objectId, role);
        }
    }

    private static void ValidatePayloadExpectationAlignment(
        ChangeSet changeSet,
        PreparedOperation prepared)
    {
        var operation = prepared.Operation;
        var arguments = prepared.Arguments;
        switch (prepared.BridgeOperation)
        {
            case "canvas.setNumberSlider":
                RequirePayloadFingerprint(
                    changeSet,
                    operation,
                    TargetResource(operation, arguments, ResourceKind.GrasshopperComponentValue),
                    RequireArgumentString(arguments, "expectedFingerprint", operation.OperationId));
                return;

            case "canvas.move":
                foreach (var item in arguments.GetProperty("expectedFingerprints").EnumerateObject())
                {
                    if (!Guid.TryParse(item.Name, out var componentId) ||
                        item.Value.ValueKind != JsonValueKind.String ||
                        string.IsNullOrWhiteSpace(item.Value.GetString()))
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' has an invalid component fingerprint entry.");
                    }
                    RequirePayloadFingerprint(
                        changeSet,
                        operation,
                        new ResourceAddress(
                            ResourceKind.GrasshopperComponentLayout,
                            componentId.ToString("D")),
                        item.Value.GetString()!);
                }
                return;

            case "canvas.delete":
                RequirePayloadFingerprint(
                    changeSet,
                    operation,
                    TargetResource(operation, arguments, ResourceKind.GrasshopperComponent),
                    RequireArgumentString(arguments, "expectedFingerprint", operation.OperationId));
                return;

            case "rhino.transform":
            case "rhino.delete":
                RequirePayloadFingerprint(
                    changeSet,
                    operation,
                    TargetResource(operation, arguments, ResourceKind.RhinoObject),
                    RequireArgumentString(arguments, "expectedFingerprint", operation.OperationId));
                return;

            case "rhino.upsert":
                var resource = TargetResource(operation, arguments, ResourceKind.RhinoObject);
                var expectation = FindExpectation(changeSet.WriteSet, resource)
                    ?? throw new InvalidOperationException(
                        $"Operation '{operation.OperationId}' requires an exact Rhino write expectation.");
                var expected = arguments.GetProperty("expectedFingerprint");
                if (operation.Kind is OperationKind.CreateRhinoObject or OperationKind.BakeGeometry)
                {
                    if (!expectation.ExpectsAbsence || expected.ValueKind != JsonValueKind.Null)
                    {
                        throw new InvalidOperationException(
                            $"Exact Rhino create '{operation.OperationId}' requires writeSet " +
                            $"'{ResourceExpectation.AbsentFingerprint}' and a null payload expectedFingerprint.");
                    }
                    return;
                }
                RequirePayloadFingerprint(
                    changeSet,
                    operation,
                    resource,
                    RequireArgumentString(arguments, "expectedFingerprint", operation.OperationId));
                return;
        }
    }

    private static ResourceAddress TargetResource(
        TypedOperation operation,
        JsonElement arguments,
        ResourceKind kind) =>
        new(
            kind,
            RequireArgumentGuid(arguments, "objectId", operation.OperationId).ToString("D"));

    private static void RequirePayloadFingerprint(
        ChangeSet changeSet,
        TypedOperation operation,
        ResourceAddress resource,
        string payloadFingerprint)
    {
        if (string.Equals(payloadFingerprint, ResourceExpectation.AutoFingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' cannot use gptino:auto: value and geometry writes " +
                "(setNumberSlider, move, delete, rhino transform/upsert) carry the fingerprint in the payload " +
                "and need the concrete value from the previous commit. Auto is for Python source/schema/value " +
                "and wire writeSet expectations only.");
        }
        var expectation = FindExpectation(changeSet.WriteSet, resource);
        if (expectation is null || expectation.ExpectsAbsence ||
            !string.Equals(
                expectation.ExpectedFingerprint,
                payloadFingerprint,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' payload fingerprint does not match its exact writeSet expectation.");
        }
    }

    private static void RejectUnusedExpectations(
        ChangeSet changeSet,
        IReadOnlyList<PreparedOperation> prepared)
    {
        var reads = prepared.SelectMany(item => item.Operation.Reads).ToArray();
        var writes = prepared.SelectMany(item => item.Operation.Writes).ToArray();
        if (changeSet.ReadSet.Any(expectation =>
                !reads.Any(resource => ExactDomainOverlaps(resource, expectation.Resource))))
        {
            throw new InvalidOperationException(
                "readSet contains a resource not declared by any operation read.");
        }
        if (changeSet.WriteSet.Any(expectation =>
                !writes.Any(resource => ExactDomainOverlaps(resource, expectation.Resource))))
        {
            throw new InvalidOperationException(
                "writeSet contains a resource not declared by any operation write.");
        }
    }

    private static void RejectOverlappingOperationWrites(IReadOnlyList<PreparedOperation> prepared)
    {
        for (var left = 0; left < prepared.Count; left++)
        {
            for (var right = left + 1; right < prepared.Count; right++)
            {
                var overlap = prepared[left].Operation.Writes.FirstOrDefault(leftResource =>
                    prepared[right].Operation.Writes.Any(rightResource =>
                        ConflictDetector.Overlaps(leftResource, rightResource)));
                if (overlap is not null)
                {
                    throw new InvalidOperationException(
                        $"Operations '{prepared[left].Operation.OperationId}' and " +
                        $"'{prepared[right].Operation.OperationId}' both write " +
                        $"'{overlap.Kind}:{overlap.Id}'. Combine them into one typed operation.");
                }
            }
        }
    }

    private static void RejectInterleavedPythonFingerprintSequences(
        IReadOnlyList<PreparedOperation> prepared)
    {
        var indexedWrites = prepared
            .Select((item, index) => new { Item = item, Index = index, Resource = PythonStateWrite(item.Operation) })
            .Where(item => item.Resource is not null)
            .ToArray();
        if (indexedWrites.Length == 0)
        {
            return;
        }
        var componentIds = indexedWrites
            .Select(item => item.Resource!.Id)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (componentIds.Length != 1 || prepared.Any(item =>
                OperationSemantics.IsWrite(item.Operation.Kind) &&
                PythonStateWrite(item.Operation) is null))
        {
            throw new InvalidOperationException(
                "A ChangeSet with Python source/I/O/value writes may mutate exactly one Python component and cannot contain other writes.");
        }
        foreach (var group in indexedWrites.GroupBy(item => item.Resource!.Id, StringComparer.Ordinal))
        {
            if (group.Count() < 2)
            {
                continue;
            }
            var first = group.Min(item => item.Index);
            var last = group.Max(item => item.Index);
            if (prepared.Skip(first).Take(last - first + 1).Any(item =>
                    PythonStateWrite(item.Operation)?.Id != group.Key))
            {
                throw new InvalidOperationException(
                    $"Wireify mutations for Python component '{group.Key}' must be contiguous within a ChangeSet.");
            }
        }
    }

    /// <summary>
    /// Script-content operations are the ones whose Error diagnostics describe the SCRIPT (compile
    /// or runtime failures) rather than the operation: the write itself landed deterministically.
    /// Keyed on OperationKind — the typed contract surface — not on bridge op names or plugin
    /// diagnostic codes. Covers the whole python-state family: the Wireify adapter emits Error
    /// diagnostics only from component runtime messages (script content), while operation-level
    /// failures arrive as thrown bridge errors and still abort. Live round R3 confirmed compile
    /// errors surface on setComponentIo responses (the schema write triggers the solve).
    /// </summary>
    private static bool IsScriptContentOperation(OperationKind kind) =>
        kind is OperationKind.UpdatePythonSource or
            OperationKind.ExecutePython or
            OperationKind.SetComponentIo or
            OperationKind.ConvertSocket;

    private static ResourceAddress? PythonStateWrite(TypedOperation operation)
    {
        if (operation.Kind is not (
                OperationKind.UpdatePythonSource or
                OperationKind.SetComponentIo or
                OperationKind.ConvertSocket or
                OperationKind.ExecutePython))
        {
            return null;
        }
        return operation.Writes.SingleOrDefault(resource => resource.Kind is
            ResourceKind.GrasshopperComponentSource or
            ResourceKind.GrasshopperComponentIo or
            ResourceKind.GrasshopperComponentValue);
    }

    private static void ValidateResourceAddress(ResourceAddress resource, Guid grasshopperDocumentId)
    {
        if (string.IsNullOrWhiteSpace(resource.Id) || resource.Field != "*")
        {
            throw new InvalidOperationException(
                "Resource addresses require a canonical id and the whole-domain '*' field.");
        }

        if (resource.Kind == ResourceKind.Document)
        {
            // The whole-document resource is scoped to the bound Grasshopper document (its runtime
            // DocumentID), not the ProjectId, which is Rhino-scoped and shared by sibling documents.
            if (!string.Equals(
                    resource.Id,
                    grasshopperDocumentId.ToString("D"),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Document resource ids must be the bound Grasshopper document UUID in D format.");
            }
            return;
        }

        if (resource.Kind == ResourceKind.GrasshopperWire)
        {
            if (!TryCanonicalizeWireId(resource.Id, out var canonical) ||
                !string.Equals(resource.Id, canonical, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Grasshopper wire resource ids must use canonical lowercase N-format endpoint UUIDs.");
            }
            return;
        }

        if (!Guid.TryParse(resource.Id, out var id) || id == Guid.Empty ||
            !string.Equals(resource.Id, id.ToString("D"), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Kind}' ids must be canonical lowercase D-format UUIDs.");
        }
    }

    private static bool TryCanonicalizeWireId(string value, out string canonical)
    {
        canonical = string.Empty;
        var endpoints = value.Split('>', StringSplitOptions.None);
        if (endpoints.Length != 2)
        {
            return false;
        }
        var source = endpoints[0].Split('/', StringSplitOptions.None);
        var target = endpoints[1].Split('/', StringSplitOptions.None);
        if (source.Length != 2 || target.Length != 2 ||
            !Guid.TryParseExact(source[0], "N", out var sourceObject) ||
            !Guid.TryParseExact(source[1], "N", out var sourceParameter) ||
            !Guid.TryParseExact(target[0], "N", out var targetObject) ||
            !Guid.TryParseExact(target[1], "N", out var targetParameter) ||
            sourceObject == Guid.Empty || sourceParameter == Guid.Empty ||
            targetObject == Guid.Empty || targetParameter == Guid.Empty)
        {
            return false;
        }
        canonical = FormattableString.Invariant(
            $"{sourceObject:N}/{sourceParameter:N}>{targetObject:N}/{targetParameter:N}");
        return true;
    }

    private static void RejectAmbiguousExpectations(
        IReadOnlyList<ResourceExpectation> expectations,
        string collectionName)
    {
        for (var left = 0; left < expectations.Count; left++)
        {
            for (var right = left + 1; right < expectations.Count; right++)
            {
                if (ConflictDetector.Overlaps(
                        expectations[left].Resource,
                        expectations[right].Resource))
                {
                    throw new InvalidOperationException(
                        $"{collectionName} contains overlapping expectations for " +
                        $"'{expectations[left].Resource.Kind}:{expectations[left].Resource.Id}'.");
                }
            }
        }
    }

    private static ResourceExpectation? FindExpectation(
        IReadOnlyList<ResourceExpectation> expectations,
        ResourceAddress resource) =>
        expectations.SingleOrDefault(item => ExactDomainOverlaps(item.Resource, resource));

    private static bool ExactDomainOverlaps(ResourceAddress left, ResourceAddress right) =>
        left.Kind == right.Kind &&
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        (left.Field == "*" || right.Field == "*" ||
         string.Equals(left.Field, right.Field, StringComparison.Ordinal));

    private static bool TryGetCreatedResource(
        PreparedOperation prepared,
        out ResourceAddress resource)
    {
        var arguments = prepared.Arguments;
        switch (prepared.Operation.Kind)
        {
            case OperationKind.CreateComponent:
                resource = new ResourceAddress(
                    ResourceKind.GrasshopperComponent,
                    RequireArgumentGuid(arguments, "objectId", prepared.Operation.OperationId).ToString("D"));
                return true;
            case OperationKind.CreateRhinoPrimitive:
            case OperationKind.CreateRhinoObject:
            case OperationKind.BakeGeometry:
                resource = new ResourceAddress(
                    ResourceKind.RhinoObject,
                    RequireArgumentGuid(arguments, "objectId", prepared.Operation.OperationId).ToString("D"));
                return true;
            case OperationKind.ConnectWire:
                var wire = arguments.GetProperty("wire");
                if (wire.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException(
                        $"Operation '{prepared.Operation.OperationId}' wire must be an object.");
                }
                var sourceObject = RequireArgumentGuid(wire, "sourceObjectId", prepared.Operation.OperationId);
                var sourceParameter = RequireArgumentGuid(wire, "sourceParameterId", prepared.Operation.OperationId);
                var targetObject = RequireArgumentGuid(wire, "targetObjectId", prepared.Operation.OperationId);
                var targetParameter = RequireArgumentGuid(wire, "targetParameterId", prepared.Operation.OperationId);
                resource = new ResourceAddress(
                    ResourceKind.GrasshopperWire,
                    FormattableString.Invariant(
                        $"{sourceObject:N}/{sourceParameter:N}>{targetObject:N}/{targetParameter:N}"));
                return true;
            case OperationKind.SetGroup:
                resource = new ResourceAddress(
                    ResourceKind.GrasshopperGroup,
                    RequireArgumentGuid(arguments, "groupId", prepared.Operation.OperationId).ToString("D"));
                return true;
            default:
                resource = null!;
                return false;
        }
    }

    private static string RequiredString(JsonElement arguments, string property)
    {
        if (!arguments.TryGetProperty(property, out var element) ||
            string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new InvalidOperationException($"'{property}' is required.");
        }
        return element.GetString()!.Trim();
    }

    private static string ComputeAcceptedRequestHash(
        ChangeSet changeSet,
        string expectedSnapshotId,
        string summary,
        IReadOnlyList<PreparedOperation> prepared)
    {
        var payloads = prepared.Select(item =>
        {
            using var document = JsonDocument.Parse(item.FrozenPayload);
            return new
            {
                operationId = item.Operation.OperationId,
                sourceArtifact = item.Operation.PayloadArtifact,
                payload = document.RootElement.Clone()
            };
        }).ToArray();
        var acceptedRequest = JsonSerializer.SerializeToElement(
            new
            {
                expectedSnapshotId,
                summary,
                changeSet,
                payloads
            },
            BridgeProtocol.JsonOptions);
        return Sha256(CanonicalizeJson(acceptedRequest));
    }

    private static void RequireMatchingRequestHash(
        string storedHash,
        string requestHash,
        string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(storedHash) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(storedHash),
                Encoding.ASCII.GetBytes(requestHash)))
        {
            throw new InvalidOperationException(
                $"Idempotency key '{idempotencyKey}' is already bound to a different accepted request. " +
                "The original job is still tracked: call job_status with the jobId from the first " +
                "change_submit response instead of resubmitting with regenerated changeSetId/createdAt.");
        }
    }

    private static byte[] CanonicalizeJson(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonicalJson(writer, element);
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }
                writer.WriteEndArray();
                return;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                return;
            case JsonValueKind.Number:
                writer.WriteRawValue(
                    CanonicalizeNumber(element.GetRawText()),
                    skipInputValidation: false);
                return;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                return;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                return;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                return;
            default:
                throw new InvalidOperationException("Undefined JSON values cannot be canonicalized.");
        }
    }

    private static string CanonicalizeNumber(string raw)
    {
        if (raw.Length > MaximumCanonicalNumberCharacters)
        {
            throw new InvalidOperationException(
                $"JSON numbers cannot exceed {MaximumCanonicalNumberCharacters} characters.");
        }
        var negative = raw[0] == '-';
        var unsigned = negative ? raw[1..] : raw;
        var exponentIndex = unsigned.IndexOf('e');
        if (exponentIndex < 0)
        {
            exponentIndex = unsigned.IndexOf('E');
        }
        var mantissa = exponentIndex < 0 ? unsigned : unsigned[..exponentIndex];
        var decimalIndex = mantissa.IndexOf('.');
        var fractionalDigits = decimalIndex < 0 ? 0 : mantissa.Length - decimalIndex - 1;
        var digits = decimalIndex < 0
            ? mantissa
            : string.Concat(mantissa.AsSpan(0, decimalIndex), mantissa.AsSpan(decimalIndex + 1));
        digits = digits.TrimStart('0');
        if (digits.Length == 0)
        {
            return negative ? "-0" : "0";
        }

        var explicitExponent = exponentIndex < 0
            ? BigInteger.Zero
            : BigInteger.Parse(unsigned[(exponentIndex + 1)..], CultureInfo.InvariantCulture);
        var exponent = explicitExponent - fractionalDigits;
        var trailingZeros = digits.Length - digits.TrimEnd('0').Length;
        if (trailingZeros > 0)
        {
            digits = digits[..^trailingZeros];
            exponent += trailingZeros;
        }
        var scientificExponent = exponent + digits.Length - 1;
        var coefficient = digits.Length == 1
            ? digits
            : $"{digits[0]}.{digits[1..]}";
        var sign = negative ? "-" : string.Empty;
        return scientificExponent.IsZero
            ? $"{sign}{coefficient}"
            : $"{sign}{coefficient}e{scientificExponent.ToString(CultureInfo.InvariantCulture)}";
    }

    private static string BuildSnapshotId(StateSnapshot state, string documentFingerprint) =>
        $"s{state.Revision}-{Sha256($"{state.Target.Identity}\n{documentFingerprint}")[..24]}";

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Sha256(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private sealed record ResourceObservation(ResourceAddress Resource, string? Fingerprint);

    private sealed record JobDiagnostic(
        string OperationId,
        BridgeDiagnosticSeverity Severity,
        string Code,
        string Message);

    /// <summary>
    /// The Grasshopper-assigned socket identities of a component this job reshaped, read from the
    /// post-write snapshot, so the session can wire without a follow-up snapshot_read round trip.
    /// </summary>
    private sealed record JobComponentSockets(
        Guid ComponentId,
        IReadOnlyList<JobSocket> Inputs,
        IReadOnlyList<JobSocket> Outputs);

    private sealed record JobSocket(
        Guid Id,
        string Name,
        string NickName,
        string? TypeHint,
        string Access);

    /// <summary>Post-solve canvas.inspectOutputs result for one written component.</summary>
    private sealed record JobComponentOutputs(Guid ComponentId, JsonElement Inspection);

    internal readonly record struct ResourceLedgerEntry(
        ResourceAddress Resource, string Fingerprint, Guid SessionId, long Revision);

    /// <summary>
    /// Post-commit chaining data: the fresh snapshot identity plus the committed write
    /// resources' fingerprints, so a session can base its next ChangeSet on job_status
    /// instead of paying another full snapshot_read.
    /// </summary>
    private sealed record CommittedJobView(
        string SnapshotId,
        long Revision,
        IReadOnlyList<CommittedResourceFingerprint> Resources);

    private sealed record CommittedResourceFingerprint(ResourceAddress Resource, string? Fingerprint);

    private sealed record PreparedOperation(
        TypedOperation Operation,
        BridgeAdapterOwner Owner,
        string BridgeOperation,
        JsonElement Arguments,
        byte[] FrozenPayload,
        string PayloadSha256);

    private sealed record SnapshotEnvelope(
        string SnapshotId,
        StateSnapshot State,
        CanvasSnapshot Canvas);

    /// <summary>
    /// Per-registered-Grasshopper-document state: the live target (freshest registration), its
    /// advertised adapters, the per-document snapshot cache + capture gate, the last selection
    /// event, and the lazily created per-docKey managed history. Membership and Target/Adapters/
    /// DocKey mutations happen under _connectionGate; Snapshot follows the former singleton
    /// field's benign-race discipline; Selection is written under _connectionGate.
    /// </summary>
    private sealed class TargetState(DocumentRuntime target, string docKey, long sequence)
    {
        public DocumentRuntime Target { get; set; } = target;

        /// <summary>Durable path-derived docKey; recomputed on re-registration (Save As).</summary>
        public string DocKey { get; set; } = docKey;

        /// <summary>Registration order; the smallest live sequence is the DEFAULT target.</summary>
        public long Sequence { get; } = sequence;

        public HashSet<BridgeAdapterOwner> Adapters { get; set; } = [];

        public SnapshotEnvelope? Snapshot { get; set; }

        public SemaphoreSlim SnapshotGate { get; } = new(1, 1);

        public SelectionChangedEvent? Selection { get; set; }

        /// <summary>Backend receipt ordinal of <see cref="Selection"/>; written under _connectionGate.</summary>
        public long SelectionSequence { get; set; }

        /// <summary>Backend receipt time of <see cref="Selection"/>; written under _connectionGate.</summary>
        public DateTimeOffset SelectionStamp { get; set; }

        public ManagedHistoryRepository? History { get; set; }
    }

    /// <summary>
    /// A bridge call awaiting its response, remembering the exact target it was stamped with so
    /// the response guard and per-document failure paths never cross documents.
    /// </summary>
    private sealed record PendingBridgeRequest(
        TaskCompletionSource<BridgeFrame> Completion,
        DocumentRuntime ExpectedTarget,
        string ExpectedTargetKey);

    private sealed record ScopedInspection(
        string Scope,
        BridgeAdapterOwner Owner,
        string Operation,
        string? Fingerprint,
        JsonElement Result,
        IReadOnlyList<BridgeDiagnostic> Diagnostics);

    private sealed record QueuedConflict(Guid OtherJobId, ChangeConflict Conflict);

    private sealed class LiveJobEntry(
        QueuedJob job,
        SessionRecord session,
        string summary,
        string idempotencyKey,
        string requestHash,
        IReadOnlyList<QueuedConflict> conflicts,
        string? targetDoc = null)
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource<JobExecutionResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private JobState _state = JobState.Queued;
        private string _phase = "queued";
        private string? _message;
        private DateTimeOffset _updatedAt = job.EnqueuedAt;

        public QueuedJob Job { get; } = job;
        public SessionRecord Session { get; } = session;
        public string Summary { get; } = summary;
        public string IdempotencyKey { get; } = idempotencyKey;
        public string RequestHash { get; } = requestHash;
        public IReadOnlyList<QueuedConflict> Conflicts { get; } = conflicts;

        private string? _targetDoc = targetDoc;

        /// <summary>
        /// Durable docKey of the Grasshopper document this job was resolved to at submit time;
        /// null on legacy/recovered rows (default-document resolution at execute time).
        /// Re-keyed in place (under the backend's _connectionGate) when a Save As
        /// re-registration recomputes the target's docKey, so queued jobs keep resolving.
        /// </summary>
        public string? TargetDoc => Volatile.Read(ref _targetDoc);

        /// <summary>Follows a Save As docKey rename; never changes which document the job targets.</summary>
        public void RemapTargetDoc(string? targetDoc) => Volatile.Write(ref _targetDoc, targetDoc);

        /// <summary>
        /// Written once when the job goes Blocked: the structured conflicts that stopped it, so
        /// the panel can show the concrete resource instead of only the flattened prose message.
        /// </summary>
        public IReadOnlyList<ChangeConflict>? BlockingConflicts { get; set; }

        /// <summary>Written once by the single-writer executor just before Committed.</summary>
        public CommittedJobView? Committed { get; set; }

        /// <summary>
        /// Written once whenever the writes landed and the post-state is fully known: on commit
        /// (same view as Committed) and on deterministic verification failure. A failed job with
        /// Applied means "the change is live but not committed — fix and resubmit against these
        /// fingerprints"; committed stays success-only.
        /// </summary>
        public CommittedJobView? Applied { get; set; }

        /// <summary>
        /// Written once at a terminal transition: the per-operation bridge diagnostics the
        /// executor collected, so job_status carries errors/warnings/remarks without another read.
        /// </summary>
        public IReadOnlyList<JobDiagnostic>? Diagnostics { get; set; }

        /// <summary>Written once alongside Committed: post-solve socket map for I/O-writing jobs.</summary>
        public IReadOnlyList<JobComponentSockets>? Sockets { get; set; }

        /// <summary>Written once alongside Committed: post-solve output inspections per written component.</summary>
        public IReadOnlyList<JobComponentOutputs>? Outputs { get; set; }

        /// <summary>
        /// Resolves after the terminal phase has been recorded, so an awaiter that wakes always
        /// projects the terminal state. Duplicate submissions can safely share this task.
        /// </summary>
        public Task<JobExecutionResult> Completion => _completion.Task;

        public void CompleteWith(JobExecutionResult result) => _completion.TrySetResult(result);

        public JobState State
        {
            get
            {
                lock (_gate)
                {
                    return _state;
                }
            }
        }

        public string Phase
        {
            get
            {
                lock (_gate)
                {
                    return _phase;
                }
            }
        }

        public string? Message
        {
            get
            {
                lock (_gate)
                {
                    return _message;
                }
            }
        }

        public DateTimeOffset UpdatedAt
        {
            get
            {
                lock (_gate)
                {
                    return _updatedAt;
                }
            }
        }

        public void SetPhase(
            JobState state,
            string phase,
            string? message,
            DateTimeOffset? updatedAt = null)
        {
            lock (_gate)
            {
                _state = state;
                _phase = phase;
                _message = message;
                _updatedAt = updatedAt ?? DateTimeOffset.UtcNow;
            }
        }
    }
}
