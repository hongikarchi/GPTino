using System.Buffers;
using System.Collections.Concurrent;
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
    DateTimeOffset EnqueuedAt);

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
    DateTimeOffset UpdatedAt);

/// <summary>
/// Owns the authenticated Rhino named-pipe connection and the only live-document writer.
/// Model turns may run concurrently, but every submitted ChangeSet crosses this broker.
/// </summary>
public sealed class LiveDocumentBackend : BackgroundService, ILiveDocumentBackend,
    ILiveDocumentQueueControl, IJobExecutor, ISelectionContextSource
{
    private static readonly TimeSpan BridgeRequestTimeout = TimeSpan.FromSeconds(45);
    private const int MaximumArtifactBytes = 2 * 1024 * 1024;
    private const int MaximumCanonicalNumberCharacters = 4096;

    private readonly object _connectionGate = new();
    private readonly object _scheduleGate = new();
    private readonly object _executionGate = new();
    private readonly SemaphoreSlim _submissionGate = new(1, 1);
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);
    private readonly SemaphoreSlim _historyGate = new(1, 1);
    private readonly AsyncDocumentGate _documentGate = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<BridgeFrame>> _pending = new();
    private readonly ConcurrentDictionary<Guid, LiveJobEntry> _jobs = new();
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
    private readonly ManagedHistoryRepository _history;
    private readonly DurableJobStore _jobStore;
    private readonly string _artifactRoot;
    private readonly BridgeSecret? _bridgeSecret;
    private DocumentPipeConnection? _connection;
    private DocumentRuntime? _target;
    private HashSet<BridgeAdapterOwner> _availableAdapters = [];
    private SnapshotEnvelope? _snapshot;
    private SessionOrderSnapshot _sessionOrder;
    private IReadOnlyDictionary<Guid, SessionRunState> _sessionStates =
        new Dictionary<Guid, SessionRunState>();
    private CancellationTokenSource? _currentExecution;
    private Guid? _writerSessionId;
    private DateTimeOffset? _writerStartedAt;
    private long _enqueueSequence;
    private SelectionChangedEvent? _currentSelection;

    public LiveDocumentBackend(
        SessionStore store,
        AgentHostOptions options,
        EventHub events,
        ILogger<LiveDocumentBackend> logger)
    {
        _store = store;
        _options = options;
        _events = events;
        _logger = logger;
        _sessionOrder = new SessionOrderSnapshot(options.ProjectId, Array.Empty<Guid>(), 0);
        _broker = new SingleWriterBroker(this, ReadSessionOrder, ReadSessionStates);
        var dataRoot = options.ResolveDataDirectory();
        _artifactRoot = Path.Combine(dataRoot, "artifacts");
        Directory.CreateDirectory(_artifactRoot);
        _history = new ManagedHistoryRepository(Path.Combine(dataRoot, "history"));
        _jobStore = new DurableJobStore(Path.Combine(dataRoot, "live-jobs.db"));

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
                return _connection is { IsConnected: true } && _target is not null;
            }
        }
    }

    public DocumentRuntime? CurrentTarget
    {
        get
        {
            lock (_connectionGate)
            {
                return _target;
            }
        }
    }

    public int QueueLength => _jobs.Values.Count(entry => IsActive(entry.State));

    public long CurrentRevision => _snapshot?.State.Revision ?? 0;

    public string? CurrentGitCommit => _snapshot?.State.GitCommit;

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
        return ReadSnapshotCoreAsync(session.Id, arguments, cancellationToken);
    }

    public Task<object> ReadSnapshotAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        ReadSnapshotCoreAsync(sessionId: null, arguments, cancellationToken);

    private async Task<object> ReadSnapshotCoreAsync(
        Guid? sessionId,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        using var documentRead = await _documentGate.EnterReadAsync(cancellationToken)
            .ConfigureAwait(false);
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
            .Select(scope => ReadInspectionScopeAsync(scope, cancellationToken))
            .ToArray();
        SnapshotEnvelope? cached;
        lock (_executionGate)
        {
            cached = _writerSessionId is not null ? _snapshot : null;
        }

        var snapshotTask = cached is not null
            ? Task.FromResult(cached)
            : CaptureSnapshotAsync(force: false, cancellationToken);
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

    public Task<object> SearchComponentCatalogAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        ReadBridgeQueryAsync(
            BridgeAdapterOwner.CordycepsCanvas,
            "canvas.catalog",
            arguments,
            cancellationToken);

    public Task<object> ListRhinoObjectsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        ReadBridgeQueryAsync(
            BridgeAdapterOwner.CordycepsRhino,
            "rhino.list",
            arguments,
            cancellationToken);

    public Task<object> InspectCanvasOutputsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        ReadBridgeQueryAsync(
            BridgeAdapterOwner.CordycepsCanvas,
            "canvas.inspectOutputs",
            arguments,
            cancellationToken);

    private async Task<object> ReadBridgeQueryAsync(
        BridgeAdapterOwner owner,
        string operation,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        using var documentRead = await _documentGate.EnterReadAsync(cancellationToken)
            .ConfigureAwait(false);
        RequireAdapter(owner);
        var request = new BridgeOperationRequest(
            $"read-{Guid.NewGuid():N}",
            owner,
            operation,
            BridgeOperationAccess.Read,
            _snapshot?.State.Revision ?? 0,
            ExpectedFingerprint: null,
            WriterLeaseToken: null,
            arguments.Clone());
        var response = await SendOperationAsync(request, cancellationToken).ConfigureAwait(false);
        return new
        {
            result = response.Result.Clone(),
            fingerprint = response.AfterFingerprint,
            diagnostics = response.Diagnostics
        };
    }

    private async Task<ScopedInspection> ReadInspectionScopeAsync(
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
        RequireAdapter(owner);
        var request = new BridgeOperationRequest(
            $"read-{Guid.NewGuid():N}",
            owner,
            operation,
            BridgeOperationAccess.Read,
            _snapshot?.State.Revision ?? 0,
            ExpectedFingerprint: null,
            WriterLeaseToken: null,
            arguments);
        var response = await SendOperationAsync(request, cancellationToken).ConfigureAwait(false);
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
        var changeSetElement = arguments.GetProperty("changeSet");
        var changeSet = changeSetElement.Deserialize<ChangeSet>(BridgeProtocol.JsonOptions)
            ?? throw new InvalidOperationException("changeSet cannot be null.");
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
        ValidateExpectationCoverage(changeSet, draftOperations);
        var requestHash = ComputeAcceptedRequestHash(
            changeSet,
            expectedSnapshotId,
            summary,
            draftOperations);
        var idempotencyScope = IdempotencyScope(session.Id, idempotencyKey);
        await _submissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_idempotency.TryGetValue(idempotencyScope, out var existingId) &&
                _jobs.TryGetValue(existingId, out var existing))
            {
                RequireMatchingRequestHash(existing.RequestHash, requestHash, idempotencyKey);
                return ProjectJob(existing, duplicate: true);
            }
        }
        finally
        {
            _submissionGate.Release();
        }

        SnapshotEnvelope snapshot;
        using (await _documentGate.EnterReadAsync(cancellationToken).ConfigureAwait(false))
        {
            snapshot = await CaptureSnapshotAsync(force: true, cancellationToken).ConfigureAwait(false);
        }
        // "gptino:auto" opts out of the whole-document snapshot/revision gate; per-resource auto expectations
        // (resolved at execute time against this session's own last-committed fingerprints) then govern every
        // resource the ChangeSet touches, so a foreign change to an UNRELATED resource no longer false-rejects.
        if (!string.Equals(expectedSnapshotId, ResourceExpectation.AutoFingerprint, StringComparison.Ordinal) &&
            !string.Equals(expectedSnapshotId, snapshot.SnapshotId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Snapshot changed. Expected '{expectedSnapshotId}', current is '{snapshot.SnapshotId}'.");
        }
        if (changeSet.BaseSnapshotRevision != ResourceExpectation.AutoBaseRevision &&
            changeSet.BaseSnapshotRevision != snapshot.State.Revision)
        {
            throw new InvalidOperationException(
                $"ChangeSet base revision {changeSet.BaseSnapshotRevision} does not match current revision {snapshot.State.Revision}.");
        }

        await RefreshScheduleAsync(cancellationToken).ConfigureAwait(false);
        LiveJobEntry entry;
        await _submissionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_idempotency.TryGetValue(idempotencyScope, out var existingId) &&
                _jobs.TryGetValue(existingId, out var existing))
            {
                RequireMatchingRequestHash(existing.RequestHash, requestHash, idempotencyKey);
                return ProjectJob(existing, duplicate: true);
            }

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

            var conflicts = DetectQueuedConflicts(frozenChangeSet);
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
                conflicts);
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
                        requestHash),
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
                    return ProjectJob(existing, duplicate: true);
                }

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
                return ProjectJob(entry, duplicate: true);
            }

            if (!_jobs.TryAdd(jobId, entry) || !_idempotency.TryAdd(idempotencyScope, jobId))
            {
                _jobs.TryRemove(jobId, out _);
                _idempotency.TryRemove(idempotencyScope, out _);
                throw new InvalidOperationException(
                    "The change was durably accepted but could not be registered in the live queue. " +
                    "Restart AgentHost to expose it as recovery-required.");
            }
        }
        finally
        {
            _submissionGate.Release();
        }

        var ticket = _broker.Enqueue(entry.Job);
        TrackCompletion(entry, ticket.Completion);
        _events.Publish();
        return ProjectJob(entry, duplicate: false);
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
                entry.Job.EnqueuedAt))
            .Where(item => item.State is
                JobState.Queued or JobState.Validating or JobState.Executing or JobState.Verifying)
            .OrderBy(item => item.State is JobState.Executing or JobState.Verifying ? 0 : 1)
            .ThenBy(item => rank.GetValueOrDefault(item.SessionId, int.MaxValue))
            .ThenBy(item => item.EnqueueSequence)
            .ToArray();
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
            .Select(entry => new LiveProblemItem(
                entry.Job.JobId,
                entry.Job.ChangeSet.SessionId,
                entry.Summary,
                entry.State,
                entry.Message,
                entry.UpdatedAt))
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
        try
        {
            var before = await CaptureSnapshotAsync(force: true, execution.Token).ConfigureAwait(false);
            var preparedOperations = await PreflightFrozenOperationsAsync(
                entry,
                execution.Token).ConfigureAwait(false);
            before = await EnrichSnapshotForConflictValidationAsync(
                before,
                job.ChangeSet,
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
                await SetJobPhaseAsync(entry, JobState.Blocked, message).ConfigureAwait(false);
                return new JobExecutionResult(job.JobId, JobState.Blocked, message);
            }

            await PreflightBridgePayloadsAsync(
                preparedOperations,
                before.State.Revision,
                execution.Token).ConfigureAwait(false);

            await EnsureHistoryBaselineAsync(before, execution.Token).ConfigureAwait(false);
            var lease = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await SetJobPhaseAsync(
                entry,
                JobState.Executing,
                "Applying typed operations through the document bridge.").ConfigureAwait(false);
            _broker.RecordJobState(job.JobId, JobState.Executing);
            _events.Publish();

            var diagnostics = new List<BridgeDiagnostic>();
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
                var response = await SendOperationAsync(request, execution.Token).ConfigureAwait(false);
                liveChanged |= response.Changed;
                diagnostics.AddRange(response.Diagnostics);
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
                if (error is not null)
                {
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
            var after = await CaptureSnapshotAsync(force: true, execution.Token).ConfigureAwait(false);
            var verificationProblems = Verify(
                job.ChangeSet,
                after,
                diagnostics,
                operationObservations);
            if (verificationProblems.Count > 0)
            {
                var message = string.Join(" ", verificationProblems);
                await SetJobPhaseAsync(
                    entry,
                    liveChanged ? JobState.RecoveryRequired : JobState.Failed,
                    message).ConfigureAwait(false);
                return new JobExecutionResult(
                    job.JobId,
                    liveChanged ? JobState.RecoveryRequired : JobState.Failed,
                    message);
            }

            try
            {
                await CommitHistoryAsync(entry, after, execution.Token).ConfigureAwait(false);
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
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Chaining data is observability sugar. The live change is verified and
                // committed at this point; a projection bug must never demote the job.
                _logger.LogWarning(exception, "Could not build the committed chaining view for job {JobId}.", job.JobId);
            }
            try
            {
                // Record each written resource's committed fingerprint + this session as its last writer, so a
                // later gptino:auto expectation from the SAME session can be self-resolved (and a foreign write
                // afterwards flips the ledger owner, forcing that auto to Block). Same never-demote discipline
                // as the committed-view build above. Runs on the broker worker thread, so no lock is needed.
                var beforeFingerprints = before.State.Resources.ToDictionary(
                    item => $"{item.Resource.Kind}:{item.Resource.Id}:{item.Resource.Field}",
                    item => item.Fingerprint,
                    StringComparer.Ordinal);
                foreach (var resource in after.State.Resources.Where(item =>
                    !string.IsNullOrWhiteSpace(item.Fingerprint)))
                {
                    // Attribute every resource this job actually changed (new, or a moved fingerprint) to this
                    // session — including SIDE EFFECTS that never appear in the writeSet, such as a wire connecting
                    // into a component's inputs, which moves the component fingerprint. This keeps a later
                    // sub-domain gptino:auto (setComponentIo/executePython) self-resolvable via its parent, and
                    // makes the committing session the ledger owner so a foreign change flips ownership and Blocks.
                    var key = $"{resource.Resource.Kind}:{resource.Resource.Id}:{resource.Resource.Field}";
                    var changed = !beforeFingerprints.TryGetValue(key, out var beforeFingerprint) ||
                        !string.Equals(beforeFingerprint, resource.Fingerprint, StringComparison.Ordinal);
                    if (changed)
                    {
                        _resourceLedger[key] = new ResourceLedgerEntry(
                            resource.Resource,
                            resource.Fingerprint!,
                            job.ChangeSet.SessionId,
                            after.State.Revision);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Could not update the resource ledger for job {JobId}.", job.JobId);
            }
            await SetJobPhaseAsync(
                entry,
                JobState.Committed,
                "Verified and committed to managed history.").ConfigureAwait(false);
            return new JobExecutionResult(job.JobId, JobState.Committed, "Verified and committed.");
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            var state = liveChanged || writeMayHaveChanged ? JobState.RecoveryRequired : JobState.Cancelled;
            var message = liveChanged || writeMayHaveChanged
                ? "Execution stopped after a live change; review or recovery is required."
                : "Execution stopped before a live change was applied.";
            await SetJobPhaseAsync(entry, state, message).ConfigureAwait(false);
            return new JobExecutionResult(job.JobId, state, message);
        }
        catch (Exception exception)
        {
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
            _target = null;
        }
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        _snapshotGate.Dispose();
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

    /// <summary>
    /// Latest Rhino selection pushed by the plugin, or null before the first push.
    /// A discovery hint for turn context and the panel — never concurrency control.
    /// </summary>
    public SelectionChangedEvent? CurrentSelection => Volatile.Read(ref _currentSelection);

    /// <summary>Digest of the last captured snapshot; null before the first capture.</summary>
    public CanvasDigest? CurrentCanvasDigest
    {
        get
        {
            lock (_connectionGate)
            {
                return _snapshot is null
                    ? null
                    : new CanvasDigest(_snapshot.State.Revision, _snapshot.Canvas.Objects.Count);
            }
        }
    }

    private void CacheSelection(BridgeFrame frame)
    {
        var target = frame.Target;
        if (target is null)
        {
            return;
        }
        lock (_connectionGate)
        {
            if (_target is null ||
                !string.Equals(
                    _target.StableTargetKey(),
                    target.StableTargetKey(),
                    StringComparison.Ordinal))
            {
                return;
            }
        }
        var selection = frame.DeserializePayload<SelectionChangedEvent>();
        Volatile.Write(ref _currentSelection, selection);
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
            lock (_connectionGate)
            {
                if (_target is not null &&
                    !string.Equals(
                        _target.StableTargetKey(),
                        requestedTarget.StableTargetKey(),
                        StringComparison.Ordinal))
                {
                    throw new BridgeProtocolException(
                        "one_target_only",
                        "This AgentHost is already bound to a different Rhino/Grasshopper pair.");
                }
                if (_target is not null && requestedTarget.Generation < _target.Generation)
                {
                    throw new BridgeProtocolException(
                        "stale_generation",
                        "Document registration generation is older than the current target.");
                }

                _target = requestedTarget;
                _availableAdapters = request.AvailableAdapters.ToHashSet();
                if (_snapshot is not null &&
                    !string.Equals(
                        _snapshot.State.Target.Identity,
                        requestedTarget.Identity,
                        StringComparison.Ordinal))
                {
                    _snapshot = null;
                }
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
        lock (_connectionGate)
        {
            if (target is not null && _target is not null &&
                string.Equals(target.StableTargetKey(), _target.StableTargetKey(), StringComparison.Ordinal))
            {
                _target = null;
                _availableAdapters.Clear();
                _snapshot = null;
            }
        }
        FailPending(new IOException("The bound document was closed."));
        _events.Publish();
    }

    private void Disconnect(DocumentPipeConnection? connection, string reason)
    {
        lock (_connectionGate)
        {
            if (connection is null || ReferenceEquals(_connection, connection))
            {
                _connection = null;
                _target = null;
                _availableAdapters.Clear();
                _snapshot = null;
            }
        }
        FailPending(new IOException(reason));
        _events.Publish();
    }

    private void CompletePending(BridgeFrame frame)
    {
        if (frame.CorrelationId is not { } correlationId ||
            !_pending.TryRemove(correlationId, out var completion))
        {
            _logger.LogWarning("Ignoring bridge response without a known correlation id.");
            return;
        }

        try
        {
            var current = RequireTarget();
            DocumentTargetGuard.RequireCurrent(current, frame.Target!);
            if (frame.Kind == BridgeMessageKind.Error)
            {
                var failure = frame.DeserializePayload<BridgeFailure>();
                completion.TrySetException(new BridgeProtocolException(failure.Code, failure.Message));
            }
            else
            {
                completion.TrySetResult(frame);
            }
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private void FailPending(Exception exception)
    {
        foreach (var pair in _pending.ToArray())
        {
            if (_pending.TryRemove(pair.Key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private async Task<BridgeFrame> SendRequestAsync(
        string payloadType,
        object payload,
        CancellationToken cancellationToken)
    {
        DocumentPipeConnection connection;
        DocumentRuntime target;
        lock (_connectionGate)
        {
            connection = _connection is { IsConnected: true } active
                ? active
                : throw new InvalidOperationException("The Rhino/Grasshopper bridge is not connected.");
            target = _target ?? throw new InvalidOperationException("No explicit document target is registered.");
        }

        var frame = BridgeFrame.Create(
            BridgeMessageKind.Request,
            payloadType,
            payload,
            target);
        var completion = new TaskCompletionSource<BridgeFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(frame.MessageId, completion))
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
        BridgeOperationRequest request,
        CancellationToken cancellationToken)
    {
        var frame = await SendRequestAsync(
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
        bool force,
        CancellationToken cancellationToken)
    {
        if (!force && _snapshot is { } existing &&
            DateTimeOffset.UtcNow - existing.State.CapturedAt < TimeSpan.FromMilliseconds(250))
        {
            return existing;
        }

        await _snapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!force && _snapshot is { } cached &&
                DateTimeOffset.UtcNow - cached.State.CapturedAt < TimeSpan.FromMilliseconds(250))
            {
                return cached;
            }

            RequireAdapter(BridgeAdapterOwner.CordycepsCanvas);
            var currentTarget = RequireTarget();
            var request = BridgeOperationRequest.Create(
                $"snapshot-{Guid.NewGuid():N}",
                BridgeAdapterOwner.CordycepsCanvas,
                "canvas.snapshot",
                BridgeOperationAccess.Read,
                _snapshot?.State.Revision ?? 0,
                new { });
            var response = await SendOperationAsync(request, cancellationToken).ConfigureAwait(false);
            var canvas = response.Result.Deserialize<CanvasSnapshot>(BridgeProtocol.JsonOptions)
                ?? throw new BridgeProtocolException("snapshot_payload", "Canvas snapshot payload was null.");
            if (canvas.GrasshopperDocumentId != currentTarget.GrasshopperDocumentId)
            {
                throw new BridgeProtocolException(
                    "snapshot_target",
                    "Canvas snapshot belongs to a different Grasshopper document.");
            }

            var previous = _snapshot;
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
                _history.ReadHead(),
                DateTimeOffset.UtcNow,
                currentTarget,
                BuildResources(currentTarget, canvas));
            var snapshotId = BuildSnapshotId(state, canvas.DocumentFingerprint);
            var envelope = new SnapshotEnvelope(snapshotId, state, canvas);
            _snapshot = envelope;
            if (!sameFingerprint)
            {
                _events.Publish();
            }
            return envelope;
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

    private static IReadOnlyList<ResourceFingerprint> BuildResources(
        DocumentRuntime target,
        CanvasSnapshot canvas)
    {
        var resources = new List<ResourceFingerprint>
        {
            new(
                new ResourceAddress(ResourceKind.Document, target.ProjectId.ToString("N")),
                canvas.DocumentFingerprint)
        };
        foreach (var item in canvas.Objects)
        {
            var id = item.ObjectId.ToString("D");
            resources.Add(new ResourceFingerprint(
                new ResourceAddress(ResourceKind.GrasshopperComponent, id),
                item.Fingerprint));
            resources.Add(new ResourceFingerprint(
                new ResourceAddress(ResourceKind.GrasshopperComponentLayout, id),
                item.Fingerprint));
            if (item.ValueJson is not null)
            {
                resources.Add(new ResourceFingerprint(
                    new ResourceAddress(ResourceKind.GrasshopperComponentValue, id),
                    item.Fingerprint));
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
            .Select(scope => ReadInspectionScopeAsync(scope, cancellationToken))).ConfigureAwait(false);
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
        ResourceAddress resource,
        CancellationToken cancellationToken)
    {
        var objectId = Guid.Parse(resource.Id);
        RequireAdapter(BridgeAdapterOwner.CordycepsRhino);
        var request = new BridgeOperationRequest(
            $"absence-{Guid.NewGuid():N}",
            BridgeAdapterOwner.CordycepsRhino,
            "rhino.list",
            BridgeOperationAccess.Read,
            _snapshot?.State.Revision ?? 0,
            ExpectedFingerprint: null,
            WriterLeaseToken: null,
            JsonSerializer.SerializeToElement(
                new RhinoListObjectsRequest(Limit: 1, ObjectId: objectId),
                BridgeProtocol.JsonOptions));
        var response = await SendOperationAsync(request, cancellationToken).ConfigureAwait(false);
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
        SnapshotEnvelope snapshot,
        CancellationToken cancellationToken)
    {
        await _historyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_history.IsInitialized)
            {
                var verification = _history.Verify();
                if (!verification.IsValid)
                {
                    throw new InvalidOperationException(
                        $"Managed history is invalid: {string.Join("; ", verification.Problems)}");
                }
                return;
            }

            await _history.InitializeBaselineAsync(
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
        SnapshotEnvelope snapshot,
        CancellationToken cancellationToken)
    {
        await _historyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var changeJson = JsonSerializer.Serialize(entry.Job.ChangeSet, BridgeProtocol.JsonOptions);
            var request = HistoryCommitRequest.Create(
                _history.ReadHead(),
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
            var result = await _history.CommitAsync(request, cancellationToken).ConfigureAwait(false);
            var committedState = snapshot.State with { GitCommit = result.Head };
            _snapshot = snapshot with { State = committedState };
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

        ValidateExpectationCoverage(entry.Job.ChangeSet, prepared);
        foreach (var owner in prepared.Select(item => item.Owner).Distinct())
        {
            RequireAdapter(owner);
        }
        return prepared;
    }

    private async Task PreflightBridgePayloadsAsync(
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
            var response = await SendOperationAsync(request, cancellationToken).ConfigureAwait(false);
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
        var parameters = request.Inputs.Concat(request.Outputs).ToArray();
        if (parameters.Any(parameter =>
                parameter is null || parameter.ParameterId == Guid.Empty ||
                string.IsNullOrWhiteSpace(parameter.Name) ||
                string.IsNullOrWhiteSpace(parameter.NickName) ||
                string.IsNullOrWhiteSpace(parameter.TypeHint)) ||
            parameters.Select(parameter => parameter.ParameterId).Distinct().Count() != parameters.Length)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' has invalid or duplicate Python parameters.");
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

    private void RequireAdapter(BridgeAdapterOwner owner)
    {
        lock (_connectionGate)
        {
            if (_target is null || _connection is not { IsConnected: true })
            {
                throw new InvalidOperationException("The Rhino/Grasshopper bridge is not connected.");
            }
            if (!_availableAdapters.Contains(owner))
            {
                throw new InvalidOperationException(
                    $"The bound document does not advertise adapter '{owner}'.");
            }
        }
    }

    private DocumentRuntime RequireTarget()
    {
        lock (_connectionGate)
        {
            return _target ?? throw new InvalidOperationException("No explicit document target is registered.");
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
                    $"gptino:auto declined for {key}: this session has not committed it, so there is no " +
                    "baseline to fill. Run snapshot_read and supply the concrete fingerprint once.");
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
        IReadOnlyList<BridgeDiagnostic> diagnostics,
        IReadOnlyList<ResourceObservation> operationObservations)
    {
        var problems = diagnostics
            .Where(item => item.Severity == BridgeDiagnosticSeverity.Error)
            .Select(item => $"{item.Code}: {item.Message}")
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
                    "Use runtimeErrorAbsent for value/move/python writes instead of predicting outcomes.");
            }
        }
        return problems;
    }

    private IReadOnlyList<QueuedConflict> DetectQueuedConflicts(ChangeSet changeSet)
    {
        return _jobs.Values
            .Where(entry => IsActive(entry.State))
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
        string? message)
    {
        var phase = state.ToString().ToLowerInvariant();
        await _jobStore.UpdateStateAsync(
            entry.Job.JobId,
            state,
            phase,
            message,
            CancellationToken.None).ConfigureAwait(false);
        entry.SetPhase(state, phase, message);
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
            Array.Empty<QueuedConflict>());
        entry.SetPhase(record.State, record.Phase, record.Message, record.UpdatedAt);
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
        }
        catch (OperationCanceledException)
        {
            await SetJobPhaseAsync(
                entry,
                JobState.RecoveryRequired,
                "AgentHost stopped before this job reached a durable terminal state. " +
                "No operations will be replayed automatically; inspect the document before recovery.")
                .ConfigureAwait(false);
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
        var committed = entry.Committed;
        return new
        {
            jobId = entry.Job.JobId,
            sessionId = entry.Job.ChangeSet.SessionId,
            changeSetId = entry.Job.ChangeSet.ChangeSetId,
            state = entry.State.ToString().ToLowerInvariant(),
            phase = entry.Phase,
            message = entry.Message,
            duplicate,
            enqueueSequence = entry.Job.EnqueueSequence,
            committed = committed is null
                ? null
                : new
                {
                    snapshotId = committed.SnapshotId,
                    revision = committed.Revision,
                    resources = committed.Resources.Select(item => new
                    {
                        kind = item.Resource.Kind,
                        id = item.Resource.Id,
                        field = item.Resource.Field,
                        fingerprint = item.Fingerprint
                    }).ToArray()
                },
            conflictsWith = entry.Conflicts.Select(item => new
            {
                jobId = item.OtherJobId,
                kind = item.Conflict.Kind.ToString().ToLowerInvariant(),
                resource = item.Conflict.Resource,
                item.Conflict.Message
            }).ToArray()
        };
    }

    private static CommittedJobView BuildCommittedJobView(ChangeSet changeSet, SnapshotEnvelope after)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var resources = new List<CommittedResourceFingerprint>();
        foreach (var expectation in changeSet.WriteSet)
        {
            var key = $"{expectation.Resource.Kind}:{expectation.Resource.Id}:{expectation.Resource.Field}";
            if (!seen.Add(key))
            {
                continue;
            }
            var current = after.State.Resources.FirstOrDefault(item =>
                ExactDomainOverlaps(item.Resource, expectation.Resource));
            resources.Add(new CommittedResourceFingerprint(expectation.Resource, current?.Fingerprint));
        }
        return new CommittedJobView(after.SnapshotId, after.State.Revision, resources);
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
        IReadOnlyList<PreparedOperation> prepared)
    {
        foreach (var expectation in changeSet.ReadSet.Concat(changeSet.WriteSet))
        {
            ValidateResourceAddress(expectation.Resource, changeSet.ProjectId);
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
                ValidateResourceAddress(resource, changeSet.ProjectId);
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
                ValidateResourceAddress(resource, changeSet.ProjectId);
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
            ValidateResourceAddress(predicate.Resource!, changeSet.ProjectId);
            if (!prepared.SelectMany(item => item.Operation.Reads.Concat(item.Operation.Writes))
                    .Any(resource => ExactDomainOverlaps(resource, predicate.Resource!)))
            {
                throw new InvalidOperationException(
                    $"Acceptance predicate '{predicate.Name}' targets a resource not declared by any operation.");
            }
        }
        foreach (var beforeImage in changeSet.RollbackBeforeImages)
        {
            ValidateResourceAddress(beforeImage.Resource, changeSet.ProjectId);
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

    private static void ValidateResourceAddress(ResourceAddress resource, Guid projectId)
    {
        if (string.IsNullOrWhiteSpace(resource.Id) || resource.Field != "*")
        {
            throw new InvalidOperationException(
                "Resource addresses require a canonical id and the whole-domain '*' field.");
        }

        if (resource.Kind == ResourceKind.Document)
        {
            if (!string.Equals(resource.Id, projectId.ToString("N"), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Document resource ids must be the canonical bound project UUID in N format.");
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
                $"Idempotency key '{idempotencyKey}' is already bound to a different accepted request.");
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
        IReadOnlyList<QueuedConflict> conflicts)
    {
        private readonly object _gate = new();
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

        /// <summary>Written once by the single-writer executor just before Committed.</summary>
        public CommittedJobView? Committed { get; set; }

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
