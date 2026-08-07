using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Codex;
using GPTino.AgentHost.Data;
using GPTino.AgentHost.Hosting;
using GPTino.AgentHost.Security;
using GPTino.BridgeContract;
using GPTino.Contracts;
using GPTino.CanvasSceneAdapter;
using GPTino.Core;
using GPTino.History;
using GPTino.ScriptAdapter;

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
public sealed partial class LiveDocumentBackend : BackgroundService, ILiveDocumentBackend,
    ILiveDocumentQueueControl, IJobExecutor, ISelectionContextSource, ILayoutTidyService
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
    // Per-session set of canvas object ids created during the CURRENT turn (new objects seen between a
    // committed ChangeSet's before/after snapshots). BeginTurn resets it; the orchestrator drains it at
    // turn end to seed the automatic layout tidy. Each set is guarded by locking on itself.
    private readonly ConcurrentDictionary<Guid, HashSet<Guid>> _turnCreatedComponents = new();
    // Per-session recovery halt latch (see SessionHaltState). Set through the SetJobPhaseAsync
    // funnel / the restart restore path; cleared only by an explicit resume naming the halt job.
    private readonly ConcurrentDictionary<Guid, SessionHaltState> _sessionHalts = new();
    // Jobs the halt paths (latch sweep / submit enqueue re-check) are cancelling, mapped to the
    // halting job's id. Marked BEFORE broker TryCancel so the marker happens-before the completion
    // observer wakes; consumed (removed) ONLY inside ObserveCompletionAsync, which is the single
    // writer of the durable Cancelled/"halted-by-recovery" teaching record — two racing halt paths
    // can therefore never strip each other's marker or clobber the record.
    private readonly ConcurrentDictionary<Guid, Guid> _haltCancelledJobs = new();
    // Last terminal job state per session: the post-turn auto-tidy consults it so a turn whose
    // last job ended Failed/Blocked never has its half-applied canvas rearranged.
    private readonly ConcurrentDictionary<Guid, JobState> _lastTerminalJobStates = new();
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
                // A Rhino-only target has no Grasshopper document to list. It is a real registered
                // target (Rhino-side work runs on it), it just contributes no row here.
                return _targets.Values
                    .OrderBy(state => state.Sequence)
                    .Where(state => state.Target.GrasshopperPath is not null)
                    .Select(state => new RegisteredGrasshopperDocument(
                        state.DocKey,
                        state.Target.GrasshopperPath!))
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
        // The full per-domain resources list and the whole-canvas dump are the heavy part of the
        // payload. Return them only for a full-document read — an empty scopes array (the default
        // orientation read) or one that explicitly asks for "canvas". When the caller narrows to
        // targeted inspection scopes (script:<guid> / rhino:<guid>), omit the full document so a
        // large definition's unrelated JSON does not crowd the model's context.
        var wantsFullDocument = scopes.Length == 0 ||
            scopes.Any(scope => string.Equals(scope, "canvas", StringComparison.OrdinalIgnoreCase));
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
            resources = wantsFullDocument ? snapshot.State.Resources : null,
            canvas = wantsFullDocument ? snapshot.Canvas : null,
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
            BridgeAdapterOwner.Canvas,
            "canvas.catalog",
            arguments,
            cancellationToken);

    public Task<object> ListRhinoObjectsAsync(
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        ReadBridgeQueryAsync(
            RequireDefaultTargetState(),
            BridgeAdapterOwner.RhinoScene,
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
            BridgeAdapterOwner.Canvas,
            "canvas.inspectOutputs",
            WithMassProperties(arguments),
            cancellationToken);
    }

    // An explicit inspect_outputs read is a deliberate, low-frequency call — unlike the per-job Verify
    // path, which requests mass properties only when a predicate needs them — so it always asks for the
    // full area/volume semantics, preserving the model's view regardless of what it passed.
    private static JsonElement WithMassProperties(JsonElement arguments)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(arguments.GetRawText())?.AsObject()
            ?? throw new InvalidOperationException("inspect_outputs arguments must be a JSON object.");
        node["includeMassProperties"] = true;
        return JsonSerializer.SerializeToElement(node, BridgeProtocol.JsonOptions);
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

    private sealed record ApprovalGrantRecord(
        string GrantId,
        IReadOnlyDictionary<Guid, string> Items,
        DateTimeOffset ExpiresAt);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ApprovalGrantRecord> _approvalGrants =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Mints a user-approval grant bound to exactly the (objectId, fingerprint) pairs the panel's
    /// approval card displayed. Grants are the ONLY way destructive ops reach objects the user
    /// made (no GPTino provenance stamp); they expire so a stale card cannot authorize later work,
    /// and coverage is per-object AND per-fingerprint, so anything that changed since the card was
    /// shown simply is not covered (approve-what-you-saw, TOCTOU-safe on top of CAS).
    /// </summary>
    public object MintApprovalGrant(IReadOnlyList<(Guid ObjectId, string Fingerprint)> items)
    {
        if (items is null || items.Count == 0)
        {
            throw new ArgumentException("An approval grant needs at least one (objectId, fingerprint) item.");
        }
        var bound = new Dictionary<Guid, string>();
        foreach (var (objectId, fingerprint) in items)
        {
            if (objectId == Guid.Empty || string.IsNullOrWhiteSpace(fingerprint))
            {
                throw new ArgumentException("Approval grant items need a non-empty objectId and fingerprint.");
            }
            bound[objectId] = fingerprint;
        }
        foreach (var stale in _approvalGrants.Values.Where(grant => grant.ExpiresAt < DateTimeOffset.UtcNow).ToArray())
        {
            _approvalGrants.TryRemove(stale.GrantId, out _);
        }
        var grantId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        _approvalGrants[grantId] = new ApprovalGrantRecord(grantId, bound, expiresAt);
        return new { grantId, expiresAt };
    }

    /// <summary>
    /// The fix op verifies the anchor's audited fingerprint at execution; another operation in the
    /// SAME ChangeSet writing that anchor would invalidate it mid-batch, and the writes-vs-writes
    /// overlap rules do not see read/write collisions.
    /// </summary>
    internal static void RejectWritesOnEndpointFixAnchors(ChangeSet changeSet)
    {
        var anchorIds = changeSet.Operations
            .Where(operation => operation.Kind == OperationKind.FixRhinoEndpointPair)
            .SelectMany(operation => operation.Reads
                .Where(read => read.Kind == ResourceKind.RhinoObject)
                .Select(read => read.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (anchorIds.Count == 0)
        {
            return;
        }
        foreach (var operation in changeSet.Operations)
        {
            foreach (var write in operation.Writes)
            {
                if (write.Kind == ResourceKind.RhinoObject && anchorIds.Contains(write.Id))
                {
                    throw new InvalidOperationException(
                        $"Operation '{operation.OperationId}' writes Rhino object {write.Id}, which " +
                        "another operation in this ChangeSet uses as an endpoint-fix ANCHOR; the " +
                        "anchor's audited fingerprint would be invalidated mid-batch. Submit the " +
                        "anchor write in a separate ChangeSet.");
                }
            }
        }
    }

    /// <summary>
    /// An approval is consent for ONE application: once the covered objects' destructive writes
    /// commit, the grant stops covering them — a user Undo restores the audited fingerprints, and
    /// an unconsumed grant would let a replay override that human revert without fresh consent.
    /// </summary>
    private void ConsumeApprovalGrant(LiveJobEntry entry)
    {
        if (entry.ApprovalGrantId is null || entry.ApprovalItems is null ||
            !_approvalGrants.TryGetValue(entry.ApprovalGrantId, out var grant))
        {
            return;
        }
        var writtenObjectIds = entry.Job.ChangeSet.WriteSet
            .Where(expectation => expectation.Resource.Kind == ResourceKind.RhinoObject)
            .Select(expectation => Guid.TryParse(expectation.Resource.Id, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        if (writtenObjectIds.Count == 0)
        {
            return;
        }
        var remaining = grant.Items
            .Where(item => !writtenObjectIds.Contains(item.Key))
            .ToDictionary(item => item.Key, item => item.Value);
        if (remaining.Count == 0)
        {
            _approvalGrants.TryRemove(grant.GrantId, out _);
        }
        else
        {
            _approvalGrants[grant.GrantId] = grant with { Items = remaining };
        }
    }

    private IReadOnlyDictionary<Guid, string>? ResolveApprovalGrant(string? grantId)
    {
        if (string.IsNullOrWhiteSpace(grantId))
        {
            return null;
        }
        if (!_approvalGrants.TryGetValue(grantId.Trim(), out var grant) ||
            grant.ExpiresAt < DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException(
                $"Approval grant '{grantId}' is unknown or expired. Ask the user to re-approve on the " +
                "panel's audit card and resubmit with the fresh grant id.");
        }
        return grant.Items;
    }

    /// <summary>
    /// Bridge failure code the Rhino adapter raises when destructive work targets an object the
    /// user made and no approval grant covers it (RhinoSceneFoundationAdapter.ApprovalRequiredCode
    /// — duplicated here because the AgentHost does not reference the Rhino plugin assembly).
    /// </summary>
    private const string ApprovalRequiredFailureCode = "approval_required";

    /// <summary>
    /// Deterministic PRE-WRITE refusal from the Rhino adapter (layer not empty, block still
    /// referenced, style still current…). Nothing was applied, so this is a plain failure the
    /// session can act on — not a recoveryRequired document review.
    /// </summary>
    private const string PreconditionRefusedFailureCode = "precondition_refused";

    /// <summary>
    /// Deterministic POST-write failure whose in-place rollback the adapter VERIFIED by reading
    /// the restored state back (GrasshopperPythonFoundationAdapter.MutationRolledBackCode —
    /// duplicated here because the AgentHost does not reference the Grasshopper plugin assembly).
    /// With no completed sibling operation and no live change there is nothing left to review:
    /// classify as a deterministic Failed the session can act on, not RecoveryRequired.
    /// </summary>
    private const string MutationRolledBackFailureCode = "mutation_rolled_back";

    // Destructive rhino ops that honor the user-approval flag; the flag is injected ONLY when the
    // grant covers the op's target object at its exact audited fingerprint.
    private static readonly string[] ApprovableOperations =
    {
        "rhino.delete", "rhino.transform", "rhino.upsert", "rhino.fixEndpointPair",
        // Quarantining a user-made object moves it — without this the panel would mint a grant
        // the executor never applies and every quarantine would be refused as unapproved.
        "rhino.moveObjectsToLayer",
    };

    internal static IReadOnlyList<PreparedOperation> InjectApprovalFlags(
        IReadOnlyList<PreparedOperation> operations,
        IReadOnlyDictionary<Guid, string>? approvalItems)
    {
        if (approvalItems is null || approvalItems.Count == 0 ||
            !operations.Any(operation => ApprovableOperations.Contains(operation.BridgeOperation)))
        {
            return operations;
        }
        var result = new List<PreparedOperation>(operations.Count);
        foreach (var operation in operations)
        {
            if (!ApprovableOperations.Contains(operation.BridgeOperation))
            {
                result.Add(operation);
                continue;
            }
            var node = System.Text.Json.Nodes.JsonNode.Parse(operation.Arguments.GetRawText())?.AsObject()
                ?? throw new InvalidOperationException(
                    $"{operation.BridgeOperation} arguments must be a JSON object.");
            bool covered;
            if (operation.BridgeOperation == "rhino.moveObjectsToLayer")
            {
                // A batch is approved only when EVERY moved object is covered at its audited
                // fingerprint: a partially covered batch must be refused, not half-authorized.
                var items = node["items"]?.AsArray();
                covered = items is { Count: > 0 } && items.All(item =>
                    item?["objectId"]?.GetValue<string>() is { } itemId &&
                    Guid.TryParse(itemId, out var movedId) &&
                    approvalItems.TryGetValue(movedId, out var approvedItemFingerprint) &&
                    item?["expectedFingerprint"]?.GetValue<string>() is { } itemFingerprint &&
                    string.Equals(itemFingerprint, approvedItemFingerprint, StringComparison.Ordinal));
            }
            else
            {
                var idProperty = operation.BridgeOperation == "rhino.fixEndpointPair" ? "moveObjectId" : "objectId";
                covered =
                    node[idProperty]?.GetValue<string>() is { } idText &&
                    Guid.TryParse(idText, out var objectId) &&
                    approvalItems.TryGetValue(objectId, out var approvedFingerprint) &&
                    node["expectedFingerprint"]?.GetValue<string>() is { } fingerprint &&
                    string.Equals(fingerprint, approvedFingerprint, StringComparison.Ordinal);
            }
            if (!covered)
            {
                result.Add(operation);
                continue;
            }
            node["approved"] = true;
            result.Add(operation with
            {
                Arguments = JsonSerializer.SerializeToElement(node, BridgeProtocol.JsonOptions)
            });
        }
        return result;
    }

    /// <summary>
    /// One data-flow summary per registered GH document: what it references from the Rhino
    /// document (with broken-reference count) and what it has baked back. Eventually consistent —
    /// refreshed after commits, on document registration, and whenever a detail read runs; a stale
    /// entry can never cause a wrong write because mutations are still CAS-gated per resource.
    /// </summary>
    public sealed record DataFlowSummary(
        string DocId,
        int ReferenceCount,
        int MissingReferenceCount,
        int BakeCount,
        long Revision,
        DateTimeOffset ObservedAt);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DataFlowSummary> _dataFlowSummaries =
        new(StringComparer.OrdinalIgnoreCase);
    private int _unattributedBakeCount;
    private int _dataFlowRefreshRunning;
    private int _dataFlowRefreshDirty;

    public IReadOnlyList<DataFlowSummary> DataFlowSummaries =>
        _dataFlowSummaries.Values.OrderBy(summary => summary.DocId, StringComparer.Ordinal).ToArray();

    public int UnattributedBakeCount => Volatile.Read(ref _unattributedBakeCount);

    /// <summary>
    /// Document-hygiene audit (rhino_audit tool + GET /audit). Like rhino_list, Rhino-scene reads
    /// are document-agnostic — default-target resolution.
    /// </summary>
    public Task<object> ReadRhinoAuditAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        ReadBridgeQueryAsync(
            RequireDefaultTargetState(),
            BridgeAdapterOwner.RhinoScene,
            "rhino.audit",
            arguments,
            cancellationToken);

    /// <summary>
    /// Structural member axis extraction (structural_extract tool). Rhino-scene read like the
    /// audit — document-agnostic, default-target resolution, detection is adapter code.
    /// </summary>
    public Task<object> ReadStructuralExtractAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        ReadBridgeQueryAsync(
            RequireDefaultTargetState(),
            BridgeAdapterOwner.RhinoScene,
            "rhino.structuralExtract",
            arguments,
            cancellationToken);

    /// <summary>
    /// Full layer table + named layer states (rhino_layers tool + GET /layers). Deterministic
    /// layer inspection: every layer carries a fingerprint and the table carries one, so presence
    /// AND absence are provable — the precondition layer mutation was gated on.
    /// </summary>
    /// <summary>
    /// Points the viewport at objects for the panel's audit card. Panel-only: it is a human
    /// pressing a finding, so it never enters a ChangeSet and the agent has no tool for it.
    /// </summary>
    public Task<object> FocusRhinoObjectsAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        ReadBridgeQueryAsync(
            RequireDefaultTargetState(),
            BridgeAdapterOwner.RhinoScene,
            "rhino.focusObjects",
            arguments,
            cancellationToken);

    /// <summary>
    /// Points the Grasshopper canvas at components for the panel's [[ghfocus:…]] chip: selects and
    /// frames them so the user can see what GPTino built. The canvas mirror of
    /// <see cref="FocusRhinoObjectsAsync"/> — panel-only, a read (never queued behind the writer),
    /// and absent from the agent's tool schema.
    /// </summary>
    public Task<object> FocusCanvasObjectsAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        ReadBridgeQueryAsync(
            RequireDefaultTargetState(),
            BridgeAdapterOwner.Canvas,
            "canvas.focusObjects",
            arguments,
            cancellationToken);

    public Task<object> ReadRhinoLayersAsync(CancellationToken cancellationToken)
    {
        using var empty = JsonDocument.Parse("{}");
        return ReadBridgeQueryAsync(
            RequireDefaultTargetState(),
            BridgeAdapterOwner.RhinoScene,
            "rhino.listLayers",
            empty.RootElement.Clone(),
            cancellationToken);
    }

    /// <summary>Session-scoped agent read (data_flow_read tool): honors the session's doc binding.</summary>
    public Task<object> ReadDataFlowAsync(SessionRecord session, CancellationToken cancellationToken) =>
        ReadDataFlowCoreAsync(ResolveSessionTargetState(session), cancellationToken);

    /// <summary>Panel read (GET /data-flow): explicit docKey, or the only registered doc.</summary>
    public Task<object> ReadDataFlowDetailAsync(string? docKey, CancellationToken cancellationToken) =>
        ReadDataFlowCoreAsync(
            ResolveTargetStateByDocKey(
                string.IsNullOrWhiteSpace(docKey) ? null : docKey.Trim(),
                "The data-flow read"),
            cancellationToken);

    private async Task<object> ReadDataFlowCoreAsync(TargetState targetState, CancellationToken cancellationToken)
    {
        // Same fail-fast rule as inspect_outputs: the document gate is writer-preferring, so
        // queuing this read behind an executing job would stall past tool deadlines.
        if (WriterSessionId is not null)
        {
            return new
            {
                writerActive = true,
                message = "A writer session currently holds the document; retry after the queue drains."
            };
        }
        using var documentRead = await _documentGate.EnterReadAsync(cancellationToken).ConfigureAwait(false);
        var references = await SendDataFlowReadAsync(
            targetState, BridgeAdapterOwner.Canvas, "canvas.listReferencedRhinoIds", cancellationToken)
            .ConfigureAwait(false);
        var stamped = await SendDataFlowReadAsync(
            targetState, BridgeAdapterOwner.RhinoScene, "rhino.listStampedObjects", cancellationToken)
            .ConfigureAwait(false);
        var revision = targetState.Snapshot?.State.Revision ?? 0;
        var observedAt = DateTimeOffset.UtcNow;
        if (UpdateDataFlowSummary(targetState.DocKey, references, stamped, revision, observedAt, RegisteredDocKeys()))
        {
            _events.Publish();
        }
        return new
        {
            docId = targetState.DocKey,
            revision,
            observedAt,
            references,
            bakes = stamped
        };
    }

    private async Task<JsonElement> SendDataFlowReadAsync(
        TargetState targetState,
        BridgeAdapterOwner owner,
        string operation,
        CancellationToken cancellationToken)
    {
        RequireAdapter(targetState, owner);
        using var emptyArguments = JsonDocument.Parse("{}");
        var request = new BridgeOperationRequest(
            $"read-{Guid.NewGuid():N}",
            owner,
            operation,
            BridgeOperationAccess.Read,
            targetState.Snapshot?.State.Revision ?? 0,
            ExpectedFingerprint: null,
            WriterLeaseToken: null,
            emptyArguments.RootElement.Clone());
        var response = await SendOperationAsync(targetState.Target, request, cancellationToken)
            .ConfigureAwait(false);
        return response.Result.Clone();
    }

    /// <summary>
    /// Best-effort, coalescing background refresh of every registered document's summary. Never
    /// throws; a failed refresh simply leaves the previous (stamped, dated) summary in place. A
    /// trigger landing while a refresh runs sets the dirty flag and the worker loops — signals
    /// are deferred, never dropped.
    /// </summary>
    internal void ScheduleDataFlowRefresh()
    {
        Volatile.Write(ref _dataFlowRefreshDirty, 1);
        if (Interlocked.CompareExchange(ref _dataFlowRefreshRunning, 1, 0) != 0)
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                while (Interlocked.Exchange(ref _dataFlowRefreshDirty, 0) == 1)
                {
                    // Head start for the broker: the post-commit trigger fires while the commit's
                    // write epoch still holds the document gate, and an immediate EnterReadAsync
                    // would queue at the writer-preferring turnstile AHEAD of the next queued job.
                    // The delay lets that writer reach the gate first and coalesces commit bursts.
                    await Task.Delay(400).ConfigureAwait(false);
                    try
                    {
                        await RefreshDataFlowCoreAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogDebug(exception, "Data-flow summary refresh failed; keeping previous summaries.");
                    }
                }
            }
            finally
            {
                Volatile.Write(ref _dataFlowRefreshRunning, 0);
                // A trigger that raced the loop exit would otherwise be stranded until the next one.
                if (Volatile.Read(ref _dataFlowRefreshDirty) == 1)
                {
                    ScheduleDataFlowRefresh();
                }
            }
        });
    }

    private async Task RefreshDataFlowCoreAsync(CancellationToken cancellationToken)
    {
        List<TargetState> targets;
        lock (_connectionGate)
        {
            targets = _targets.Values.OrderBy(state => state.Sequence).ToList();
        }
        if (targets.Count == 0)
        {
            var cleared = !_dataFlowSummaries.IsEmpty || Volatile.Read(ref _unattributedBakeCount) != 0;
            _dataFlowSummaries.Clear();
            Volatile.Write(ref _unattributedBakeCount, 0);
            if (cleared)
            {
                _events.Publish();
            }
            return;
        }
        var registeredKeys = RegisteredDocKeys();
        // Each bridge read takes the document gate for just its own round trip: holding it across
        // the whole sweep would make a writer arriving mid-refresh wait for every remaining doc.
        var stamped = await SendDataFlowReadGatedAsync(
            targets[0], BridgeAdapterOwner.RhinoScene, "rhino.listStampedObjects", cancellationToken)
            .ConfigureAwait(false);
        var changed = false;
        var liveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var targetState in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            liveKeys.Add(targetState.DocKey);
            var references = await SendDataFlowReadGatedAsync(
                targetState, BridgeAdapterOwner.Canvas, "canvas.listReferencedRhinoIds", cancellationToken)
                .ConfigureAwait(false);
            changed |= UpdateDataFlowSummary(
                targetState.DocKey,
                references,
                stamped,
                targetState.Snapshot?.State.Revision ?? 0,
                DateTimeOffset.UtcNow,
                registeredKeys);
        }
        foreach (var staleKey in _dataFlowSummaries.Keys.Where(key => !liveKeys.Contains(key)).ToArray())
        {
            changed |= _dataFlowSummaries.TryRemove(staleKey, out _);
        }
        if (changed)
        {
            _events.Publish();
        }
    }

    private async Task<JsonElement> SendDataFlowReadGatedAsync(
        TargetState targetState,
        BridgeAdapterOwner owner,
        string operation,
        CancellationToken cancellationToken)
    {
        using var documentRead = await _documentGate.EnterReadAsync(cancellationToken).ConfigureAwait(false);
        return await SendDataFlowReadAsync(targetState, owner, operation, cancellationToken).ConfigureAwait(false);
    }

    private HashSet<string> RegisteredDocKeys()
    {
        lock (_connectionGate)
        {
            return _targets.Values
                .Select(state => state.DocKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    private bool UpdateDataFlowSummary(
        string docKey,
        JsonElement references,
        JsonElement stamped,
        long revision,
        DateTimeOffset observedAt,
        IReadOnlySet<string> registeredDocKeys)
    {
        var referenceCount = ReadInt32(references, "referenceCount");
        var missingCount = ReadInt32(references, "missingCount");
        var bakeCount = 0;
        var unattributed = 0;
        if (stamped.ValueKind == JsonValueKind.Object &&
            stamped.TryGetProperty("groups", out var groups) &&
            groups.ValueKind == JsonValueKind.Array)
        {
            foreach (var group in groups.EnumerateArray())
            {
                var count = ReadInt32(group, "count");
                var sourceDocKey = group.TryGetProperty("sourceDocKey", out var keyProperty) &&
                    keyProperty.ValueKind == JsonValueKind.String
                        ? keyProperty.GetString()
                        : null;
                if (string.IsNullOrEmpty(sourceDocKey) || !registeredDocKeys.Contains(sourceDocKey))
                {
                    // Null keys predate provenance stamping; non-null keys matching no registered
                    // doc are orphans (Save As re-keyed the document, or a skill-derived key
                    // diverged). Both land in the honest unattributed bucket — dropping them
                    // silently would make tracked bakes vanish from every ledger surface.
                    unattributed += count;
                }
                else if (string.Equals(sourceDocKey, docKey, StringComparison.OrdinalIgnoreCase))
                {
                    bakeCount += count;
                }
            }
        }
        var previousUnattributed = Interlocked.Exchange(ref _unattributedBakeCount, unattributed);
        var next = new DataFlowSummary(docKey, referenceCount, missingCount, bakeCount, revision, observedAt);
        var changed = previousUnattributed != unattributed;
        if (_dataFlowSummaries.TryGetValue(docKey, out var previous))
        {
            changed |= previous.ReferenceCount != next.ReferenceCount ||
                previous.MissingReferenceCount != next.MissingReferenceCount ||
                previous.BakeCount != next.BakeCount ||
                previous.Revision != next.Revision;
        }
        else
        {
            // A first summary for a doc is always news to the panel.
            changed = true;
        }
        _dataFlowSummaries[docKey] = next;
        return changed;
    }

    private static int ReadInt32(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    // Both Rhino-object creation paths carry provenance: upsert (bakeGeometry and friends) AND
    // createPrimitive — the live gate caught an agent baking "one point" through the primitive op,
    // which would have left the object honestly-but-needlessly unattributed.
    private static readonly string[] SourceDocKeyStampedOperations = { "rhino.upsert", "rhino.createPrimitive" };

    internal static IReadOnlyList<PreparedOperation> InjectRhinoUpsertSourceDocKey(
        IReadOnlyList<PreparedOperation> operations,
        string docKey)
    {
        if (!operations.Any(operation => SourceDocKeyStampedOperations.Contains(operation.BridgeOperation)))
        {
            return operations;
        }
        var result = new List<PreparedOperation>(operations.Count);
        foreach (var operation in operations)
        {
            if (!SourceDocKeyStampedOperations.Contains(operation.BridgeOperation))
            {
                result.Add(operation);
                continue;
            }
            var node = System.Text.Json.Nodes.JsonNode.Parse(operation.Arguments.GetRawText())?.AsObject()
                ?? throw new InvalidOperationException(
                    $"{operation.BridgeOperation} arguments must be a JSON object.");
            node["sourceDocKey"] = docKey;
            result.Add(operation with
            {
                Arguments = JsonSerializer.SerializeToElement(node, BridgeProtocol.JsonOptions)
            });
        }
        return result;
    }

    private async Task<ScopedInspection> ReadInspectionScopeAsync(
        TargetState targetState,
        string scope,
        CancellationToken cancellationToken)
    {
        // Layer/table scopes ("rhinoTables:<kind>:<id>") resolve from one layer-table read rather
        // than a per-object inspect: layers and document-table entries appear in no snapshot, so
        // this is the only way their expectations survive conflict validation.
        if (scope.StartsWith("rhinoTables:", StringComparison.Ordinal))
        {
            return await ReadTableScopeAsync(targetState, scope, cancellationToken).ConfigureAwait(false);
        }

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
            "script" => (
                BridgeAdapterOwner.Script,
                "python.inspect",
                JsonSerializer.SerializeToElement(new { componentId = objectId }, BridgeProtocol.JsonOptions)),
            "script-messages" => (
                BridgeAdapterOwner.Script,
                "python.runtimeMessages",
                JsonSerializer.SerializeToElement(new { componentId = objectId }, BridgeProtocol.JsonOptions)),
            "rhino" => (
                BridgeAdapterOwner.RhinoScene,
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

    /// <summary>
    /// Resolves a layer or document-table expectation from one rhino.listLayers read. The layer
    /// table's own fingerprint answers RhinoLayerTable scopes (a whole-table CAS covering presence
    /// AND absence); a single layer's fingerprint answers RhinoLayer. Other table kinds (block,
    /// dimension style, material, linetype) are purge targets whose entries the audit fingerprints;
    /// their live value is resolved by the adapter at execution, so the enrichment reports the
    /// table fingerprint and lets the purge re-verify usage itself.
    /// </summary>
    private async Task<ScopedInspection> ReadTableScopeAsync(
        TargetState targetState,
        string scope,
        CancellationToken cancellationToken)
    {
        var parts = scope.Split(':', 3);
        if (parts.Length != 3)
        {
            throw new InvalidOperationException($"Invalid table scope '{scope}'.");
        }
        RequireAdapter(targetState, BridgeAdapterOwner.RhinoScene);
        using var empty = JsonDocument.Parse("{}");
        var request = new BridgeOperationRequest(
            $"read-{Guid.NewGuid():N}",
            BridgeAdapterOwner.RhinoScene,
            "rhino.listLayers",
            BridgeOperationAccess.Read,
            targetState.Snapshot?.State.Revision ?? 0,
            ExpectedFingerprint: null,
            WriterLeaseToken: null,
            empty.RootElement.Clone());
        var response = await SendOperationAsync(targetState.Target, request, cancellationToken)
            .ConfigureAwait(false);
        var table = response.Result.Deserialize<RhinoLayerTableResult>(BridgeProtocol.JsonOptions)
            ?? throw new BridgeProtocolException(
                "rhino_layer_table_payload",
                "The Rhino layer listing returned an empty payload.");
        var fingerprint = parts[1] switch
        {
            nameof(ResourceKind.RhinoLayer) => Guid.TryParse(parts[2], out var layerId)
                ? table.Layers.FirstOrDefault(layer => layer.LayerId == layerId)?.Fingerprint
                : null,
            _ => table.Fingerprint,
        };
        return new ScopedInspection(
            scope,
            BridgeAdapterOwner.RhinoScene,
            "rhino.listLayers",
            fingerprint,
            response.Result.Clone(),
            response.Diagnostics);
    }

    /// <summary>
    /// Server-computed "tidy" layout. Reads the live snapshot, computes a deterministic layered
    /// arrangement of the dataflow cluster(s) the <c>seedComponentIds</c> belong to (see
    /// <see cref="CanvasLayout"/>), and submits the resulting component moves as a perfectly ordinary
    /// <c>canvas.move</c> ChangeSet — so single-writer, conflict detection, rollback, and the adapter's
    /// re-layout/redraw all apply unchanged. The model supplies only the seed ids it authored; every pivot
    /// and fingerprint is server-owned (computed from wire topology + real bounds), so it costs no model
    /// inference and cannot drift. A no-op when the cluster is already tidy.
    /// </summary>
    public async Task<object> ArrangeLayoutAsync(
        SessionRecord session,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var seeds = ReadSeedComponentIds(arguments);
        if (seeds.Count == 0)
        {
            throw new InvalidOperationException(
                "arrange_layout requires seedComponentIds: the objectIds of the components you just authored.");
        }
        // Default wait=true so the tidy result comes back inline with the tool call.
        var wait = !arguments.TryGetProperty("wait", out var waitElement) ||
            waitElement.ValueKind != JsonValueKind.False;
        return await ArrangeSeedsAsync(session, seeds, wait, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Core of the tidy: captures the live canvas, computes the layered layout for the cluster(s) the
    /// <paramref name="seeds"/> belong to, and submits the resulting moves as an ordinary canvas.move
    /// ChangeSet. Shared by the model-driven <c>arrange_layout</c> tool and the automatic post-turn tidy.
    /// </summary>
    internal async Task<object> ArrangeSeedsAsync(
        SessionRecord session,
        IReadOnlyCollection<Guid> seeds,
        bool wait,
        CancellationToken cancellationToken)
    {
        var targetState = ResolveSessionTargetState(session);
        SnapshotEnvelope snapshot;
        using (await _documentGate.EnterReadAsync(cancellationToken).ConfigureAwait(false))
        {
            snapshot = await CaptureSnapshotAsync(targetState, force: true, cancellationToken).ConfigureAwait(false);
        }

        var moves = CanvasLayout.Arrange(snapshot.Canvas, seeds);
        if (moves.Count == 0)
        {
            return new { status = "already-tidy", moved = 0 };
        }

        // Per-component layout fingerprint from the SAME snapshot the move will validate against — using the
        // exact fallback BuildResources uses — so the writeSet/payload fingerprints are consistent by
        // construction and never manufacture a false conflict.
        var layoutFingerprint = snapshot.Canvas.Objects.ToDictionary(
            item => item.ObjectId,
            item => string.IsNullOrEmpty(item.LayoutFingerprint) ? item.Fingerprint : item.LayoutFingerprint);

        const string operationId = "arrange";
        var pivots = new Dictionary<Guid, object>();
        var expectedFingerprints = new Dictionary<Guid, string>();
        var writes = new List<ResourceAddress>();
        var writeSet = new List<ResourceExpectation>();
        foreach (var (id, pivot) in moves)
        {
            if (!layoutFingerprint.TryGetValue(id, out var fingerprint))
            {
                continue; // a computed move for a component no longer in the snapshot — skip defensively
            }
            pivots[id] = new { x = pivot.X, y = pivot.Y };
            expectedFingerprints[id] = fingerprint;
            var address = new ResourceAddress(ResourceKind.GrasshopperComponentLayout, id.ToString("D"));
            writes.Add(address);
            writeSet.Add(new ResourceExpectation(address, fingerprint));
        }
        if (writeSet.Count == 0)
        {
            return new { status = "already-tidy", moved = 0 };
        }

        var artifactName = FormattableString.Invariant($"arrange-{Guid.NewGuid():N}.json");
        await WriteSessionArtifactAsync(
            session.Id,
            artifactName,
            new
            {
                bridgeOperation = "canvas.move",
                arguments = new { operationId, pivots, expectedFingerprints },
            },
            cancellationToken).ConfigureAwait(false);

        var changeSet = new ChangeSet(
            Guid.NewGuid(),
            targetState.Target.ProjectId,
            session.Id,
            ResourceExpectation.AutoBaseRevision,
            null,
            Array.Empty<Guid>(),
            Array.Empty<ResourceExpectation>(),
            writeSet,
            [
                new TypedOperation(
                    operationId,
                    OperationKind.MoveComponent,
                    AdapterOwner.Canvas,
                    Array.Empty<ResourceAddress>(),
                    writes,
                    Reversible: true,
                    artifactName)
            ],
            Array.Empty<VerificationPredicate>(),
            Array.Empty<RollbackBeforeImage>(),
            DateTimeOffset.UtcNow);

        var submission = JsonSerializer.SerializeToElement(
            new
            {
                changeSet,
                // 'gptino:auto' skips the whole-snapshot-id gate; the concrete per-component layout
                // fingerprints above still govern conflicts, so a between-capture drift blocks correctly.
                expectedSnapshotId = ResourceExpectation.AutoFingerprint,
                // The key/summary prefixes are the arrange-job tag IsArrangeJob keys off; keep
                // them in sync with the constants.
                idempotencyKey = FormattableString.Invariant($"{ArrangeIdempotencyKeyPrefix}{Guid.NewGuid():N}"),
                summary = FormattableString.Invariant($"{ArrangeSummaryPrefix} ({writeSet.Count} components)"),
                wait,
            },
            BridgeProtocol.JsonOptions);

        return await SubmitChangeAsync(session, submission, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resets the current session's per-turn "created components" accumulator (ILayoutTidyService).</summary>
    public void BeginTurn(Guid sessionId) => _turnCreatedComponents[sessionId] = new HashSet<Guid>();

    /// <summary>
    /// Drains the components this session created during the just-finished turn and, if any, tidies the
    /// dataflow cluster(s) they belong to via the same layered layout as arrange_layout. Best effort: a
    /// disconnected document or a layout failure is logged and swallowed so it never demotes the turn.
    /// Returns the number of seed components tidied (0 when nothing was created or the canvas was clean).
    /// </summary>
    public async Task<int> TidyTurnCreationsAsync(SessionRecord session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!_turnCreatedComponents.TryRemove(session.Id, out var set))
        {
            return 0;
        }
        // A halted session never auto-tidies: the incident's canvas state must stay untouched
        // for review (the production failure was an auto-tidy committing on RecoveryRequired
        // wreckage). Soft-skip: a turn whose LAST job ended Failed/Blocked (or was cancelled/
        // interrupted) is not rearranged either — never tidy a half-failed turn's canvas.
        if (_sessionHalts.ContainsKey(session.Id))
        {
            return 0;
        }
        if (_lastTerminalJobStates.TryGetValue(session.Id, out var lastTerminal) &&
            lastTerminal is JobState.Failed or JobState.Blocked
                or JobState.RecoveryRequired or JobState.Cancelled)
        {
            return 0;
        }
        Guid[] seeds;
        lock (set)
        {
            seeds = set.ToArray();
        }
        if (seeds.Length == 0)
        {
            return 0;
        }
        try
        {
            // wait:false — the tidy move rides the normal broker queue and repaints when it lands; the
            // turn is already complete, so there is nothing to block on.
            await ArrangeSeedsAsync(session, seeds, wait: false, cancellationToken).ConfigureAwait(false);
            return seeds.Length;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Automatic post-turn tidy failed for session {SessionId}.", session.Id);
            return 0;
        }
    }

    // Test seam (InternalsVisibleTo): seeds the per-turn accumulator exactly as a committed create
    // would, so the tidy-gate tests can exercise the post-turn path without a full create round trip.
    internal void SeedTurnCreatedComponents(Guid sessionId, params Guid[] componentIds)
    {
        var set = _turnCreatedComponents.GetOrAdd(sessionId, static _ => new HashSet<Guid>());
        lock (set)
        {
            foreach (var id in componentIds)
            {
                set.Add(id);
            }
        }
    }

    // Records the canvas objects that appeared (new ids) between a committed ChangeSet's before/after
    // snapshots into the session's per-turn accumulator, so the post-turn tidy can seed on exactly the
    // components this turn authored. Cheap set diff; a no-op when the write created no canvas objects.
    private void AccumulateTurnCreatedComponents(Guid sessionId, CanvasSnapshot before, CanvasSnapshot after)
    {
        if (after.Objects.Count == 0 || after.Objects.Count <= before.Objects.Count)
        {
            return;
        }
        var beforeIds = before.Objects.Select(item => item.ObjectId).ToHashSet();
        var created = after.Objects
            .Select(item => item.ObjectId)
            .Where(id => !beforeIds.Contains(id))
            .ToList();
        if (created.Count == 0)
        {
            return;
        }
        var set = _turnCreatedComponents.GetOrAdd(sessionId, static _ => new HashSet<Guid>());
        lock (set)
        {
            foreach (var id in created)
            {
                set.Add(id);
            }
        }
    }

    private static IReadOnlyCollection<Guid> ReadSeedComponentIds(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("seedComponentIds", out var element) ||
            element.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<Guid>();
        }
        var ids = new List<Guid>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var id))
            {
                ids.Add(id);
            }
        }
        return ids;
    }

    private async Task WriteSessionArtifactAsync(
        Guid sessionId,
        string artifactName,
        object payload,
        CancellationToken cancellationToken)
    {
        var sessionRoot = Path.Combine(_artifactRoot, sessionId.ToString("N"));
        Directory.CreateDirectory(sessionRoot);
        var path = ConstrainedPath.Resolve(sessionRoot, artifactName, "Arrange payload");
        var json = JsonSerializer.Serialize(payload, payload.GetType(), BridgeProtocol.JsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
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
        RejectWritesOnEndpointFixAnchors(changeSet);
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

        // Host-enforced recovery halt: a FRESH submission into a halted session is refused with
        // the remediation path. Checked AFTER the idempotent duplicate fast path above so replays
        // of already-accepted keys (including the halting job itself) keep answering.
        ThrowIfSessionHalted(session.Id);

        // Resolve the optional user-approval grant AFTER the duplicate fast path (like the target
        // below): a matching request hash proves the request was already accepted with a
        // then-valid grant, so an idempotent replay keeps answering even after grant expiry or a
        // restart wiped the in-memory registry. An unknown/expired grant on a FRESH submit still
        // fails with the teaching message. Items ride the in-memory job entry only — interrupted
        // jobs never execute after a restart (they become RecoveryRequired).
        var approvalItems = ResolveApprovalGrant(changeSet.ApprovalGrantId);

        // Session -> Grasshopper document resolution happens once at submit and is frozen into the
        // job (durably, for restart recovery): the queue and executor never re-derive it. Resolved
        // AFTER the duplicate fast path above so an idempotent replay (a matching request hash
        // proves the request is byte-identical to the previously validated one) keeps answering
        // even when no target is registered — e.g. right after an AgentHost restart.
        var targetState = ResolveSessionTargetState(session);
        ValidateExpectationCoverage(
            changeSet,
            draftOperations,
            targetState.Target.GrasshopperDocumentId,
            targetState.Target.ProjectId);

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
                    targetState.DocKey)
                {
                    ApprovalItems = approvalItems,
                    ApprovalGrantId = changeSet.ApprovalGrantId,
                };
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
                        // latchHalt: this path itself just converted the orphaned row to
                        // RecoveryRequired — a genuine this-run interruption, so it halts.
                        RegisterRestoredEntry(entry, latchHalt: true);
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
            // RACE-CLOSE: the halt latch may have flipped after the submit-entry check but before
            // this enqueue (a same-session job just ended RecoveryRequired). The latch-set path's
            // sweep only sees jobs registered before its enumeration, and the scheduler overlay
            // already refuses halted sessions — this re-check retires the freshly inserted job
            // deterministically instead of leaving it parked in the queue until resume. Mark +
            // cancel only: the completion observer is the single writer of the durable
            // Cancelled/"halted-by-recovery" record and resolves the entry AFTER that write, so a
            // wait:true caller still returns the terminal projection and a concurrent sweep over
            // the same job can never race this path's marker (see CancelQueuedSessionJobs).
            if (_sessionHalts.TryGetValue(session.Id, out var lateHalt))
            {
                _haltCancelledJobs.TryAdd(entry.Job.JobId, lateHalt.JobId);
                _broker.TryCancel(entry.Job.JobId);
            }
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
        var order = new SessionOrderSnapshot(
            projectId,
            sessions.Select(item => item.Id).ToArray(),
            version);
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
        // A problem is only worth surfacing while it is the session's CURRENT job. Once the session
        // enqueues a newer job (a resubmitted fix, or simply its next turn) — or that newer job
        // commits — the old Blocked/Failed/RecoveryRequired entry is resolved and must drop off the
        // warning banner, otherwise a fixed conflict lingers and looks unresolved.
        var latestSequenceBySession = _jobs.Values
            .GroupBy(entry => entry.Job.ChangeSet.SessionId)
            .ToDictionary(group => group.Key, group => group.Max(entry => entry.Job.EnqueueSequence));
        return _jobs.Values
            .Where(entry => entry.State is
                JobState.RecoveryRequired or JobState.Blocked or JobState.Failed)
            .Where(entry => latestSequenceBySession.TryGetValue(entry.Job.ChangeSet.SessionId, out var latest) &&
                entry.Job.EnqueueSequence == latest)
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
        // Recovery-manifest bookkeeping: which operations completed their bridge round trip and
        // which one was in flight when a failure surfaced. The in-flight operation's outcome is
        // genuinely unknown (its write may or may not have landed) — the manifest reports it as
        // unknown, never as failed.
        var completedOperationIds = new List<string>();
        // Verified-rollback classification must ignore completed READ operations (a ChangeSet may
        // legally carry reads): only a completed WRITE proves an earlier document mutation. Kept
        // as a separate counter — completedOperationIds still feeds the recovery manifest, whose
        // Applied list must keep showing reads.
        var completedWriteOperationCount = 0;
        string? inFlightOperationId = null;
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
            // Rebase self-attributable stale concrete fingerprints (the session's own prior commit
            // advanced them) to live BEFORE validation, so a stale base for a value/geometry write no
            // longer Blocks. Foreign/drifted resources are left for ValidateAgainstSnapshot to Block.
            var selfStaleRebase = ResolveSelfStaleConcreteRebase(
                resolvedChangeSet,
                preparedOperations,
                before.State,
                job.ChangeSet.SessionId,
                _resourceLedger);
            resolvedChangeSet = selfStaleRebase.ChangeSet;
            preparedOperations = selfStaleRebase.Operations;
            foreach (var (resource, staleFingerprint, liveFingerprint) in selfStaleRebase.Rebased)
            {
                _problemLog?.RecordSelfStaleRebase(
                    job.JobId,
                    job.ChangeSet.SessionId,
                    resource,
                    staleFingerprint,
                    liveFingerprint);
            }
            var conflicts = _conflictDetector.ValidateAgainstSnapshot(resolvedChangeSet, before.State);
            if (conflicts.Count > 0)
            {
                var message = string.Join(" ", conflicts.Select(conflict => conflict.Message));
                await SetJobPhaseAsync(entry, JobState.Blocked, message, conflicts).ConfigureAwait(false);
                return new JobExecutionResult(job.JobId, JobState.Blocked, message);
            }

            // Server-owned provenance: stamp every rhino.upsert with the job's target docKey so
            // bakes stay attributable to the GH document that produced them. Model payloads cannot
            // carry the field (ValidateUpsertArguments rejects it at submit); like auto-pivot
            // resolution below, only the dispatched Arguments change — FrozenPayload is untouched.
            preparedOperations = InjectRhinoUpsertSourceDocKey(preparedOperations, targetState.DocKey);
            // User-approval injection: only ops whose target object AND audited fingerprint the
            // grant covers gain approved=true; everything else keeps the default-deny.
            preparedOperations = InjectApprovalFlags(preparedOperations, entry.ApprovalItems);
            await PreflightBridgePayloadsAsync(
                targetState,
                preparedOperations,
                before.State.Revision,
                execution.Token).ConfigureAwait(false);
            PreflightPythonSchemas(preparedOperations, before);
            PreflightDeterministicAdapterRejections(preparedOperations, before);
            // After the synchronous rejections so the cheaper, more specific instance/type
            // confusion guard wins over the catalog lookup for the same bogus GUID.
            await PreflightCanvasCreateComponentTypesAsync(
                targetState,
                preparedOperations,
                before.State.Revision,
                execution.Token).ConfigureAwait(false);

            // Server-owned deterministic placement: rewrite every canvas.create whose model pivot is the
            // "gptino:auto" sentinel into a concrete, non-overlapping pivot computed against the live
            // before-snapshot, stripping autoUpstream so the (unchanged) Grasshopper adapter receives
            // today's exact contract. Mirrors gptino:auto fingerprint resolution above: only the dispatched
            // Arguments change — FrozenPayload (idempotency hash, reserved artifacts) is never touched, and
            // an existing human-placed object is never moved (it is only an immutable collision obstacle).
            preparedOperations = CanvasAutoPlacement.ResolveAutoPivots(preparedOperations, before.Canvas);

            // Consumer-first delete reordering: within each CONTIGUOUS run of canvas.delete
            // operations, downstream (consumer) components are dispatched before their upstream
            // sources. The structure fingerprint hashes a component's INPUT wires, so deleting an
            // upstream component first removes a surviving target's incoming wire and refuses the
            // batch's own later delete mid-apply (RecoveryRequired). Deleting consumers first
            // never moves a remaining target's fingerprint. DISPATCH ORDER ONLY: FrozenPayload,
            // operation ids, and the accepted request hash (computed at submit) stay byte-identical.
            preparedOperations = ReorderContiguousDeletesConsumerFirst(preparedOperations, before.Canvas);

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
                var pythonWrite = bridgeOwner == BridgeAdapterOwner.Script &&
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
                inFlightOperationId = operation.OperationId;
                var operationTimer = Stopwatch.StartNew();
                BridgeOperationResponse response;
                try
                {
                    response = await SendOperationAsync(targetState.Target, request, execution.Token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    // Slow-op accounting (Information only): surfaces where the bridge budget went
                    // in the terminal job view, so a session sees a solve approaching the cap
                    // BEFORE one times out. Sub-threshold ops stay silent to keep projections slim.
                    operationTimer.Stop();
                    if (operationTimer.Elapsed >= OperationDurationDiagnosticThreshold)
                    {
                        diagnostics.Add(new JobDiagnostic(
                            operation.OperationId,
                            BridgeDiagnosticSeverity.Information,
                            "op_duration",
                            FormatOperationDuration(
                                prepared.BridgeOperation,
                                operationTimer.Elapsed,
                                BridgeRequestTimeout)));
                    }
                }
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
                            $"Script operation '{operation.OperationId}' returned an invalid fingerprint chain.");
                    }
                    rollingPythonFingerprints[pythonWrite.Id] = response.AfterFingerprint;
                }
                if (bridgeOwner is BridgeAdapterOwner.Script or BridgeAdapterOwner.RhinoScene)
                {
                    // A multi-object operation reports ONE aggregate AfterFingerprint, which is not
                    // any object's real fingerprint. Batch results carry per-item fingerprints;
                    // recording the aggregate for each declared write would poison the resource
                    // ledger and stale every later operation on those objects.
                    var perItem = ReadBatchItemFingerprints(response.Result);
                    operationObservations.AddRange(operation.Writes.Select(resource =>
                        new ResourceObservation(
                            resource,
                            perItem is not null && perItem.TryGetValue(resource.Id, out var itemFingerprint)
                                ? itemFingerprint
                                : response.AfterFingerprint)));
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
                completedOperationIds.Add(operation.OperationId);
                if (access == BridgeOperationAccess.Write)
                {
                    completedWriteOperationCount++;
                }
                inFlightOperationId = null;
            }

            await SetJobPhaseAsync(
                entry,
                JobState.Verifying,
                "Capturing and verifying the resulting document state.").ConfigureAwait(false);
            _broker.RecordJobState(job.JobId, JobState.Verifying);
            _events.Publish();
            var after = await CaptureSnapshotAsync(targetState, force: true, execution.Token)
                .ConfigureAwait(false);
            // Collect the post-solve output inspection up front so semantic acceptance predicates
            // (OutputCountInRange) verify against real counts. Best-effort: on failure outputs stay
            // empty and count predicates fail closed (an unverifiable claim never passes).
            IReadOnlyList<JobComponentOutputs> componentOutputs = Array.Empty<JobComponentOutputs>();
            try
            {
                componentOutputs = await CollectComponentOutputsAsync(
                    targetState.Target, job.ChangeSet, after, execution.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Could not collect component outputs for job {JobId}.", job.JobId);
            }
            entry.Outputs = componentOutputs;
            var predicateOutcomes = new List<PredicateOutcome>();
            var verificationProblems = Verify(
                job.ChangeSet,
                after,
                diagnostics,
                operationObservations,
                componentOutputs,
                predicateOutcomes);
            // Log every predicate outcome (pass and fail) so we can later mine which predicates the
            // model declares and whether they catch real problems — data-first tuning of the library.
            foreach (var outcome in predicateOutcomes)
            {
                _problemLog?.RecordPredicateOutcome(
                    job.JobId,
                    job.ChangeSet.SessionId,
                    outcome.Name,
                    outcome.Kind.ToString(),
                    outcome.Resource,
                    outcome.ExpectedValue,
                    outcome.Passed);
            }
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
                    // entry.Outputs was already collected before Verify.
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
                // Post-solve socket identities of reshaped components (from the after-snapshot; kills
                // the follow-up snapshot_read), captured while the write lease is still held. The
                // output inspection (counts/types/bounds/samples) was already collected before Verify
                // and is on entry.Outputs. Same never-demote discipline as the committed view above.
                entry.Sockets = CollectComponentSockets(job.ChangeSet, after);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Could not capture post-solve observations for job {JobId}.", job.JobId);
            }
            UpdateResourceLedger(before, after, job.ChangeSet.SessionId, job.JobId);
            AccumulateTurnCreatedComponents(job.ChangeSet.SessionId, before.Canvas, after.Canvas);
            // Informational commit quality: runtime warnings and empty solved outputs, appended to
            // the commit message (and thereby the problem-log row SetJobPhaseAsync writes) so a
            // "committed but red/empty on canvas" state survives outside the transcript. This is
            // reporting only — it never demotes the commit.
            var commitQuality = DescribeCommitQuality(diagnostics, entry.Outputs);
            await SetJobPhaseAsync(
                entry,
                JobState.Committed,
                commitQuality is null
                    ? "Verified and committed to managed history."
                    : $"Verified and committed to managed history. {commitQuality}").ConfigureAwait(false);
            // Commits are the moment reference/bake topology can change; refresh the data-flow
            // summaries in the background (the refresh takes the read gate itself, so it waits for
            // this write epoch to release rather than extending it).
            ScheduleDataFlowRefresh();
            ConsumeApprovalGrant(entry);
            return new JobExecutionResult(
                job.JobId,
                JobState.Committed,
                commitQuality is null ? "Verified and committed." : $"Verified and committed. {commitQuality}");
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
            // The human-wins refusal is raised by the adapter BEFORE it touches the document, so
            // (unless an earlier operation in the batch already landed) nothing changed: report a
            // deterministic Failed the session can act on, not a recoveryRequired review task.
            var approvalRefusal =
                exception is BridgeProtocolException { Code: ApprovalRequiredFailureCode or PreconditionRefusedFailureCode } &&
                !liveChanged;
            // A VERIFIED rollback (the adapter proved the pre-write state was restored via
            // read-back) with no completed sibling WRITE and no live change is a deterministic
            // Failed the session can act on. Completed READS don't count — they prove nothing
            // changed. A batch-middle failure — any completed write — stays RecoveryRequired:
            // earlier writes landed and must be reviewed.
            var verifiedRollback =
                exception is BridgeProtocolException { Code: MutationRolledBackFailureCode } &&
                completedWriteOperationCount == 0 &&
                !liveChanged;
            var state = !(approvalRefusal || verifiedRollback) && (liveChanged || writeMayHaveChanged)
                ? JobState.RecoveryRequired
                : JobState.Failed;
            var message = exception.Message;
            if (state == JobState.RecoveryRequired)
            {
                // The recovery manifest turns "review the document state" into a deterministic
                // worklist: which operations verifiably applied, which one was in flight (outcome
                // honestly unknown — never reported as failed), and which never dispatched. A
                // refusal code on the in-flight operation proves it never wrote; a verified
                // rollback proves it wrote and was restored — either way the manifest stops
                // claiming its outcome is unknown, with each proof labeled honestly.
                var manifest = BuildRecoveryManifest(
                    job.ChangeSet.Operations,
                    completedOperationIds,
                    inFlightOperationId,
                    inFlightRefusedBeforeWrite: exception is BridgeProtocolException
                    {
                        Code: ApprovalRequiredFailureCode or PreconditionRefusedFailureCode
                    },
                    inFlightRolledBack: exception is BridgeProtocolException
                    {
                        Code: MutationRolledBackFailureCode
                    });
                message = $"{message} {manifest.Message}";
                diagnostics.AddRange(manifest.Diagnostics);
            }
            entry.Diagnostics ??= diagnostics;
            await SetJobPhaseAsync(entry, state, message).ConfigureAwait(false);
            return new JobExecutionResult(job.JobId, state, message);
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

    /// <summary>
    /// Reorders CONTIGUOUS runs of canvas.delete operations consumer-first using the
    /// before-snapshot's wire topology (canvas wires unioned with each input's CurrentSources —
    /// the same union the layout uses). Rules: never reorder across a non-delete boundary
    /// (segment-local only), stable among independent deletes (original order preserved), and
    /// defensive — a cycle or an unreadable target keeps the submitted order. Dispatch order
    /// changes ONLY; payloads and identity are untouched.
    /// </summary>
    private static IReadOnlyList<PreparedOperation> ReorderContiguousDeletesConsumerFirst(
        IReadOnlyList<PreparedOperation> operations,
        CanvasSnapshot canvas)
    {
        static bool IsCanvasDelete(PreparedOperation operation) =>
            string.Equals(operation.BridgeOperation, "canvas.delete", StringComparison.Ordinal);
        if (operations.Count(IsCanvasDelete) < 2)
        {
            return operations;
        }
        var result = new List<PreparedOperation>(operations.Count);
        var run = new List<PreparedOperation>();
        void FlushRun()
        {
            if (run.Count == 1)
            {
                result.Add(run[0]);
            }
            else if (run.Count > 1)
            {
                result.AddRange(OrderDeleteRunConsumerFirst(run, canvas));
            }
            run.Clear();
        }
        foreach (var operation in operations)
        {
            if (IsCanvasDelete(operation))
            {
                run.Add(operation);
                continue;
            }
            FlushRun();
            result.Add(operation);
        }
        FlushRun();
        return result;
    }

    // Complexity note: the stable-Kahn emission below is O(n^3) worst case over the run's delete
    // count (n emissions x n candidate scans x n prerequisite checks). Deliberate: delete runs are
    // a handful of operations in practice, and the stable smallest-original-index tie-break is
    // worth more than an asymptotic win here.
    private static IReadOnlyList<PreparedOperation> OrderDeleteRunConsumerFirst(
        IReadOnlyList<PreparedOperation> run,
        CanvasSnapshot canvas)
    {
        // Target object of every delete in the run; anything unreadable keeps the original order.
        var indexesByTarget = new Dictionary<Guid, List<int>>();
        for (var index = 0; index < run.Count; index++)
        {
            if (!run[index].Arguments.TryGetProperty("objectId", out var idElement) ||
                idElement.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(idElement.GetString(), out var objectId))
            {
                return run;
            }
            if (!indexesByTarget.TryGetValue(objectId, out var list))
            {
                indexesByTarget[objectId] = list = [];
            }
            list.Add(index);
        }

        // source -> consumer edges restricted to the run's targets, from the before-snapshot's
        // topology. Wires are authoritative; each input's CurrentSources are unioned in so a
        // snapshot that reported only one form still yields the full graph.
        var feeds = new HashSet<(Guid Source, Guid Consumer)>();
        foreach (var wire in canvas.Wires)
        {
            if (wire.SourceObjectId != wire.TargetObjectId &&
                indexesByTarget.ContainsKey(wire.SourceObjectId) &&
                indexesByTarget.ContainsKey(wire.TargetObjectId))
            {
                feeds.Add((wire.SourceObjectId, wire.TargetObjectId));
            }
        }
        foreach (var item in canvas.Objects)
        {
            if (!indexesByTarget.ContainsKey(item.ObjectId))
            {
                continue;
            }
            foreach (var input in item.Inputs)
            {
                foreach (var source in input.CurrentSources)
                {
                    if (source.OwnerObjectId != item.ObjectId &&
                        indexesByTarget.ContainsKey(source.OwnerObjectId))
                    {
                        feeds.Add((source.OwnerObjectId, item.ObjectId));
                    }
                }
            }
        }
        if (feeds.Count == 0)
        {
            return run;
        }

        // prerequisites[i] = run indexes that must dispatch BEFORE i: every consumer of i's
        // target. Deleting the consumer first never moves the source's structure fingerprint;
        // the reverse order does (the incident class this exists to kill).
        var prerequisites = new HashSet<int>[run.Count];
        for (var index = 0; index < run.Count; index++)
        {
            prerequisites[index] = [];
        }
        foreach (var (source, consumer) in feeds)
        {
            foreach (var sourceIndex in indexesByTarget[source])
            {
                foreach (var consumerIndex in indexesByTarget[consumer])
                {
                    prerequisites[sourceIndex].Add(consumerIndex);
                }
            }
        }

        // Stable Kahn: always emit the unblocked delete with the smallest ORIGINAL index, so
        // independent deletes keep their submitted order exactly. No emittable node => cycle in
        // the declared topology: keep the submitted order defensively.
        var emitted = new bool[run.Count];
        var ordered = new List<PreparedOperation>(run.Count);
        while (ordered.Count < run.Count)
        {
            var progressed = false;
            for (var index = 0; index < run.Count; index++)
            {
                if (emitted[index] || !prerequisites[index].All(previous => emitted[previous]))
                {
                    continue;
                }
                emitted[index] = true;
                ordered.Add(run[index]);
                progressed = true;
                break;
            }
            if (!progressed)
            {
                return run;
            }
        }
        return ordered;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await _jobStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var recovery = await _jobStore.RecoverInterruptedAsync(cancellationToken)
            .ConfigureAwait(false);
        var (sessions, _) = await _store.ReadStateAsync(cancellationToken).ConfigureAwait(false);
        var sessionsById = sessions.ToDictionary(session => session.Id);
        foreach (var durable in recovery.Records)
        {
            if (durable.ChangeSet.SessionId != durable.SessionId)
            {
                throw new InvalidDataException(
                    $"Durable job '{durable.JobId:D}' has inconsistent session identity.");
            }

            var session = sessionsById.GetValueOrDefault(durable.SessionId)
                ?? CreateRecoveredSession(durable);
            // Latch ONLY sessions whose job THIS startup converted from a non-terminal state:
            // RecoveryRequired rows that were already terminal — an acknowledged resume, or a
            // row recorded by an earlier run — are history and must not re-halt on every boot.
            RegisterRestoredEntry(
                CreateRestoredEntry(durable, session),
                latchHalt: recovery.InterruptedJobIds.Contains(durable.JobId));
            _enqueueSequence = Math.Max(_enqueueSequence, durable.EnqueueSequence);
        }

        await base.StartAsync(cancellationToken).ConfigureAwait(false);
        if (recovery.Records.Count > 0)
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

    /// <summary>
    /// Reads bridge frames until the connection ends. EVERY exit path is traced, including the
    /// exception that caused it: this loop also delivers the responses to outgoing operations, so
    /// when it dies quietly every bridge call hangs forever while health still reports "connected".
    /// That failure mode cost a whole debugging session with no host-side trace to look at.
    /// </summary>
    private async Task ReceiveLoopAsync(
        DocumentPipeConnection connection,
        CancellationToken cancellationToken)
    {
        var received = 0L;
        try
        {
            while (!cancellationToken.IsCancellationRequested && connection.IsConnected)
            {
                var frame = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                frame.Validate();
                received++;
                if (frame.Kind is BridgeMessageKind.Response or BridgeMessageKind.Error)
                {
                    CompletePending(frame);
                    continue;
                }

                DevelopmentDiagnosticTrace.TryWrite(
                    "AgentHost",
                    "frame-received",
                    $"kind={frame.Kind};type={frame.PayloadType};" +
                    $"targetGh={frame.Target?.HasGrasshopper.ToString() ?? "none"};n={received}");

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
            DevelopmentDiagnosticTrace.TryWrite(
                "AgentHost",
                "receive-loop-exit",
                $"reason=closed;cancelled={cancellationToken.IsCancellationRequested};" +
                $"connected={connection.IsConnected};frames={received}");
        }
        catch (Exception exception)
        {
            DevelopmentDiagnosticTrace.TryWrite(
                "AgentHost",
                "receive-loop-faulted",
                $"{exception.GetType().Name}: {exception.Message};frames={received}");
            throw;
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

            DevelopmentDiagnosticTrace.TryWrite(
                "AgentHost",
                "target-registered",
                $"key={requestedTarget.StableTargetKey()[..8]};gh={requestedTarget.HasGrasshopper};" +
                $"targets={_targets.Count};adapters={string.Join('+', request.AvailableAdapters)}");
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
            // A newly registered (or Save-As-renamed) document changes what the data-flow view
            // should cover; refresh in the background once the registration frame is answered.
            ScheduleDataFlowRefresh();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var code = exception is BridgeProtocolException protocol ? protocol.Code : "registration_rejected";
            DevelopmentDiagnosticTrace.TryWrite(
                "AgentHost",
                "target-registration-rejected",
                $"code={code};{exception.GetType().Name}: {exception.Message}");
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
        int remaining;
        lock (_connectionGate)
        {
            removed = _targets.Remove(key);
            remaining = _targets.Count;
        }
        DevelopmentDiagnosticTrace.TryWrite(
            "AgentHost",
            "target-closed",
            $"key={key[..8]};removed={removed};remaining={remaining};gh={target.HasGrasshopper}");
        if (removed)
        {
            // Only calls addressed to the closed document fail; siblings keep running.
            FailPendingFor(key, new IOException("The bound document was closed."));
            // The closed doc's data-flow summary must not linger in the panel.
            ScheduleDataFlowRefresh();
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
        // With zero targets the refresh clears every summary (and publishes), so the panel's
        // data layer drops instead of showing counts for a bridge that no longer exists.
        ScheduleDataFlowRefresh();
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
        catch (TimeoutException)
        {
            // The default "The operation has timed out." taught nothing: sessions resubmitted the
            // same heavy solve and froze Rhino again. Name the operation, the budget, and the way
            // out. The timeout only abandons the pipe wait — the Grasshopper solve keeps running.
            var operation = payload as BridgeOperationRequest;
            throw new TimeoutException(BridgeTimeoutMessage(
                operation?.OperationId,
                operation?.Operation ?? payloadType,
                BridgeRequestTimeout));
        }
        finally
        {
            _pending.TryRemove(frame.MessageId, out _);
        }
    }

    /// <summary>
    /// Bridge-wait timeout message carrying the operation id, the bridge operation name, and the
    /// budget, plus recovery guidance. Internal so the message contract is pinned by unit tests
    /// without paying the live 45s wait.
    /// </summary>
    internal static string BridgeTimeoutMessage(
        string? operationId,
        string operationName,
        TimeSpan budget) =>
        $"Bridge operation '{operationId ?? "(no id)"}' ({operationName}) exceeded its " +
        $"{budget.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}s budget. Grasshopper is " +
        "likely still solving on the UI thread — the write may still land and Rhino may be frozen " +
        "until the solve finishes. Do NOT resubmit the same heavy solve: reduce sampling/segment " +
        "counts, split the work into staged components, or wire native Grasshopper components for " +
        "solver-heavy work. Once Rhino responds, re-read the document state and retry the lighter " +
        "version.";

    /// <summary>
    /// Ops slower than this get an Information "op_duration" diagnostic in the terminal job view, so a
    /// session sees which component exceeded the ~1s per-component target and should be split into
    /// smaller logical stages (well before any solve approaches the 45s bridge budget).
    /// </summary>
    internal static readonly TimeSpan OperationDurationDiagnosticThreshold = TimeSpan.FromSeconds(1);

    internal static string FormatOperationDuration(
        string bridgeOperation,
        TimeSpan elapsed,
        TimeSpan budget) =>
        $"{bridgeOperation}: {elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms " +
        $"of the {budget.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}s bridge budget.";

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

            // No Grasshopper document means the canvas contributes no resources — which is a state,
            // not a failure. Refusing here would deny Rhino-only work its ChangeSet envelope
            // (projectId and sessionId come from snapshot_read) and its preflight, even though every
            // resource it touches lives outside the snapshot anyway and is resolved by a rhinoTables scope.
            if (!targetState.Target.HasGrasshopper)
            {
                return CaptureCanvaslessSnapshot(targetState);
            }

            RequireAdapter(targetState, BridgeAdapterOwner.Canvas);
            var currentTarget = targetState.Target;
            var request = BridgeOperationRequest.Create(
                $"snapshot-{Guid.NewGuid():N}",
                BridgeAdapterOwner.Canvas,
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

    /// <summary>
    /// The snapshot of a target with no Grasshopper document: a real envelope carrying the target,
    /// the history head and the project identity, with an EMPTY canvas and no resources. Rhino-side
    /// resources (layers, document tables) live outside the snapshot in every target anyway — they
    /// are resolved by a rhinoTables inspection scope — so nothing is lost by having none here.
    /// The revision only advances when the target changes; with no canvas there is nothing else to
    /// churn it, so repeated reads are stable and CAS on Rhino resources stays meaningful.
    /// Callers hold the snapshot gate.
    /// </summary>
    private SnapshotEnvelope CaptureCanvaslessSnapshot(TargetState targetState)
    {
        var currentTarget = targetState.Target;
        var previous = targetState.Snapshot;
        var sameTarget = previous is not null &&
            string.Equals(previous.State.Target.Identity, currentTarget.Identity, StringComparison.Ordinal);
        var revision = sameTarget ? previous!.State.Revision : 1;
        var canvas = new CanvasSnapshot(
            Guid.Empty,
            string.Empty,
            Array.Empty<CanvasObjectState>(),
            Array.Empty<WireState>(),
            Array.Empty<GroupState>());
        var state = new StateSnapshot(
            currentTarget.ProjectId,
            revision,
            GetHistory(targetState).ReadHead(),
            DateTimeOffset.UtcNow,
            currentTarget,
            Array.Empty<ResourceFingerprint>());
        var envelope = new SnapshotEnvelope(
            BuildSnapshotId(state, canvas.DocumentFingerprint),
            state,
            canvas);
        targetState.Snapshot = envelope;
        if (!sameTarget)
        {
            _events.Publish();
        }
        return envelope;
    }

    private static IReadOnlyList<ResourceFingerprint> BuildResources(
        DocumentRuntime target,
        CanvasSnapshot canvas)
    {
        var resources = new List<ResourceFingerprint>
        {
            // The whole-document resource is addressed by the runtime Grasshopper DocumentID (an
            // in-memory scope), never by the now Rhino-scoped ProjectId, which would collide the
            // Document rows of sibling documents in the snapshot and the ledger. A canvas snapshot
            // only exists for a target that HAS a Grasshopper document, so the id is present here.
            new(
                new ResourceAddress(
                    ResourceKind.Document,
                    (target.GrasshopperDocumentId
                        ?? throw new InvalidOperationException(
                            "A canvas snapshot requires a bound Grasshopper document."))
                        .ToString("D")),
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
        RequireAdapter(targetState, BridgeAdapterOwner.RhinoScene);
        var request = new BridgeOperationRequest(
            $"absence-{Guid.NewGuid():N}",
            BridgeAdapterOwner.RhinoScene,
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
            ? $"script:{componentId:D}"
            : null,
        ResourceKind.RhinoObject or
        ResourceKind.RhinoObjectGeometry or
        ResourceKind.RhinoObjectAttributes => Guid.TryParse(resource.Id, out var objectId)
            ? $"rhino:{objectId:D}"
            : null,
        // Layer and document-table resources live in no snapshot (BuildResources emits Grasshopper
        // kinds only), so without an inspection scope every layer/purge expectation would Stale-
        // block before dispatch. One layer-table read serves all of them.
        ResourceKind.RhinoLayer or
        ResourceKind.RhinoLayerTable or
        ResourceKind.RhinoBlockDefinition or
        ResourceKind.RhinoDimensionStyle or
        ResourceKind.RhinoMaterial or
        ResourceKind.RhinoLinetype => $"rhinoTables:{resource.Kind}:{resource.Id}",
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
            targetState.Target.GrasshopperDocumentId,
            targetState.Target.ProjectId);
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
                BridgeAdapterOwner.RhinoScene,
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

    // A fabricated component-type GUID used to surface only when the adapter's EmitObject returned
    // null AT EXECUTE TIME — after sibling writes in the same ChangeSet had landed — dead-ending the
    // job in RecoveryRequired. Verify every canvas.create type id against the live component catalog
    // (one GUID-query read per distinct id) BEFORE any write, so a made-up GUID is a clean
    // deterministic Failed with an actionable lookup recipe. The well-known Rhino 8 script-component
    // type ids ship with Rhino and are skipped. STRICTLY NARROWER: only an explicit empty match list
    // rejects — a failed or unparseable catalog read (older plugin without GUID catalog queries)
    // logs and passes the create through to the adapter's own EmitObject backstop.
    private async Task PreflightCanvasCreateComponentTypesAsync(
        TargetState targetState,
        IReadOnlyList<PreparedOperation> prepared,
        long snapshotRevision,
        CancellationToken cancellationToken)
    {
        // Distinct type ids, each remembering the first operation that requested it.
        var componentTypeIds = new Dictionary<Guid, string>();
        foreach (var item in prepared.Where(item =>
                     string.Equals(item.BridgeOperation, "canvas.create", StringComparison.Ordinal)))
        {
            if (!TryReadCreateComponentTypeId(
                    item.Arguments,
                    item.Operation.OperationId,
                    out var componentTypeId) ||
                IsScriptComponentType(componentTypeId))
            {
                continue;
            }
            componentTypeIds.TryAdd(componentTypeId, item.Operation.OperationId);
        }
        foreach (var (componentTypeId, operationId) in componentTypeIds)
        {
            ComponentCatalogSearchResult? catalog;
            try
            {
                var request = new BridgeOperationRequest(
                    operationId,
                    BridgeAdapterOwner.Canvas,
                    "canvas.catalog",
                    BridgeOperationAccess.Read,
                    snapshotRevision,
                    ExpectedFingerprint: null,
                    WriterLeaseToken: null,
                    JsonSerializer.SerializeToElement(
                        new ComponentCatalogSearchRequest(
                            componentTypeId.ToString("D"),
                            Limit: 1,
                            IncludeObsolete: true),
                        BridgeProtocol.JsonOptions));
                request.Validate();
                var response = await SendOperationAsync(targetState.Target, request, cancellationToken)
                    .ConfigureAwait(false);
                catalog = response.Result.Deserialize<ComponentCatalogSearchResult>(BridgeProtocol.JsonOptions);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Version-skew guard: a failed catalog read must never turn valid creates into
                // false declines — the adapter's EmitObject refusal still backstops execute time.
                _logger.LogWarning(
                    exception,
                    "canvas.create type preflight skipped: catalog read for {ComponentTypeId} failed.",
                    componentTypeId);
                continue;
            }
            if (catalog?.Matches is not { Count: 0 })
            {
                continue;
            }
            throw new InvalidOperationException(
                $"Operation '{operationId}': Grasshopper component type {componentTypeId:D} is not " +
                "installed. Rejected before any write. Look the GUID up with a component_catalog name " +
                "search (or use the well-known GUID table in the gh-authoring skill) and resubmit — " +
                "never write a type GUID from memory.");
        }
    }

    // A setComponentIo schema is append-only: the adapter rejects a socket-count reduction with a
    // NotSupportedException at execute time, which — because the same ChangeSet's source write has
    // already landed — dead-ends the job in RecoveryRequired. Catch it here, BEFORE any write, by
    // comparing the requested socket counts against the component's live sockets in the pre-write
    // snapshot, so a removal is a clean deterministic failure with no partial state. The gate is
    // the COUNT check (renames stay legal — the adapter reconciles by position); the socket-name
    // diff exists to make the rejection actionable by naming the live sockets the declaration
    // missed, especially the script console output 'out' that models cannot see.
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
            // The managed console output ('out') is auto-preserved by the adapter when a
            // declaration omits it (GrasshopperPythonFoundationAdapter.PreserveManagedConsoleOutputs),
            // so it does not count against the append-only floor. Only genuine removal of a
            // model-owned socket is rejected here. Keep this in lockstep with the adapter.
            var declaredOutputNames = SchemaSocketNames(item.Arguments, "outputs");
            var autoPreservedConsoleOutputs = component.Outputs
                .Count(parameter =>
                    string.Equals(parameter.Name, "out", StringComparison.Ordinal) &&
                    !declaredOutputNames.Contains("out", StringComparer.Ordinal));
            var effectiveLiveOutputs = component.Outputs.Count - autoPreservedConsoleOutputs;
            if (requestedInputs < component.Inputs.Count || requestedOutputs < effectiveLiveOutputs)
            {
                throw new InvalidOperationException(BuildAppendOnlySchemaRejection(
                    item.Operation.OperationId,
                    componentId,
                    component,
                    SchemaSocketNames(item.Arguments, "inputs"),
                    SchemaSocketNames(item.Arguments, "outputs"),
                    requestedInputs,
                    requestedOutputs));
            }
        }
    }

    private static string BuildAppendOnlySchemaRejection(
        string operationId,
        Guid componentId,
        CanvasObjectState component,
        IReadOnlyList<string> declaredInputs,
        IReadOnlyList<string> declaredOutputs,
        int requestedInputs,
        int requestedOutputs)
    {
        var liveInputs = component.Inputs.Select(parameter => parameter.Name).ToArray();
        var liveOutputs = component.Outputs.Select(parameter => parameter.Name).ToArray();
        var undeclaredInputs = UndeclaredSocketNames(liveInputs, declaredInputs);
        // The console output ('out') is auto-preserved, so it is never something the model must
        // declare — leave it out of the "undeclared" listing to keep the guidance about genuine
        // removals only.
        IReadOnlyList<string> undeclaredOutputs = UndeclaredSocketNames(liveOutputs, declaredOutputs)
            .Where(name => !string.Equals(name, "out", StringComparison.Ordinal))
            .ToArray();
        var message = new StringBuilder();
        message.Append(
            $"Operation '{operationId}' would remove sockets from component " +
            $"{componentId:D} (schema is append-only): it has {component.Inputs.Count} input(s) and " +
            $"{component.Outputs.Count} output(s), but the request declares {requestedInputs} input(s) " +
            $"and {requestedOutputs} output(s).");
        message.Append($" Live inputs: {SocketNameList(liveInputs)}.");
        message.Append($" Live outputs: {SocketNameList(liveOutputs)}.");
        if (undeclaredInputs.Count > 0)
        {
            message.Append($" Undeclared existing input(s): {SocketNameList(undeclaredInputs)}.");
        }
        if (undeclaredOutputs.Count > 0)
        {
            message.Append($" Undeclared existing output(s): {SocketNameList(undeclaredOutputs)}.");
        }
        message.Append(
            " List every existing socket in order, then appended ones; you may rename or retype " +
            "existing sockets but not remove them. (The console 'out' output is preserved " +
            "automatically — you never need to declare it.)");
        return message.ToString();
    }

    private static string SocketNameList(IReadOnlyList<string> names) =>
        names.Count == 0 ? "none" : string.Join(", ", names.Select(name => $"'{name}'"));

    private static IReadOnlyList<string> UndeclaredSocketNames(
        IReadOnlyList<string> liveNames,
        IReadOnlyList<string> declaredNames)
    {
        var declared = new HashSet<string>(declaredNames, StringComparer.Ordinal);
        return liveNames.Where(name => !declared.Contains(name)).ToArray();
    }

    private static int CountSchemaSockets(JsonElement arguments, string property) =>
        arguments.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Array
            ? element.GetArrayLength()
            : 0;

    private static IReadOnlyList<string> SchemaSocketNames(JsonElement arguments, string property) =>
        arguments.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray()
                .Select(socket => socket.ValueKind == JsonValueKind.Object &&
                    socket.TryGetProperty("name", out var name) &&
                    name.ValueKind == JsonValueKind.String
                        ? name.GetString() ?? string.Empty
                        : string.Empty)
                .Where(name => !string.IsNullOrEmpty(name))
                .ToArray()
            : Array.Empty<string>();

    // Script-component type ids, mirrored from src/GPTino.Grasshopper/GrasshopperPythonFoundationAdapter.cs
    // (Cpython3ComponentGuid / IronPython2ComponentGuid / CSharpComponentGuid, lines 21-27).
    private static readonly Guid Cpython3ScriptComponentTypeId = new("719467e6-7cf5-4848-99b0-c5dd57e5442c");
    private static readonly Guid IronPython2ScriptComponentTypeId = new("410755b1-224a-4c1e-a407-bf32fb45ea7e");
    private static readonly Guid CSharpScriptComponentTypeId = new("b6ba1144-02d6-4a2d-b53c-ec62e290eeb7");

    // C# reserved keywords are illegal script-variable names: RhinoCode's C# codegen rejects them
    // deterministically at compile time ("Output parameter \"out\" can not use reserved keyword
    // \"out\" as variable name") — but only AFTER the schema write has landed. Mirrored here so a
    // C# component's schema is rejected BEFORE any write. Contextual keywords (var, async, ...)
    // are legal identifiers and stay allowed.
    private static readonly HashSet<string> CSharpReservedKeywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
    };

    // Deterministic adapter rejections that used to surface at execute time — after a sibling
    // write in the same ChangeSet had landed — and therefore dead-ended as RecoveryRequired.
    // Validated here against the pre-write snapshot (same pattern as the socket-removal preflight
    // above) so they land as a clean deterministic Failed with zero writes. STRICTLY NARROWER than
    // the adapter: anything this method cannot prove the adapter would reject passes through —
    // objects created inside this ChangeSet, sockets a same-ChangeSet schema write may append,
    // and every type hint (the adapter accepts ANY hint and degrades unknown ones to a generic
    // socket — see GrasshopperPythonFoundationAdapter.ResolveSafeType and the accept-all comment
    // above its allowObject branch — so a hint whitelist here would mint new false declines).
    private static void PreflightDeterministicAdapterRejections(
        IReadOnlyList<PreparedOperation> prepared,
        SnapshotEnvelope before)
    {
        foreach (var item in prepared)
        {
            switch (item.BridgeOperation)
            {
                case "canvas.setWire":
                    PreflightWireEndpoints(item, prepared, before);
                    break;
                case "canvas.create":
                    PreflightCreateTypeInstanceConfusion(item, before);
                    break;
                case "python.setTyping":
                    PreflightTypingTarget(item, prepared, before);
                    break;
                case "python.setSchema":
                    PreflightSchemaSocketNames(item, prepared, before);
                    break;
                case "python.execute":
                    PreflightExecuteCost(item, before);
                    break;
                case "python.setSource":
                    PreflightSourceBudgetGuard(item);
                    PreflightSdkSourceGuard(item);
                    break;
            }
        }
    }

    // Layer 2 (self-limiting budget guard) enforcement. A running solve holds Rhino's single UI thread
    // and cannot be aborted from outside (the thread that would process an abort IS the blocked one), so
    // a truly infinite loop freezes Rhino forever — the only escape is the script throwing from inside the
    // loop. The house-rule teaches every large loop to carry a stopwatch/iteration budget that throws; this
    // is the hard backstop for the unambiguous case. DELIBERATELY conservative — it rejects a source ONLY
    // when it has an unbounded loop header (while(true)/for(;;)/while True) AND contains no exit or guard
    // mechanism anywhere (no break/return/throw/raise/goto/yield, no escape-key/stopwatch/time check). That
    // combination is an unconditional freeze; anything with any exit path passes through untouched, so valid
    // scripts are never blocked (recall for merely-large bounded loops is covered by the house-rule + the
    // layer-1 cost gate, not here). Never rewrites the source — the model owns its text so a read-back on the
    // next edit stays consistent.
    private static readonly string[] LoopEscapeTokens =
    [
        "break", "return", "throw", "raise", "goto", "yield",
        "EscapeKeyPressed", "ElapsedMilliseconds", "time.time", "__sw", "__t0",
    ];

    private static void PreflightSourceBudgetGuard(PreparedOperation item)
    {
        if (!item.Arguments.TryGetProperty("source", out var sourceElement) ||
            sourceElement.ValueKind != JsonValueKind.String)
        {
            return;
        }
        var source = sourceElement.GetString();
        if (string.IsNullOrEmpty(source))
        {
            return;
        }
        var isCSharp = item.Arguments.TryGetProperty("runtime", out var runtimeElement) &&
            runtimeElement.ValueKind == JsonValueKind.String &&
            string.Equals(runtimeElement.GetString(), "csharp", StringComparison.OrdinalIgnoreCase);
        if (!HasUnboundedLoopWithoutEscape(source, isCSharp))
        {
            return;
        }
        throw new InvalidOperationException(
            $"Operation '{item.Operation.OperationId}': the script has an unbounded loop " +
            "(while(true) / for(;;) / while True) with no break, return, throw/raise, or solve-budget guard " +
            "anywhere — it will spin forever and freeze Rhino on the single UI thread, which cannot be aborted " +
            "from outside once the solve starts. Rejected before any write. Add a self-limiting budget guard " +
            "that throws when a stopwatch/iteration cap is exceeded (see the house-rule), or a bounded exit " +
            "condition, before resubmitting.");
    }

    /// <summary>
    /// Pure detector for the conservative infinite-loop backstop: true only when the source has an
    /// unbounded loop header and NO exit/guard token anywhere. Unit-tested without a live document.
    /// </summary>
    internal static bool HasUnboundedLoopWithoutEscape(string source, bool isCSharp)
    {
        if (string.IsNullOrEmpty(source) || !ContainsUnboundedLoopHeader(source, isCSharp))
        {
            return false;
        }
        foreach (var token in LoopEscapeTokens)
        {
            if (source.Contains(token, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ContainsUnboundedLoopHeader(string source, bool isCSharp)
    {
        foreach (var rawLine in source.Split('\n'))
        {
            var line = rawLine.Trim();
            // Skip whole-line comments so a commented-out loop never trips the backstop.
            if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }
            var code = line;
            if (isCSharp)
            {
                var slashSlash = code.IndexOf("//", StringComparison.Ordinal);
                if (slashSlash >= 0)
                {
                    code = code[..slashSlash];
                }
            }
            else
            {
                var hash = code.IndexOf('#');
                if (hash >= 0)
                {
                    code = code[..hash];
                }
            }
            var compact = new string(code.Where(c => !char.IsWhiteSpace(c)).ToArray());
            if (isCSharp)
            {
                if (compact.Contains("while(true)", StringComparison.Ordinal) ||
                    compact.Contains("for(;;)", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            else if (compact.Contains("whileTrue:", StringComparison.Ordinal) ||
                     compact.Contains("while1:", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    // SDK-style C# sources (a GH_ScriptInstance/GH_Component class wrapper or a RunScript method)
    // never compile in a Rhino 8 script component — RhinoCode takes plain top-level statements —
    // and the failure used to surface only AFTER the source write landed, dead-ending the job.
    // Reject the payload BEFORE any write. The declared runtime gates the check: the detector
    // applies ONLY when the payload explicitly declares runtime 'csharp'. When it is absent or
    // different, skip — the adapter refuses a missing/mismatched runtime pre-write anyway
    // (precondition_refused), and the C#-shaped patterns CAN collide with Python-ish text (e.g.
    // 'class MyHelper:' next to a bare GH_Component mention), so guessing here would risk
    // false-rejecting a valid Python source.
    private static void PreflightSdkSourceGuard(PreparedOperation item)
    {
        if (!item.Arguments.TryGetProperty("source", out var sourceElement) ||
            sourceElement.ValueKind != JsonValueKind.String)
        {
            return;
        }
        var source = sourceElement.GetString();
        if (string.IsNullOrEmpty(source))
        {
            return;
        }
        if (!item.Arguments.TryGetProperty("runtime", out var runtimeElement) ||
            runtimeElement.ValueKind != JsonValueKind.String ||
            !string.Equals(runtimeElement.GetString(), "csharp", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        if (!LooksLikeSdkComponentSource(source))
        {
            return;
        }
        throw new InvalidOperationException(
            $"Operation '{item.Operation.OperationId}': C# sources must be Rhino 8 script-mode: " +
            "plain top-level statements, no class/GH_ScriptInstance/RunScript wrapper. Declare " +
            "sockets with setComponentIo and read/assign socket-named variables. Rejected before " +
            "any write. See the gh-csharp-cookbook skill.");
    }

    // Kept in LOCKSTEP with GrasshopperPythonFoundationAdapter.LooksLikeSdkComponentSource (the
    // adapter's in-process backstop; duplicated because the AgentHost does not reference the
    // Grasshopper plugin assembly). Conservative by design: only a class declaration whose base
    // TYPE is GH_ScriptInstance/GH_Component (never a mere generic argument such as
    // IComparer<GH_Component>), or a modifier-prefixed 'void RunScript(' SDK signature, matches —
    // plain top-level script-mode statements (including a local 'void RunScript()' helper
    // function) never do. Comments (line and block) and string literals are stripped first so a
    // commented-out or quoted wrapper (or the '// #! csharp' directive) never trips it.
    private static readonly System.Text.RegularExpressions.Regex SdkClassBaseListPattern = new(
        @"\bclass\s+\w+\s*:\s*([^{;]+)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex SdkBaseTypeNamePattern = new(
        @"\b(GH_ScriptInstance|GH_Component)\b",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex SdkRunScriptSignaturePattern = new(
        @"\b(private|public|protected|internal|override)\s+(static\s+)?void\s+RunScript\s*\(",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Pure detector for SDK-class C# component source (vs. Rhino 8 script-mode top-level
    /// statements). Unit-tested without a live document.
    /// </summary>
    internal static bool LooksLikeSdkComponentSource(string source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return false;
        }
        var text = StripCommentsAndStringLiterals(source);
        if (SdkRunScriptSignaturePattern.IsMatch(text))
        {
            return true;
        }
        foreach (System.Text.RegularExpressions.Match declaration in SdkClassBaseListPattern.Matches(text))
        {
            if (BaseListNamesSdkType(declaration.Groups[1].Value))
            {
                return true;
            }
        }
        return false;
    }

    // True only when GH_ScriptInstance/GH_Component appears in the segment BEFORE the first '<'
    // of a base entry (entries split on top-level commas) — i.e. as the base TYPE itself. A
    // generic ARGUMENT (IComparer<GH_Component>) sits behind a '<' and never matches.
    private static bool BaseListNamesSdkType(string baseList)
    {
        var depth = 0;
        var entryStart = 0;
        for (var index = 0; index <= baseList.Length; index++)
        {
            if (index < baseList.Length)
            {
                var current = baseList[index];
                if (current == '<')
                {
                    depth++;
                    continue;
                }
                if (current == '>')
                {
                    depth = Math.Max(0, depth - 1);
                    continue;
                }
                if (current != ',' || depth != 0)
                {
                    continue;
                }
            }
            var entry = baseList[entryStart..index];
            var angle = entry.IndexOf('<');
            if (SdkBaseTypeNamePattern.IsMatch(angle >= 0 ? entry[..angle] : entry))
            {
                return true;
            }
            entryStart = index + 1;
        }
        return false;
    }

    // Replaces //-line and /* */ block comments plus string literals ("..." with \" escapes,
    // verbatim @"..."/$@"..." with "" escapes) and char literals with spaces, so quoted or
    // commented SDK shapes never reach the patterns. Newlines are preserved through line
    // comments to keep the remaining code line-shaped.
    private static string StripCommentsAndStringLiterals(string source)
    {
        var stripped = new StringBuilder(source.Length);
        var index = 0;
        while (index < source.Length)
        {
            var current = source[index];
            var next = index + 1 < source.Length ? source[index + 1] : '\0';
            if (current == '/' && next == '/')
            {
                while (index < source.Length && source[index] != '\n')
                {
                    index++;
                }
                continue;
            }
            if (current == '/' && next == '*')
            {
                index += 2;
                while (index + 1 < source.Length && !(source[index] == '*' && source[index + 1] == '/'))
                {
                    index++;
                }
                index = Math.Min(source.Length, index + 2);
                stripped.Append(' ');
                continue;
            }
            if (current == '\'')
            {
                // Char literal — it may contain a raw or escaped quote that would otherwise open
                // a phantom string.
                index++;
                while (index < source.Length && source[index] != '\'' && source[index] != '\n')
                {
                    index += source[index] == '\\' ? 2 : 1;
                }
                if (index < source.Length && source[index] == '\'')
                {
                    index++;
                }
                stripped.Append(' ');
                continue;
            }
            if (current == '"')
            {
                var lookback = index - 1;
                while (lookback >= 0 && source[lookback] == '$')
                {
                    lookback--;
                }
                var verbatim = lookback >= 0 && source[lookback] == '@';
                index++;
                if (verbatim)
                {
                    while (index < source.Length)
                    {
                        if (source[index] != '"')
                        {
                            index++;
                            continue;
                        }
                        if (index + 1 < source.Length && source[index + 1] == '"')
                        {
                            index += 2;
                            continue;
                        }
                        index++;
                        break;
                    }
                }
                else
                {
                    while (index < source.Length && source[index] != '"' && source[index] != '\n')
                    {
                        index += source[index] == '\\' ? 2 : 1;
                    }
                    if (index < source.Length && source[index] == '"')
                    {
                        index++;
                    }
                }
                stripped.Append(' ');
                continue;
            }
            stripped.Append(current);
            index++;
        }
        return stripped.ToString();
    }

    // The GH document solves on Rhino's single UI thread, so a script whose loop count is driven by
    // large resolution sliders can freeze Rhino for the whole solve — there is no way to abort it
    // mid-flight. When the count-like sliders wired straight into an executed component multiply out
    // to an egregiously large element count, reject the execute BEFORE it runs so the model lowers
    // the counts or stages the work first. Conservative by design: only whole-number sliders whose
    // socket name reads like a resolution knob are counted, and the threshold is high, so ordinary
    // work is never blocked — a heavy solve with no such slider simply is not caught here.
    // Established components (already solved and committed at least once — a non-empty ValueFingerprint
    // in the before-snapshot) may run up to this hard ceiling; beyond it a solve is egregiously large and
    // will freeze Rhino on the UI thread, so it is rejected before any write.
    private const long ExecuteElementCostBlockThreshold = 2_000_000;

    // First-solve ceiling (layer 1 — "low-resolution first"): a component that has never produced a
    // committed solve (null/empty ValueFingerprint) must make its FIRST execute low-resolution, so the
    // true solve cost is measured cheaply and checkpointed BEFORE the counts are raised. A never-solved
    // component whose resolution sliders already multiply past this is rejected before the write, with
    // guidance to run a low-res pass first (see the staged-authoring house-rule). This substitutes for the
    // impossible task of predicting an arbitrary solve's runtime: instead of guessing, make the first touch
    // cheap and observable. Restart-safe — the signal is the persisted snapshot, not in-memory state — and
    // the failure direction is safe (an unknown/unreported ValueFingerprint falls back to the higher
    // established ceiling, which still blocks the catastrophic case). ~100x100 grid passes; 200x200 does not.
    private const long FirstSolveElementCostThreshold = 10_000;

    private static readonly string[] CountKnobKeywords =
    [
        "count", "num", "span", "div", "segment", "seg", "sample", "resolution", "res",
        "subdiv", "grid", "row", "col", "column", "density", "cell", "step", "tile",
    ];

    /// <summary>
    /// Pure gate decision so it is unit-tested without a live document: an execute solving
    /// <paramref name="estimate"/> elements is blocked when it exceeds the ceiling for the component's
    /// maturity. <paramref name="established"/> is true once the component has a committed solve.
    /// </summary>
    internal static bool ShouldBlockExecuteCost(long estimate, bool established, out long ceiling)
    {
        ceiling = established ? ExecuteElementCostBlockThreshold : FirstSolveElementCostThreshold;
        return estimate > ceiling;
    }

    private static void PreflightExecuteCost(PreparedOperation item, SnapshotEnvelope before)
    {
        if (!item.Arguments.TryGetProperty("componentId", out var componentElement) ||
            !componentElement.TryGetGuid(out var componentId))
        {
            return;
        }
        var (estimate, knobs) = EstimateExecuteElementCost(before.Canvas, componentId);
        if (estimate == 0)
        {
            return;
        }
        var component = before.Canvas.Objects.FirstOrDefault(obj => obj.ObjectId == componentId);
        var established = component is not null && !string.IsNullOrEmpty(component.ValueFingerprint);
        if (!ShouldBlockExecuteCost(estimate, established, out _))
        {
            return;
        }
        if (established)
        {
            throw new InvalidOperationException(
                $"Operation '{item.Operation.OperationId}': executing component {componentId:D} would solve " +
                $"~{estimate:N0} elements from its resolution sliders ({string.Join(", ", knobs)}), which will " +
                "freeze Rhino on the UI thread — Grasshopper cannot abort a running solve. Rejected before any " +
                "write. Lower those slider counts and run a low-resolution pass first, or split the work into " +
                "staged components (each executed and verified in turn); raise resolution only after a committed " +
                "low-resolution solve.");
        }
        throw new InvalidOperationException(
            $"Operation '{item.Operation.OperationId}': component {componentId:D} has never produced a committed " +
            $"solve, and this first execute would solve ~{estimate:N0} elements from its resolution sliders " +
            $"({string.Join(", ", knobs)}) — over the {FirstSolveElementCostThreshold:N0}-element first-pass limit. " +
            "A new component's FIRST execute must be low-resolution so its real solve cost is measured cheaply " +
            "before scaling: lower those slider counts to run a low-resolution pass, verify it commits, then raise " +
            "the counts. Rejected before any write (an untested heavy solve freezes Rhino on the UI thread, which " +
            "Grasshopper cannot abort).");
    }

    /// <summary>
    /// Estimates the element count an execute would solve as the product of the whole-number
    /// "resolution" sliders wired directly into the component's inputs (a socket named like a count
    /// — see <see cref="CountKnobKeywords"/>). Pure so the estimator is unit-tested without a live
    /// document. Returns (0, empty) when no such slider drives the component, so it never guesses.
    /// </summary>
    internal static (long Estimate, IReadOnlyList<string> Knobs) EstimateExecuteElementCost(
        CanvasSnapshot canvas,
        Guid componentId)
    {
        var component = canvas.Objects.FirstOrDefault(obj => obj.ObjectId == componentId);
        if (component is null)
        {
            return (0, Array.Empty<string>());
        }
        long product = 1;
        var knobs = new List<string>();
        foreach (var input in component.Inputs)
        {
            var socketName = $"{input.Name} {input.NickName}".ToLowerInvariant();
            if (!CountKnobKeywords.Any(keyword => socketName.Contains(keyword, StringComparison.Ordinal)))
            {
                continue;
            }
            foreach (var source in input.CurrentSources)
            {
                var sourceObject = canvas.Objects.FirstOrDefault(obj => obj.ObjectId == source.OwnerObjectId);
                if (sourceObject?.ValueJson is not { } valueJson ||
                    !TryReadWholeSliderValue(valueJson, out var value) ||
                    value < 2)
                {
                    continue;
                }
                // Clamp to avoid overflow on absurd inputs; the clamp is still far past the threshold.
                product = value > long.MaxValue / product ? long.MaxValue : product * value;
                knobs.Add($"{(string.IsNullOrWhiteSpace(input.NickName) ? input.Name : input.NickName)}={value}");
            }
        }
        return knobs.Count > 0 ? (product, knobs) : (0, Array.Empty<string>());
    }

    private static bool TryReadWholeSliderValue(string valueJson, out long value)
    {
        value = 0;
        try
        {
            using var document = JsonDocument.Parse(valueJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("kind", out var kind) ||
                kind.ValueKind != JsonValueKind.String ||
                !string.Equals(kind.GetString(), "numberSlider", StringComparison.Ordinal) ||
                !root.TryGetProperty("value", out var valueElement) ||
                valueElement.ValueKind != JsonValueKind.Number)
            {
                return false;
            }
            // Only whole-number sliders count as loop knobs; a fractional slider (e.g. sag=1.5) is a
            // dimension, not an iteration count.
            var raw = valueElement.GetDouble();
            if (Math.Abs(raw - Math.Round(raw)) > 1e-9)
            {
                return false;
            }
            value = (long)Math.Round(raw);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Shared componentTypeId reader for the canvas.create preflights. A componentTypeId that is
    // present but not a GUID string would silently skip both guards and die at adapter
    // deserialization AFTER sibling writes landed — the exact mid-batch dead-end (FIX A) the
    // preflights exist to prevent — so a present-but-malformed value is rejected here with an
    // actionable message instead. NOTE: JsonElement.TryGetGuid THROWS InvalidOperationException
    // on non-string kinds (null/number/...), so ValueKind is checked first. Returns false only
    // when the property is absent entirely (left for payload validation to report).
    internal static bool TryReadCreateComponentTypeId(
        JsonElement arguments,
        string operationId,
        out Guid componentTypeId)
    {
        componentTypeId = Guid.Empty;
        if (!arguments.TryGetProperty("componentTypeId", out var typeElement))
        {
            return false;
        }
        if (typeElement.ValueKind != JsonValueKind.String ||
            !typeElement.TryGetGuid(out componentTypeId))
        {
            var actual = typeElement.ValueKind == JsonValueKind.String
                ? $"'{typeElement.GetString()}'"
                : $"a JSON {typeElement.ValueKind.ToString().ToLowerInvariant()}";
            throw new InvalidOperationException(
                $"Operation '{operationId}': componentTypeId must be a GUID string, but the " +
                $"payload carries {actual}. Rejected before any write. Use the component TYPE id " +
                "from a component_catalog search (or the well-known GUID table in the " +
                "gh-authoring skill) and resubmit.");
        }
        return true;
    }

    // Instance/type confusion guard: a canvas.create whose componentTypeId is actually the id of
    // an EXISTING canvas object is a deterministic execute-time failure (EmitObject cannot emit an
    // instance id) that used to dead-end mid-batch. The snapshot proves the confusion and names
    // the object's real TYPE id, so one retry can fix it. STRICTLY NARROWER: ids minted for
    // objects created inside this same ChangeSet are absent from the snapshot and pass through.
    private static void PreflightCreateTypeInstanceConfusion(
        PreparedOperation item,
        SnapshotEnvelope before)
    {
        if (!TryReadCreateComponentTypeId(
                item.Arguments,
                item.Operation.OperationId,
                out var componentTypeId))
        {
            return;
        }
        var existing = before.Canvas.Objects.FirstOrDefault(obj => obj.ObjectId == componentTypeId);
        if (existing is null)
        {
            return;
        }
        if (existing.ComponentTypeId == componentTypeId)
        {
            // The object's OWN type id equals the requested GUID: the GUID is provably a real
            // component TYPE id (a previous create in this session used a type GUID as its
            // objectId). Rejecting here would refuse a perfectly legitimate create.
            return;
        }
        throw new InvalidOperationException(
            $"Operation '{item.Operation.OperationId}': {componentTypeId:D} is the instance id of " +
            $"canvas object \"{existing.Name}\" — its component TYPE id is " +
            $"{existing.ComponentTypeId:D}. Did you mean that? Rejected before any write; " +
            "componentTypeId must be a component TYPE id (from component_catalog), never a canvas " +
            "object id.");
    }

    private static void PreflightWireEndpoints(
        PreparedOperation item,
        IReadOnlyList<PreparedOperation> prepared,
        SnapshotEnvelope before)
    {
        if (!item.Arguments.TryGetProperty("wire", out var wire) ||
            wire.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        PreflightWireEndpoint(item, prepared, before, wire, source: true);
        PreflightWireEndpoint(item, prepared, before, wire, source: false);
    }

    private static void PreflightWireEndpoint(
        PreparedOperation item,
        IReadOnlyList<PreparedOperation> prepared,
        SnapshotEnvelope before,
        JsonElement wire,
        bool source)
    {
        if (!wire.TryGetProperty(source ? "sourceObjectId" : "targetObjectId", out var objectElement) ||
            !objectElement.TryGetGuid(out var objectId) ||
            !wire.TryGetProperty(source ? "sourceParameterId" : "targetParameterId", out var parameterElement) ||
            !parameterElement.TryGetGuid(out var parameterId))
        {
            return;
        }
        var owner = before.Canvas.Objects.FirstOrDefault(obj => obj.ObjectId == objectId);
        if (owner is null)
        {
            // Created inside this ChangeSet: the snapshot cannot see it — let the adapter decide.
            if (ChangeSetCreatesObject(prepared, objectId))
            {
                return;
            }
            throw new InvalidOperationException(
                $"Operation '{item.Operation.OperationId}': Grasshopper {(source ? "source" : "target")} " +
                $"object {objectId:D} was not found in the pre-write snapshot and no operation in this " +
                "ChangeSet creates it. Rejected before any write; wire to an existing object id " +
                "(job results carry socket ids under committed.sockets).");
        }
        // A same-ChangeSet schema write may append sockets the snapshot cannot see yet.
        if (ChangeSetEditsComponentSchema(prepared, objectId))
        {
            return;
        }
        var side = source ? owner.Outputs : owner.Inputs;
        if (side.Any(parameter => parameter.ParameterId == parameterId))
        {
            return;
        }
        var available = side.Count == 0
            ? "none"
            : string.Join(", ", side.Select(parameter => $"{parameter.Name}={parameter.ParameterId:D}"));
        // Common mistake: using the component's own object id as the socket id. Sockets have their
        // own ids, distinct from the component that owns them — name it explicitly.
        var confusionHint = parameterId == objectId
            ? $" (You used the {(source ? "source" : "target")} object's own id as its parameter id; a " +
              "socket id is never the component id — pick one of the listed socket ids.)"
            : string.Empty;
        throw new InvalidOperationException(
            $"Operation '{item.Operation.OperationId}': Grasshopper {(source ? "source" : "target")} " +
            $"parameter {parameterId:D} on object {objectId:D} was not found in the pre-write snapshot. " +
            $"Available {(source ? "output" : "input")} sockets: {available}. Rejected before any write; " +
            "wire to one of the listed name=id pairs." + confusionHint);
    }

    private static void PreflightTypingTarget(
        PreparedOperation item,
        IReadOnlyList<PreparedOperation> prepared,
        SnapshotEnvelope before)
    {
        if (!item.Arguments.TryGetProperty("componentId", out var componentElement) ||
            !componentElement.TryGetGuid(out var componentId) ||
            !item.Arguments.TryGetProperty("inputParameterId", out var parameterElement) ||
            !parameterElement.TryGetGuid(out var parameterId))
        {
            return;
        }
        var component = before.Canvas.Objects.FirstOrDefault(obj => obj.ObjectId == componentId);
        if (component is null ||
            !IsScriptComponentType(component.ComponentTypeId) ||
            ChangeSetEditsComponentSchema(prepared, componentId))
        {
            // Unknown, non-script, or reshaped by this same ChangeSet — let the adapter decide.
            return;
        }
        if (component.Inputs.Any(parameter => parameter.ParameterId == parameterId))
        {
            return;
        }
        var available = component.Inputs.Count == 0
            ? "none"
            : string.Join(", ", component.Inputs.Select(parameter =>
                $"{parameter.Name}={parameter.ParameterId:D}"));
        var confusionHint = parameterId == componentId
            ? " (You used the component's own id as the input parameter id; a socket id is never the " +
              "component id — pick one of the listed socket ids.)"
            : string.Empty;
        throw new InvalidOperationException(
            $"Operation '{item.Operation.OperationId}': Python input {parameterId:D} was not found on " +
            $"component {componentId:D} in the pre-write snapshot. Available input sockets: {available}. " +
            "Rejected before any write; use one of the listed name=id pairs (job results carry them " +
            "under committed.sockets)." + confusionHint);
    }

    // Socket names become script variables. Two deterministic adapter/compiler rejections are
    // caught pre-write: (1) names the adapter's ValidateSchema rejects via IsPythonIdentifier —
    // mirrored EXACTLY (Unicode letters allowed, spaces/punctuation not) so this preflight never
    // rejects a name the adapter would accept; (2) on C# components, C# reserved keywords, which
    // RhinoCode rejects at compile time after the write has landed.
    private static void PreflightSchemaSocketNames(
        PreparedOperation item,
        IReadOnlyList<PreparedOperation> prepared,
        SnapshotEnvelope before)
    {
        if (!item.Arguments.TryGetProperty("componentId", out var componentElement) ||
            !componentElement.TryGetGuid(out var componentId))
        {
            return;
        }
        var names = SchemaSocketNames(item.Arguments, "inputs")
            .Concat(SchemaSocketNames(item.Arguments, "outputs"))
            .ToArray();
        foreach (var name in names)
        {
            if (!IsSafeScriptIdentifier(name))
            {
                throw new InvalidOperationException(
                    $"Operation '{item.Operation.OperationId}': '{name}' is not a safe Python variable " +
                    "name. Socket names become script variables — use letters, digits, and underscores, " +
                    "starting with a letter or underscore (no spaces). Rejected before any write.");
            }
        }
        if (!IsCSharpScriptComponent(componentId, before, prepared))
        {
            return;
        }
        foreach (var name in names)
        {
            if (CSharpReservedKeywords.Contains(name))
            {
                var hint = string.Equals(name, "out", StringComparison.Ordinal)
                    ? "'console_log'"
                    : $"'{name}_value'";
                throw new InvalidOperationException(
                    $"Operation '{item.Operation.OperationId}': '{name}' is a C# reserved keyword and " +
                    "cannot be a socket/variable name on a C# script component (RhinoCode rejects it at " +
                    $"compile time). Rename it (e.g. {hint}). Rejected before any write.");
            }
        }
    }

    // Mirrors GrasshopperPythonFoundationAdapter.IsPythonIdentifier exactly — including Unicode
    // letters — so this preflight never rejects a name the adapter would accept.
    private static bool IsSafeScriptIdentifier(string value) =>
        !string.IsNullOrEmpty(value) &&
        (char.IsLetter(value[0]) || value[0] == '_') &&
        value.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');

    private static bool IsScriptComponentType(Guid componentTypeId) =>
        componentTypeId == Cpython3ScriptComponentTypeId ||
        componentTypeId == IronPython2ScriptComponentTypeId ||
        componentTypeId == CSharpScriptComponentTypeId;

    private static bool IsCSharpScriptComponent(
        Guid componentId,
        SnapshotEnvelope before,
        IReadOnlyList<PreparedOperation> prepared)
    {
        var component = before.Canvas.Objects.FirstOrDefault(obj => obj.ObjectId == componentId);
        if (component is not null && IsScriptComponentType(component.ComponentTypeId))
        {
            return component.ComponentTypeId == CSharpScriptComponentTypeId;
        }
        foreach (var item in prepared)
        {
            if (string.Equals(item.BridgeOperation, "canvas.create", StringComparison.Ordinal) &&
                item.Arguments.TryGetProperty("objectId", out var objectElement) &&
                objectElement.TryGetGuid(out var objectId) &&
                objectId == componentId &&
                item.Arguments.TryGetProperty("componentTypeId", out var typeElement) &&
                typeElement.TryGetGuid(out var typeId) &&
                typeId == CSharpScriptComponentTypeId)
            {
                return true;
            }
            if (string.Equals(item.BridgeOperation, "python.setSource", StringComparison.Ordinal) &&
                item.Arguments.TryGetProperty("componentId", out var sourceElement) &&
                sourceElement.TryGetGuid(out var sourceComponentId) &&
                sourceComponentId == componentId &&
                item.Arguments.TryGetProperty("runtime", out var runtime) &&
                runtime.ValueKind == JsonValueKind.String &&
                string.Equals(runtime.GetString(), "csharp", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ChangeSetCreatesObject(
        IReadOnlyList<PreparedOperation> prepared,
        Guid objectId) =>
        prepared.Any(item =>
            string.Equals(item.BridgeOperation, "canvas.create", StringComparison.Ordinal) &&
            item.Arguments.TryGetProperty("objectId", out var element) &&
            element.TryGetGuid(out var id) &&
            id == objectId);

    private static bool ChangeSetEditsComponentSchema(
        IReadOnlyList<PreparedOperation> prepared,
        Guid componentId) =>
        prepared.Any(item =>
            string.Equals(item.BridgeOperation, "python.setSchema", StringComparison.Ordinal) &&
            item.Arguments.TryGetProperty("componentId", out var element) &&
            element.TryGetGuid(out var id) &&
            id == componentId);

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
                // Name the cause, not the plumbing: "no adapter 'Canvas'" reads like a
                // broken bridge, when the truth is simply that no definition is open — a state the
                // reader can act on (open one, or do Rhino-side work instead).
                if (!targetState.Target.HasGrasshopper)
                {
                    throw new InvalidOperationException(
                        "No Grasshopper definition is open for this Rhino document, so canvas work " +
                        "is unavailable. Rhino-side operations (layers, document tables, objects) " +
                        "still work.");
                }
                throw new InvalidOperationException(
                    $"The bound document does not advertise adapter '{owner}'.");
            }
        }
    }

    // The DEFAULT target: the only registered target when exactly one Grasshopper document is
    // open (today's single-document behavior, byte-for-byte), otherwise the first registered.
    /// <summary>
    /// The default target: the first-registered one that HAS a Grasshopper document, falling back
    /// to the first registered at all. The Rhino-only target registers first — it exists before any
    /// definition is opened — so without the preference it would stay the default forever and the
    /// panel would report no Grasshopper file while one was open.
    /// </summary>
    private TargetState? DefaultTargetStateUnsafe() =>
        _targets.Values.Where(state => state.Target.HasGrasshopper).MinBy(state => state.Sequence)
        ?? _targets.Values.MinBy(state => state.Sequence);

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
                // Ambiguity is counted over GRASSHOPPER documents only. The Rhino-only target is
                // always registered and serves every Rhino-side operation, so it must never make an
                // unbound session look ambiguous — Rhino-only work is never bound to a .gh.
                var grasshopperTargets = _targets.Values.Count(state => state.Target.HasGrasshopper);
                if (grasshopperTargets <= 1)
                {
                    return DefaultTargetStateUnsafe()!;
                }
                throw new InvalidOperationException(
                    $"{char.ToUpperInvariant(subject[0])}{subject[1..]} is not bound to a Grasshopper document and " +
                    $"{grasshopperTargets} are registered. Bind the session to one document (or create sessions " +
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
                    .Where(state => state.Target.GrasshopperPath is not null)
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
        IReadOnlyDictionary<Guid, SessionRunState> states;
        lock (_scheduleGate)
        {
            states = _sessionStates;
        }
        if (_sessionHalts.IsEmpty)
        {
            return states;
        }
        // Overlay (on a copy — the source snapshot is shared): halted sessions read as Blocked so
        // the ReadyWorkScheduler never dispatches their queued jobs. Core stays untouched and
        // every other session keeps running.
        var overlaid = new Dictionary<Guid, SessionRunState>(states);
        foreach (var sessionId in _sessionHalts.Keys)
        {
            overlaid[sessionId] = SessionRunState.Blocked;
        }
        return overlaid;
    }

    private async Task SetJobPhaseAsync(
        LiveJobEntry entry,
        JobState state,
        string? message,
        IReadOnlyList<ChangeConflict>? blockingConflicts = null,
        string? phaseOverride = null,
        bool triggerHalt = true)
    {
        var phase = phaseOverride ?? state.ToString().ToLowerInvariant();
        // Terminal states can be re-asserted (executor sets them, then the broker's completion
        // observer sets the same state again); only genuine transitions go to the problem log.
        var isRepeat = state == entry.State &&
            string.Equals(message, entry.Message, StringComparison.Ordinal);
        // A repeated RecoveryRequired re-assert is a full no-op: the first write already recorded
        // it durably and latched the halt. Writing again would (a) re-latch a halt an intervening
        // resume just lifted and (b) revert the durable "recoveryrequired-acknowledged" phase that
        // resume recorded — the observer's late re-assert must never undo the user's resume.
        if (state == JobState.RecoveryRequired && isRepeat)
        {
            return;
        }
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
        // Auto-tidy/arrange submissions are excluded from the per-session last-terminal tracker:
        // it gates the NEXT turn's tidy, and a previous turn's async arrange ending Blocked must
        // not suppress the tidy of a fully-committed later turn.
        if ((state is JobState.Committed or JobState.RolledBack or JobState.Blocked
                or JobState.RecoveryRequired or JobState.Failed or JobState.Cancelled) &&
            !IsArrangeJob(entry))
        {
            _lastTerminalJobStates[entry.Session.Id] = state;
        }
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
        // The single funnel every RecoveryRequired transition passes through (execution paths and
        // the completion observer alike) doubles as the halt trigger. Idempotent: the latch is
        // first-writer-wins, so re-asserted terminals never re-halt or re-sweep. triggerHalt is
        // false only for the shutdown OperationCanceledException path — see ObserveCompletionAsync.
        if (state == JobState.RecoveryRequired && triggerHalt)
        {
            await HaltSessionForRecoveryAsync(entry.Session.Id, entry.Job.JobId, message)
                .ConfigureAwait(false);
        }
    }

    private const string HaltedByRecoveryPhase = "halted-by-recovery";
    private const string RecoveryAcknowledgedPhase = "recoveryrequired-acknowledged";

    // ArrangeSeedsAsync tags every layout submission (the model's arrange_layout tool and the
    // automatic post-turn tidy alike) with this idempotency-key prefix plus this summary prefix.
    // Both must match for IsArrangeJob so an ordinary change_submit can't collide by key alone.
    private const string ArrangeIdempotencyKeyPrefix = "arrange-";
    private const string ArrangeSummaryPrefix = "Auto-tidy layout";

    private static bool IsArrangeJob(LiveJobEntry entry) =>
        entry.IdempotencyKey.StartsWith(ArrangeIdempotencyKeyPrefix, StringComparison.Ordinal) &&
        entry.Summary.StartsWith(ArrangeSummaryPrefix, StringComparison.Ordinal);

    private static string HaltCancellationMessage(Guid haltJobId) =>
        $"Cancelled because job {haltJobId:D} ended recoveryRequired and the session was halted. " +
        "Inspect job_status / the document, report to the user, then call recovery_resume with " +
        "that jobId. Resubmit this work with a NEW idempotencyKey after resume if it is still wanted.";

    private void ThrowIfSessionHalted(Guid sessionId)
    {
        if (_sessionHalts.TryGetValue(sessionId, out var halt))
        {
            throw new InvalidOperationException(
                $"This session is halted: job {halt.JobId:D} ended recoveryRequired. Inspect " +
                "job_status and the live document, report the actual state to the user, then call " +
                $"recovery_resume with jobId {halt.JobId:D}. Queued jobs were cancelled — after " +
                "resume, resubmit any still-needed work with a NEW idempotencyKey.");
        }
    }

    /// <summary>Current halt of a session, or null when it is not halted (projection surface).</summary>
    internal SessionHaltState? TryReadSessionHalt(Guid sessionId) =>
        _sessionHalts.TryGetValue(sessionId, out var halt) ? halt : null;

    // Sets the latch (first halt wins) and discards the session's pending auto-tidy seeds so the
    // post-turn tidy can never fire on the incident's wreckage. Synchronous on purpose: the
    // restart restore path calls it while holding _submissionGate, and the halt path must NEVER
    // take that gate itself (the submit path holds it; the race is closed at the enqueue re-check).
    // `at` pins the latch to the incident time when the caller knows it (the restart restore path
    // passes the durable row's UpdatedAt); live halts default to now.
    private bool LatchSessionHalt(Guid sessionId, Guid jobId, string? message, DateTimeOffset? at = null)
    {
        var halt = new SessionHaltState(
            jobId,
            string.IsNullOrWhiteSpace(message) ? "The job ended recoveryRequired." : message,
            at ?? DateTimeOffset.UtcNow);
        if (!_sessionHalts.TryAdd(sessionId, halt))
        {
            return false;
        }
        _turnCreatedComponents.TryRemove(sessionId, out _);
        return true;
    }

    /// <summary>
    /// Halts ONE session after a RecoveryRequired job: latches it, discards its pending tidy
    /// seeds, and marks + broker-cancels its still-queued jobs (each job's completion observer
    /// then writes the durable Cancelled/"halted-by-recovery" record). Other sessions keep
    /// running. Internal (InternalsVisibleTo) so the race-close test can flip the latch mid-submit.
    /// </summary>
    internal Task HaltSessionForRecoveryAsync(Guid sessionId, Guid jobId, string? message)
    {
        if (!LatchSessionHalt(sessionId, jobId, message))
        {
            return Task.CompletedTask;
        }
        CancelQueuedSessionJobs(sessionId, jobId);
        _events.Publish();
        return Task.CompletedTask;
    }

    // Internal (InternalsVisibleTo) so the marker-race test can drive two concurrent sweeps over
    // the same queued job.
    internal void CancelQueuedSessionJobs(Guid sessionId, Guid haltJobId)
    {
        foreach (var entry in _jobs.Values
            .Where(candidate => candidate.Session.Id == sessionId &&
                candidate.Job.JobId != haltJobId &&
                candidate.State == JobState.Queued)
            .OrderBy(candidate => candidate.Job.EnqueueSequence))
        {
            // Mark BEFORE TryCancel so the marker happens-before the completion observer wakes.
            // Markers are removed ONLY at the observer's single consumption point (which is also
            // the single durable writer for halt cancellations) — so when this sweep and the
            // submit path's enqueue re-check race over the same queued job, neither can strip the
            // other's marker or double-write the row: whichever TryCancel wins resolves the broker
            // completion once, and the one observer writes the one teaching record. TryCancel
            // returning false (executing / already cancelled / not yet broker-enqueued) leaves the
            // marker in place on purpose: the observer consumes it at that job's terminal no
            // matter how it ends, and for the not-yet-enqueued case the submit path's re-check
            // completes the cancellation under the same marker. (A marker added just after a job's
            // observer already finished is unreachable and merely idles in the map — jobIds are
            // never reused, so it can never mislabel anything.)
            _haltCancelledJobs.TryAdd(entry.Job.JobId, haltJobId);
            _broker.TryCancel(entry.Job.JobId);
        }
    }

    /// <summary>
    /// Drops the session-scoped runtime latches (recovery halt, last-terminal tidy gate, per-turn
    /// tidy seeds) when a session is soft-deleted or purged. Without this, deleting a halted
    /// session would park an unresumable latch: the panel hides the session, so nothing can ever
    /// POST /resume for it again, and a later restore would come back frozen.
    /// </summary>
    public void ForgetSessionRuntimeState(Guid sessionId)
    {
        var hadHalt = _sessionHalts.TryRemove(sessionId, out _);
        _lastTerminalJobStates.TryRemove(sessionId, out _);
        _turnCreatedComponents.TryRemove(sessionId, out _);
        if (hadHalt)
        {
            _broker.NotifyScheduleChanged();
            _events.Publish();
        }
    }

    /// <summary>
    /// Lifts the recovery halt when <paramref name="jobId"/> names the halting job, acknowledging
    /// it durably (phase "recoveryrequired-acknowledged"). Idempotent: resuming a session that is
    /// not halted succeeds. A mismatched jobId returns the current halt for the error surface.
    /// </summary>
    internal async Task<SessionResumeOutcome> TryResumeSessionAsync(
        Guid sessionId,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        if (!_sessionHalts.TryGetValue(sessionId, out var halt))
        {
            return new SessionResumeOutcome(true, null);
        }
        if (halt.JobId != jobId)
        {
            return new SessionResumeOutcome(false, halt);
        }
        _sessionHalts.TryRemove(new KeyValuePair<Guid, SessionHaltState>(sessionId, halt));
        // Durable acknowledgment written DIRECTLY (not through SetJobPhaseAsync — the funnel
        // would re-latch the very halt this call is lifting).
        _jobs.TryGetValue(jobId, out var haltEntry);
        var acknowledgedMessage = haltEntry?.Message ?? halt.Message;
        try
        {
            await _jobStore.UpdateStateAsync(
                jobId,
                JobState.RecoveryRequired,
                RecoveryAcknowledgedPhase,
                acknowledgedMessage,
                cancellationToken).ConfigureAwait(false);
            haltEntry?.SetPhase(
                JobState.RecoveryRequired,
                RecoveryAcknowledgedPhase,
                acknowledgedMessage);
        }
        catch (KeyNotFoundException)
        {
            // No durable row for the halting job (latched without one); the resume still lifts.
        }
        _broker.NotifyScheduleChanged();
        _events.Publish();
        return new SessionResumeOutcome(true, null);
    }

    /// <summary>
    /// Panel resume (SHARED CONTRACT: POST /sessions/{id}/resume): lifts whatever halt the
    /// session currently has. Idempotent — a non-halted session is a successful no-op.
    /// </summary>
    internal async Task ResumeSessionFromPanelAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (_sessionHalts.TryGetValue(sessionId, out var halt))
        {
            await TryResumeSessionAsync(sessionId, halt.JobId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Model tool recovery_resume: jobId must name the halting job (self-correcting error).</summary>
    public async Task<object> ResumeSessionAsync(
        SessionRecord session,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var jobText = RequiredString(arguments, "jobId");
        if (!Guid.TryParse(jobText, out var jobId))
        {
            throw new InvalidOperationException("recovery_resume requires jobId as a UUID.");
        }
        var outcome = await TryResumeSessionAsync(session.Id, jobId, cancellationToken)
            .ConfigureAwait(false);
        if (!outcome.Resumed)
        {
            var halt = outcome.Halt!;
            return new
            {
                resumed = false,
                halt = new { jobId = halt.JobId, message = halt.Message, at = halt.At },
                message = "jobId does not match the halting job. This session is halted by job " +
                    $"{halt.JobId:D}; call recovery_resume again with that exact id.",
            };
        }
        return new
        {
            resumed = true,
            message = "Session resumed. Jobs queued at the halt were cancelled — resubmit any " +
                "still-needed work with a NEW idempotencyKey.",
        };
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

    private void RegisterRestoredEntry(LiveJobEntry entry, bool latchHalt)
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
        // The restart restore path does not flow through SetJobPhaseAsync (DurableJobStore marked
        // the interrupted rows RecoveryRequired inside RecoverInterruptedAsync), so interrupted
        // sessions come back HALTED here — honest state, resumed only explicitly. latchHalt is
        // true ONLY for jobs converted to RecoveryRequired in THIS process (this startup's
        // RecoverInterruptedAsync, or the duplicate-insert recovery in SubmitChangeAsync) —
        // acknowledged or previously recorded RecoveryRequired rows never re-latch. Latch only:
        // at restore every non-terminal sibling already became RecoveryRequired, so there is
        // nothing queued to sweep, and this is also safe under _submissionGate (the
        // duplicate-insert recovery path in SubmitChangeAsync holds it while registering). The
        // latch timestamp is the record's UpdatedAt (the incident/conversion moment), not "now",
        // so the panel shows when the job actually stopped.
        if (latchHalt && entry.State == JobState.RecoveryRequired)
        {
            LatchSessionHalt(entry.Session.Id, entry.Job.JobId, entry.Message, entry.UpdatedAt);
        }
    }

    // Test seam (InternalsVisibleTo): replays the completion observer's terminal re-assert for a
    // job — the same state+message write ObserveCompletionAsync issues after the executor already
    // recorded the terminal — so the repeat-suppression in SetJobPhaseAsync can be exercised
    // deterministically against a resume that landed in between.
    internal Task SimulateCompletionReassertForTestAsync(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var entry))
        {
            throw new InvalidOperationException($"Unknown job '{jobId:D}'.");
        }
        return SetJobPhaseAsync(entry, entry.State, entry.Message);
    }

    private static SessionRecord CreateRecoveredSession(DurableJobRecord record) =>
        new(
            record.SessionId,
            "Recovered session",
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
            // Single consumption point of the halt-cancellation markers, and the single durable
            // writer for halt cancellations: the halt paths (latch sweep / enqueue re-check) only
            // mark + broker-TryCancel, so however many of them raced over this job, exactly one
            // teaching record is written, exactly here. The marker is set BEFORE the cancellation
            // resolves this completion, so consumption is deterministic. The entry.State check
            // separates a pre-dispatch halt cancel (entry never left Queued — a successful
            // TryCancel proves the broker never took it) from an executor-side Cancelled, whose
            // own richer record must stand even if a stale halt marker exists.
            var haltMarked = _haltCancelledJobs.TryRemove(entry.Job.JobId, out var haltJobId);
            if (haltMarked && result.State == JobState.Cancelled && entry.State == JobState.Queued)
            {
                var message = HaltCancellationMessage(haltJobId);
                // Terminal phase FIRST, entry completion AFTER — a wait:true watcher that wakes
                // on the completion must never observe a stale Queued projection.
                await SetJobPhaseAsync(
                    entry,
                    JobState.Cancelled,
                    message,
                    phaseOverride: HaltedByRecoveryPhase).ConfigureAwait(false);
                entry.CompleteWith(new JobExecutionResult(entry.Job.JobId, JobState.Cancelled, message));
            }
            else
            {
                await SetJobPhaseAsync(entry, result.State, result.Message).ConfigureAwait(false);
                entry.CompleteWith(result);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown: SingleWriterBroker.DisposeAsync cancelled the pending completion. Consume
            // any halt marker (nothing else will) and record the honest interruption WITHOUT the
            // halt trigger: during shutdown a latch+sweep would race every sibling's own
            // OCE-observer write nondeterministically (Cancelled/"halted-by-recovery" vs
            // RecoveryRequired) and leak markers — and it buys nothing, because whether a session
            // must come back halted is decided by the NEXT startup's restore path from the
            // durable rows alone.
            _haltCancelledJobs.TryRemove(entry.Job.JobId, out _);
            const string message =
                "AgentHost stopped before this job reached a durable terminal state. " +
                "No operations will be replayed automatically; inspect the document before recovery.";
            await SetJobPhaseAsync(entry, JobState.RecoveryRequired, message, triggerHalt: false)
                .ConfigureAwait(false);
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

        // The expensive AreaMassProperties/VolumeMassProperties integration is computed by the adapter
        // only when this job actually declares an area/volume predicate to check — every other job
        // inspects outputs without paying that per-geometry cost.
        var includeMassProperties = changeSet.AcceptancePredicates.Any(predicate =>
            predicate.Kind is PredicateKind.AreaInRange or PredicateKind.VolumeInRange);

        var outputs = new List<JobComponentOutputs>(components.Length);
        foreach (var componentId in components)
        {
            try
            {
                // Direct bridge read: this runs while the executor holds the document WRITE gate, so
                // going through ReadBridgeQueryAsync (which enters the read gate) would deadlock.
                var request = new BridgeOperationRequest(
                    $"read-{Guid.NewGuid():N}",
                    BridgeAdapterOwner.Canvas,
                    "canvas.inspectOutputs",
                    BridgeOperationAccess.Read,
                    after.State.Revision,
                    ExpectedFingerprint: null,
                    WriterLeaseToken: null,
                    JsonSerializer.SerializeToElement(
                        new { objectId = componentId, includeMassProperties },
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

}
