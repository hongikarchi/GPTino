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
                // A Rhino-only target has no Grasshopper document to list. It is a real registered
                // target (the curator runs on it), it just contributes no row here.
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
        // targeted inspection scopes (wireify:<guid> / rhino:<guid>), omit the full document so a
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
            BridgeAdapterOwner.CordycepsRhino,
            "rhino.audit",
            arguments,
            cancellationToken);

    /// <summary>
    /// Full layer table + named layer states (rhino_layers tool + GET /layers). Deterministic
    /// layer inspection: every layer carries a fingerprint and the table carries one, so presence
    /// AND absence are provable — the precondition layer mutation was gated on.
    /// </summary>
    public Task<object> ReadRhinoLayersAsync(CancellationToken cancellationToken)
    {
        using var empty = JsonDocument.Parse("{}");
        return ReadBridgeQueryAsync(
            RequireDefaultTargetState(),
            BridgeAdapterOwner.CordycepsRhino,
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
            targetState, BridgeAdapterOwner.CordycepsCanvas, "canvas.listReferencedRhinoIds", cancellationToken)
            .ConfigureAwait(false);
        var stamped = await SendDataFlowReadAsync(
            targetState, BridgeAdapterOwner.CordycepsRhino, "rhino.listStampedObjects", cancellationToken)
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
            targets[0], BridgeAdapterOwner.CordycepsRhino, "rhino.listStampedObjects", cancellationToken)
            .ConfigureAwait(false);
        var changed = false;
        var liveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var targetState in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            liveKeys.Add(targetState.DocKey);
            var references = await SendDataFlowReadGatedAsync(
                targetState, BridgeAdapterOwner.CordycepsCanvas, "canvas.listReferencedRhinoIds", cancellationToken)
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
        RequireAdapter(targetState, BridgeAdapterOwner.CordycepsRhino);
        using var empty = JsonDocument.Parse("{}");
        var request = new BridgeOperationRequest(
            $"read-{Guid.NewGuid():N}",
            BridgeAdapterOwner.CordycepsRhino,
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
            BridgeAdapterOwner.CordycepsRhino,
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
                    AdapterOwner.Cordyceps,
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
                idempotencyKey = FormattableString.Invariant($"arrange-{Guid.NewGuid():N}"),
                summary = FormattableString.Invariant($"Auto-tidy layout ({writeSet.Count} components)"),
                wait,
            },
            BridgeProtocol.JsonOptions);

        return await SubmitChangeAsync(session, submission, cancellationToken).ConfigureAwait(false);
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
        // Curator sessions are deliberately absent from the priority order: the scheduler ranks
        // absent ids int.MaxValue, so document hygiene always yields to modeling work — durably,
        // regardless of when sessions are created or how the user reorders the rail.
        var order = new SessionOrderSnapshot(
            projectId,
            sessions
                .Where(item => !string.Equals(item.Role, "curator", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Id)
                .ToArray(),
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

            // Server-owned deterministic placement: rewrite every canvas.create whose model pivot is the
            // "gptino:auto" sentinel into a concrete, non-overlapping pivot computed against the live
            // before-snapshot, stripping autoUpstream so the (unchanged) Grasshopper adapter receives
            // today's exact contract. Mirrors gptino:auto fingerprint resolution above: only the dispatched
            // Arguments change — FrozenPayload (idempotency hash, reserved artifacts) is never touched, and
            // an existing human-placed object is never moved (it is only an immutable collision obstacle).
            preparedOperations = CanvasAutoPlacement.ResolveAutoPivots(preparedOperations, before.Canvas);

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
                            $"Wireify operation '{operation.OperationId}' returned an invalid fingerprint chain.");
                    }
                    rollingPythonFingerprints[pythonWrite.Id] = response.AfterFingerprint;
                }
                if (bridgeOwner is BridgeAdapterOwner.Wireify or BridgeAdapterOwner.CordycepsRhino)
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
            var state = !approvalRefusal && (liveChanged || writeMayHaveChanged)
                ? JobState.RecoveryRequired
                : JobState.Failed;
            var message = exception.Message;
            if (state == JobState.RecoveryRequired)
            {
                // The recovery manifest turns "review the document state" into a deterministic
                // worklist: which operations verifiably applied, which one was in flight (outcome
                // honestly unknown — never reported as failed), and which never dispatched.
                var manifest = BuildRecoveryManifest(
                    job.ChangeSet.Operations,
                    completedOperationIds,
                    inFlightOperationId);
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
            // A newly registered (or Save-As-renamed) document changes what the data-flow view
            // should cover; refresh in the background once the registration frame is answered.
            ScheduleDataFlowRefresh();
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
        OperationKind.UpdateRhinoAttributes or OperationKind.UpdateRhinoLayer or
        OperationKind.FixRhinoEndpointPair or OperationKind.PurgeTableEntries or
        OperationKind.MoveObjectsToLayer or OperationKind.UpdateRhinoLayerProperties or
        OperationKind.DeleteRhinoLayer or OperationKind.SaveRhinoLayerState or
        OperationKind.EnsureRhinoLayer;

    private static string ResolveBridgeOperation(TypedOperation operation, JsonElement payload)
    {
        var inferred = operation.Kind switch
        {
            OperationKind.MoveComponent or OperationKind.SetLayout => "canvas.move",
            OperationKind.SetValue => "canvas.setNumberSlider",
            OperationKind.ConnectWire or OperationKind.DisconnectWire => "canvas.setWire",
            OperationKind.CreateComponent => "canvas.create",
            OperationKind.ReferenceRhinoObjects => "canvas.referenceRhinoObjects",
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
            OperationKind.FixRhinoEndpointPair => "rhino.fixEndpointPair",
            OperationKind.PurgeTableEntries => "rhino.purgeTableEntries",
            OperationKind.MoveObjectsToLayer => "rhino.moveObjectsToLayer",
            OperationKind.UpdateRhinoLayerProperties => "rhino.updateLayer",
            OperationKind.DeleteRhinoLayer => "rhino.deleteLayer",
            OperationKind.SaveRhinoLayerState => "rhino.layerState",
            OperationKind.EnsureRhinoLayer => "rhino.ensureLayer",
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
            "canvas.referenceRhinoObjects" => new[] { "operationId", "objectId", "rhinoObjectIds", "paramType", "pivot" },
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
            "rhino.fixEndpointPair" => new[]
            {
                "operationId", "anchorObjectId", "anchorEnd", "moveObjectId", "moveEnd",
                "expectedAnchorFingerprint", "expectedFingerprint", "tolerance"
            },
            "rhino.purgeTableEntries" => new[] { "operationId", "entries" },
            // layerId is required even for a brand-new layer: the caller picks the identity so the
            // writeSet can declare it with the absent sentinel before it exists.
            "rhino.ensureLayer" => new[] { "operationId", "layerId", "fullPath" },
            "rhino.moveObjectsToLayer" => new[] { "operationId", "items", "targetLayerId" },
            "rhino.updateLayer" => new[] { "operationId", "layerId", "expectedFingerprint" },
            "rhino.deleteLayer" => new[] { "operationId", "layerId", "expectedFingerprint" },
            "rhino.layerState" => new[] { "operationId", "action", "name" },
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
                    ValidateCanvasCreateArguments(operation, arguments);
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
                    RequireNotPreApproved(rhinoDelete.Approved, operation.OperationId);
                    if (rhinoDelete.ObjectId == Guid.Empty ||
                        string.IsNullOrWhiteSpace(rhinoDelete.ExpectedFingerprint))
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' has an invalid Rhino delete payload.");
                    }
                    return;
                case "rhino.fixEndpointPair":
                    ValidateFixEndpointPairArguments(
                        DeserializeArguments<FixEndpointPairRequest>(arguments, operation.OperationId),
                        operation.OperationId);
                    return;
                case "rhino.purgeTableEntries":
                    ValidatePurgeArguments(
                        DeserializeArguments<PurgeTableEntriesRequest>(arguments, operation.OperationId),
                        operation.OperationId);
                    return;
                case "rhino.moveObjectsToLayer":
                    ValidateMoveObjectsArguments(
                        DeserializeArguments<MoveObjectsToLayerRequest>(arguments, operation.OperationId),
                        operation.OperationId);
                    return;
                case "rhino.updateLayer":
                    var layerUpdate = DeserializeArguments<UpdateRhinoLayerRequest>(arguments, operation.OperationId);
                    if (layerUpdate.LayerId == Guid.Empty ||
                        string.IsNullOrWhiteSpace(layerUpdate.ExpectedFingerprint) ||
                        (layerUpdate.ArgbColor is null && layerUpdate.Visible is null && layerUpdate.Locked is null))
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' has an invalid Rhino layer-update payload " +
                            "(it must change at least one of color, visible, locked).");
                    }
                    return;
                case "rhino.deleteLayer":
                    var layerDelete = DeserializeArguments<DeleteRhinoLayerRequest>(arguments, operation.OperationId);
                    if (layerDelete.LayerId == Guid.Empty ||
                        string.IsNullOrWhiteSpace(layerDelete.ExpectedFingerprint))
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' has an invalid Rhino layer-delete payload.");
                    }
                    return;
                case "rhino.layerState":
                    var layerState = DeserializeArguments<RhinoLayerStateRequest>(arguments, operation.OperationId);
                    if (string.IsNullOrWhiteSpace(layerState.Name) ||
                        layerState.Action is not ("save" or "restore" or "delete"))
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' needs a layer-state name and action save|restore|delete.");
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

    // canvas.create accepts either an explicit pivot:{x,y} (honored verbatim) OR the sentinel
    // pivot:"gptino:auto" with an optional sibling autoUpstream:[objectId,...] naming the
    // components/sliders that will feed the new one. The sentinel + autoUpstream cannot survive
    // strict CreateCanvasObjectRequest deserialization (BridgeProtocol.JsonOptions disallows
    // unmapped members and has no CanvasPoint case for a string), so the sentinel path is
    // hand-validated here; CanvasAutoPlacement.ResolveAutoPivots rewrites it into a concrete pivot
    // and strips autoUpstream just before bridge dispatch, so the adapter still sees today's shape.
    private static void ValidateCanvasCreateArguments(TypedOperation operation, JsonElement arguments)
    {
        var pivot = arguments.GetProperty("pivot");
        if (pivot.ValueKind == JsonValueKind.String)
        {
            if (!string.Equals(
                    pivot.GetString(),
                    CanvasAutoPlacement.AutoPivotSentinel,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Operation '{operation.OperationId}' pivot string must be " +
                    $"'{CanvasAutoPlacement.AutoPivotSentinel}' (server-computed placement) or an " +
                    "explicit {{x,y}} point.");
            }
            // objectId and componentTypeId are already enforced as non-empty UUIDs by GuidArguments
            // before this validator runs; only the optional autoUpstream needs shape checking here.
            if (arguments.TryGetProperty("autoUpstream", out var autoUpstream))
            {
                ValidateAutoUpstream(operation, autoUpstream);
            }
            return;
        }

        RequireOnlyProperties(pivot, operation.OperationId, "x", "y");
        if (arguments.TryGetProperty("autoUpstream", out _))
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' may declare autoUpstream only with pivot " +
                $"'{CanvasAutoPlacement.AutoPivotSentinel}'; an explicit {{x,y}} pivot owns its own " +
                "coordinates.");
        }
        var create = DeserializeArguments<CreateCanvasObjectRequest>(arguments, operation.OperationId);
        if (create.ObjectId == Guid.Empty || create.ComponentTypeId == Guid.Empty ||
            !float.IsFinite(create.Pivot.X) || !float.IsFinite(create.Pivot.Y))
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' has an invalid canvas create payload.");
        }
    }

    private static void ValidateAutoUpstream(TypedOperation operation, JsonElement autoUpstream)
    {
        if (autoUpstream.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' autoUpstream must be an array of object UUIDs.");
        }
        foreach (var element in autoUpstream.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(element.GetString(), out var id) || id == Guid.Empty)
            {
                throw new InvalidOperationException(
                    $"Operation '{operation.OperationId}' autoUpstream must contain non-empty object UUIDs.");
            }
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

    internal static void ValidatePrimitiveArguments(
        CreateRhinoPrimitiveRequest request,
        string operationId)
    {
        if (request.SourceDocKey is not null)
        {
            // Same anti-spoof rule as rhino.upsert: provenance is server-injected at execution.
            throw new InvalidOperationException(
                $"Operation '{operationId}' must not set sourceDocKey; provenance is stamped by the server.");
        }
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

    /// <summary>
    /// The Approved flag is server-injected at execution when a user approval grant covers the
    /// object; a model-authored payload carrying it would let the human-wins default-deny be
    /// bypassed by prompt alone. (Disallow no longer catches this — the member is mapped.)
    /// </summary>
    internal static void RequireNotPreApproved(bool approved, string operationId)
    {
        if (approved)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' must not set approved; user approval is granted through " +
                "the panel and injected by the server.");
        }
    }

    internal static void ValidatePurgeArguments(PurgeTableEntriesRequest request, string operationId)
    {
        if (request.Entries is null || request.Entries.Count == 0)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' must list at least one table entry to purge.");
        }
        foreach (var entry in request.Entries)
        {
            if (entry.Id == Guid.Empty ||
                (entry.Table ?? string.Empty).Trim().ToLowerInvariant()
                    is not ("block" or "dimstyle" or "linetype" or "material"))
            {
                throw new InvalidOperationException(
                    $"Operation '{operationId}' has an invalid purge entry; table must be " +
                    "block|dimStyle|linetype|material with a non-empty id.");
            }
        }
    }

    internal static void ValidateMoveObjectsArguments(MoveObjectsToLayerRequest request, string operationId)
    {
        RequireNotPreApproved(request.Approved, operationId);
        if (request.TargetLayerId == Guid.Empty || request.Items is null || request.Items.Count == 0)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' has an invalid layer-move payload.");
        }
        var seen = new HashSet<Guid>();
        foreach (var item in request.Items)
        {
            if (item.ObjectId == Guid.Empty || string.IsNullOrWhiteSpace(item.ExpectedFingerprint))
            {
                throw new InvalidOperationException(
                    $"Operation '{operationId}' layer-move items need an objectId and expectedFingerprint.");
            }
            if (!seen.Add(item.ObjectId))
            {
                throw new InvalidOperationException(
                    $"Operation '{operationId}' lists Rhino object {item.ObjectId:D} more than once.");
            }
        }
    }

    internal static void ValidateFixEndpointPairArguments(FixEndpointPairRequest request, string operationId)
    {
        RequireNotPreApproved(request.Approved, operationId);
        if (request.AnchorObjectId == Guid.Empty || request.MoveObjectId == Guid.Empty ||
            request.AnchorObjectId == request.MoveObjectId ||
            string.IsNullOrWhiteSpace(request.ExpectedAnchorFingerprint) ||
            string.IsNullOrWhiteSpace(request.ExpectedFingerprint) ||
            request.AnchorEnd is not (0 or 1) || request.MoveEnd is not (0 or 1) ||
            double.IsNaN(request.Tolerance) || double.IsInfinity(request.Tolerance) || request.Tolerance < 0)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' has an invalid Rhino endpoint-fix payload.");
        }
    }

    internal static void ValidateTransformArguments(
        TransformRhinoObjectRequest request,
        string operationId)
    {
        RequireNotPreApproved(request.Approved, operationId);
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

    internal static void ValidateUpsertArguments(UpsertRhinoObjectRequest request, string operationId)
    {
        RequireNotPreApproved(request.Approved, operationId);
        if (request.SourceDocKey is not null)
        {
            // Provenance is server-injected at execution; a model-authored payload carrying it
            // would let bake attribution be spoofed.
            throw new InvalidOperationException(
                $"Operation '{operationId}' must not set sourceDocKey; provenance is stamped by the server.");
        }
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

            case "rhino.fixEndpointPair":
                // The move object is the single declared write; the untouched anchor must still be
                // declared as a read so its fingerprint expectation guards the pair.
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "moveObjectId", operation.OperationId),
                    write: true,
                    ResourceKind.RhinoObject);
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "anchorObjectId", operation.OperationId),
                    write: false,
                    ResourceKind.RhinoObject);
                return;

            case "rhino.moveObjectsToLayer":
                // One operation, N object writes: every moved object must be declared, and every
                // declared write must be moved — the same exactness single-target ops get.
                RequireExactDeclaredGuidTargets(
                    operation,
                    ReadItemGuids(arguments, "items", "objectId", operation.OperationId).ToHashSet(),
                    write: true,
                    ResourceKind.RhinoObject);
                return;

            case "rhino.updateLayer":
            case "rhino.deleteLayer":
                RequireExactDeclaredGuidTarget(
                    operation,
                    RequireArgumentGuid(arguments, "layerId", operation.OperationId),
                    write: true,
                    ResourceKind.RhinoLayer);
                return;

            case "rhino.purgeTableEntries":
                // One declared write per purged entry, in that entry's own table domain — a purge
                // is exactly as declared as any other destructive write.
                RequireExactDeclaredTableTargets(operation, arguments);
                return;

            case "rhino.layerState":
                // Save/restore/delete all touch the layer table as a whole; restore rewrites every
                // layer, so the table resource is the honest (and CAS-able) declaration.
                if (!operation.Writes.Any(resource => resource.Kind == ResourceKind.RhinoLayerTable))
                {
                    throw new InvalidOperationException(
                        $"Operation '{operation.OperationId}' must declare a rhinoLayerTable write " +
                        "(a layer state save/restore/delete acts on the whole table).");
                }
                return;

            case "rhino.ensureLayer":
                // Creating or updating a layer by path: the layer is the write. A brand-new layer
                // has no id yet, so the declaration is an absent-expectation on its path-derived
                // id — the adapter returns the real id after creating it.
                return;
        }
    }

    /// <summary>
    /// Per-object after-fingerprints from a batch mutation result (keyed by the resource id form
    /// the writeSet uses), or null when the response is not a batch.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? ReadBatchItemFingerprints(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object ||
            !result.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object &&
                item.TryGetProperty("objectId", out var objectId) &&
                objectId.ValueKind == JsonValueKind.String &&
                Guid.TryParse(objectId.GetString(), out var id) &&
                item.TryGetProperty("afterFingerprint", out var fingerprint) &&
                fingerprint.ValueKind == JsonValueKind.String)
            {
                map[id.ToString("D")] = fingerprint.GetString()!;
            }
        }
        return map.Count > 0 ? map : null;
    }

    /// <summary>
    /// Every purge entry must be declared as a write in its own table domain, and every declared
    /// table write must be purged — the exactness single-object ops get, applied per entry.
    /// </summary>
    private static void RequireExactDeclaredTableTargets(TypedOperation operation, JsonElement arguments)
    {
        var declared = operation.Writes
            .Where(resource => resource.Kind is ResourceKind.RhinoBlockDefinition
                or ResourceKind.RhinoDimensionStyle or ResourceKind.RhinoMaterial or ResourceKind.RhinoLinetype)
            .Select(resource => (resource.Kind, Id: resource.Id))
            .ToHashSet();
        var payload = new HashSet<(ResourceKind Kind, string Id)>();
        foreach (var entry in arguments.GetProperty("entries").EnumerateArray())
        {
            var table = entry.TryGetProperty("table", out var tableValue) ? tableValue.GetString() : null;
            var id = entry.TryGetProperty("id", out var idValue) && Guid.TryParse(idValue.GetString(), out var parsed)
                ? parsed
                : Guid.Empty;
            var kind = (table ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "block" => ResourceKind.RhinoBlockDefinition,
                "dimstyle" => ResourceKind.RhinoDimensionStyle,
                "linetype" => ResourceKind.RhinoLinetype,
                "material" => ResourceKind.RhinoMaterial,
                _ => (ResourceKind?)null,
            };
            if (kind is null || id == Guid.Empty)
            {
                throw new InvalidOperationException(
                    $"Operation '{operation.OperationId}' has an invalid purge entry.");
            }
            payload.Add((kind.Value, id.ToString("D")));
        }
        if (!declared.SetEquals(payload))
        {
            throw new InvalidOperationException(
                $"Operation '{operation.OperationId}' must declare exactly one write per purged entry " +
                "(kinds rhinoBlockDefinition|rhinoDimensionStyle|rhinoLinetype|rhinoMaterial).");
        }
    }

    private static IReadOnlyList<Guid> ReadItemGuids(
        JsonElement arguments,
        string arrayProperty,
        string idProperty,
        string operationId)
    {
        if (!arguments.TryGetProperty(arrayProperty, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"Operation '{operationId}' argument '{arrayProperty}' must be an array.");
        }
        var ids = new List<Guid>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty(idProperty, out var idValue) ||
                idValue.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(idValue.GetString(), out var id) ||
                id == Guid.Empty)
            {
                throw new InvalidOperationException(
                    $"Operation '{operationId}' has an item without a valid '{idProperty}'.");
            }
            ids.Add(id);
        }
        return ids;
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
        "canvas.referenceRhinoObjects" => ["objectId"],
        "canvas.delete" => ["objectId"],
        "canvas.setNumberSlider" => ["objectId"],
        "canvas.setGroup" => ["groupId"],
        "python.setSource" or "python.setSchema" or "python.execute" or
            "python.runtimeMessages" or "python.inspect" => ["componentId"],
        "python.setTyping" => ["componentId", "inputParameterId"],
        "canvas.inspect" or "rhino.inspect" or "rhino.createPrimitive" or
            "rhino.transform" or "rhino.upsert" or "rhino.delete" => ["objectId"],
        "rhino.fixEndpointPair" => ["anchorObjectId", "moveObjectId"],
        "rhino.moveObjectsToLayer" => ["targetLayerId"],
        "rhino.updateLayer" or "rhino.deleteLayer" or "rhino.ensureLayer" => ["layerId"],
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

    /// <summary>
    /// Auto-rebases SELF-ATTRIBUTABLE stale concrete fingerprints to live. Value/geometry writes
    /// (setNumberSlider, move, delete, rhino transform/upsert) carry a concrete fingerprint that
    /// gptino:auto cannot fill, so a session that already advanced a resource's fingerprint with its
    /// OWN prior commit then submits a stale base and Blocks — the dominant conflict in the field.
    /// Using the exact same safety test as <see cref="ResolveAutoExpectations"/> (the current live
    /// fingerprint equals what THIS session last wrote, per the ledger — no foreign write, no manual
    /// drift), we rebase both the writeSet expectation AND the operation payload fingerprint to live.
    /// A foreign/drifted resource is left untouched, so <see cref="ConflictDetector"/> still Blocks a
    /// genuine conflict. Returns the (possibly) rewritten change set and operations plus the rebased
    /// resource keys for logging.
    /// </summary>
    internal static (ChangeSet ChangeSet, IReadOnlyList<PreparedOperation> Operations, IReadOnlyList<(ResourceAddress Resource, string StaleFingerprint, string LiveFingerprint)> Rebased)
        ResolveSelfStaleConcreteRebase(
            ChangeSet changeSet,
            IReadOnlyList<PreparedOperation> operations,
            StateSnapshot liveState,
            Guid sessionId,
            IReadOnlyDictionary<string, ResourceLedgerEntry> resourceLedger)
    {
        var rebased = new List<(ResourceAddress Resource, string StaleFingerprint, string LiveFingerprint)>();
        var staleToLive = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var expectation in changeSet.WriteSet)
        {
            if (expectation.IsAuto || expectation.ExpectsAbsence)
            {
                continue;
            }
            var live = liveState.Resources.FirstOrDefault(resource =>
                ExactDomainOverlaps(resource.Resource, expectation.Resource));
            if (live is null ||
                string.IsNullOrWhiteSpace(live.Fingerprint) ||
                string.Equals(expectation.ExpectedFingerprint, live.Fingerprint, StringComparison.Ordinal))
            {
                continue; // absent, unmanaged, or not stale — nothing to rebase here.
            }
            var key = $"{expectation.Resource.Kind}:{expectation.Resource.Id}:{expectation.Resource.Field}";
            // Rebase ONLY when the live state is this session's own last write (no foreign write, no
            // manual drift) — identical to the gptino:auto self-sequential test.
            if (!resourceLedger.TryGetValue(key, out var ledger) ||
                ledger.SessionId != sessionId ||
                !string.Equals(ledger.Fingerprint, live.Fingerprint, StringComparison.Ordinal))
            {
                continue;
            }
            rebased.Add((expectation.Resource, expectation.ExpectedFingerprint, live.Fingerprint));
            staleToLive[expectation.ExpectedFingerprint] = live.Fingerprint;
        }
        if (rebased.Count == 0)
        {
            return (changeSet, operations, Array.Empty<(ResourceAddress, string, string)>());
        }
        var rebasedResources = rebased.Select(item => item.Resource).ToArray();
        var newWriteSet = changeSet.WriteSet
            .Select(expectation => rebasedResources.Any(resource => ExactDomainOverlaps(resource, expectation.Resource)) &&
                staleToLive.TryGetValue(expectation.ExpectedFingerprint, out var live)
                    ? expectation with { ExpectedFingerprint = live }
                    : expectation)
            .ToArray();
        var newOperations = operations
            .Select(operation =>
            {
                if (!operation.Operation.Writes.Any(write =>
                        rebasedResources.Any(resource => ExactDomainOverlaps(write, resource))))
                {
                    return operation;
                }
                var rewritten = RewritePayloadFingerprints(operation.Arguments, staleToLive);
                return rewritten is { } arguments ? operation with { Arguments = arguments } : operation;
            })
            .ToArray();
        return (changeSet with { WriteSet = newWriteSet }, newOperations, rebased);
    }

    /// <summary>
    /// Rewrites the concrete fingerprints a value/geometry payload carries: the scalar
    /// <c>expectedFingerprint</c> and any values in the <c>expectedFingerprints</c> map (canvas.move)
    /// whose value is a rebased stale fingerprint are replaced with the live one. Only
    /// <see cref="PreparedOperation.Arguments"/> is rewritten — the frozen idempotency payload is
    /// never touched. Returns null when nothing changed.
    /// </summary>
    private static JsonElement? RewritePayloadFingerprints(
        JsonElement arguments,
        IReadOnlyDictionary<string, string> staleToLive)
    {
        if (JsonNode.Parse(arguments.GetRawText()) is not JsonObject node)
        {
            return null;
        }
        var changed = false;
        if (node["expectedFingerprint"] is JsonValue scalar &&
            scalar.TryGetValue<string>(out var scalarValue) &&
            staleToLive.TryGetValue(scalarValue, out var scalarLive))
        {
            node["expectedFingerprint"] = scalarLive;
            changed = true;
        }
        if (node["expectedFingerprints"] is JsonObject map)
        {
            foreach (var entryKey in map.Select(pair => pair.Key).ToArray())
            {
                if (map[entryKey] is JsonValue value &&
                    value.TryGetValue<string>(out var mapValue) &&
                    staleToLive.TryGetValue(mapValue, out var mapLive))
                {
                    map[entryKey] = mapLive;
                    changed = true;
                }
            }
        }
        if (!changed)
        {
            return null;
        }
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
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

    internal readonly record struct PredicateOutcome(
        string Name, PredicateKind Kind, ResourceAddress? Resource, string? ExpectedValue, bool Passed);

    private static IReadOnlyList<string> Verify(
        ChangeSet changeSet,
        SnapshotEnvelope snapshot,
        IReadOnlyList<JobDiagnostic> diagnostics,
        IReadOnlyList<ResourceObservation> operationObservations,
        IReadOnlyList<JobComponentOutputs>? componentOutputs,
        ICollection<PredicateOutcome>? outcomes = null)
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
                PredicateKind.OutputCountInRange =>
                    predicate.Resource is not null &&
                    Guid.TryParse(predicate.Resource.Id, out var countComponentId) &&
                    TryParseOutputCountRange(predicate.ExpectedValue, out var countRange) &&
                    EvaluateOutputCountInRange(componentOutputs, countComponentId, countRange),
                PredicateKind.AreaInRange =>
                    predicate.Resource is not null &&
                    Guid.TryParse(predicate.Resource.Id, out var areaComponentId) &&
                    TryParseNumericOutputRange(predicate.ExpectedValue, out var areaName, out var areaMin, out var areaMax) &&
                    EvaluateNumericOutputInRange(componentOutputs, areaComponentId, areaName, "area", areaMin, areaMax),
                PredicateKind.DataTreeBranchCountInRange =>
                    predicate.Resource is not null &&
                    Guid.TryParse(predicate.Resource.Id, out var branchComponentId) &&
                    TryParseNumericOutputRange(predicate.ExpectedValue, out var branchName, out var branchMin, out var branchMax) &&
                    EvaluateNumericOutputInRange(componentOutputs, branchComponentId, branchName, "branchCount", branchMin, branchMax),
                PredicateKind.GeometryClosed =>
                    predicate.Resource is not null &&
                    Guid.TryParse(predicate.Resource.Id, out var closedComponentId) &&
                    !string.IsNullOrWhiteSpace(predicate.ExpectedValue) &&
                    EvaluateGeometryClosed(componentOutputs, closedComponentId, predicate.ExpectedValue!.Trim()),
                PredicateKind.VolumeInRange =>
                    predicate.Resource is not null &&
                    Guid.TryParse(predicate.Resource.Id, out var volumeComponentId) &&
                    TryParseNumericOutputRange(predicate.ExpectedValue, out var volumeName, out var volumeMin, out var volumeMax) &&
                    EvaluateNumericOutputInRange(componentOutputs, volumeComponentId, volumeName, "volume", volumeMin, volumeMax),
                PredicateKind.BoundingBoxInRange =>
                    predicate.Resource is not null &&
                    Guid.TryParse(predicate.Resource.Id, out var bboxComponentId) &&
                    TryParseBoundingBoxRange(predicate.ExpectedValue, out var bboxName, out var bboxAxis, out var bboxMin, out var bboxMax) &&
                    EvaluateBoundingBoxInRange(componentOutputs, bboxComponentId, bboxName, bboxAxis, bboxMin, bboxMax),
                _ => false
            };
            outcomes?.Add(new PredicateOutcome(
                predicate.Name, predicate.Kind, predicate.Resource, predicate.ExpectedValue, passed));
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

    /// <summary>
    /// Informational commit-quality summary: runtime warning count plus the names of solved-empty
    /// outputs (from the post-solve output inspections), e.g. "1 runtime warning(s); output(s)
    /// 'Ceiling' empty." Null when there is nothing to report. Strictly informational — the caller
    /// appends it to the commit message and MUST NOT let it change the job state (an intentionally
    /// empty output is legal; this only makes the canvas-visible state survive in records).
    /// </summary>
    internal readonly record struct OutputCountRange(string OutputName, int Min, int Max);

    /// <summary>Parses an OutputCountInRange ExpectedValue of the form "outputName:min:max" (max may be "*").</summary>
    internal static bool TryParseOutputCountRange(string? expectedValue, out OutputCountRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(expectedValue))
        {
            return false;
        }
        var parts = expectedValue.Split(':');
        if (parts.Length != 3)
        {
            return false;
        }
        var name = parts[0].Trim();
        if (name.Length == 0 ||
            !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var min) ||
            min < 0)
        {
            return false;
        }
        int max;
        if (string.Equals(parts[2].Trim(), "*", StringComparison.Ordinal))
        {
            max = int.MaxValue;
        }
        else if (!int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out max))
        {
            return false;
        }
        if (max < min)
        {
            return false;
        }
        range = new OutputCountRange(name, min, max);
        return true;
    }

    /// <summary>
    /// True when the named output of the component solved to an item count within [Min,Max]. Reads
    /// the post-solve inspection GPTino already collects (name + dataCount per output). Fails closed:
    /// a missing component, missing output, or absent inspection returns false so an unverifiable
    /// claim never passes.
    /// </summary>
    internal static bool EvaluateOutputCountInRange(
        IReadOnlyList<JobComponentOutputs>? componentOutputs,
        Guid componentId,
        OutputCountRange range)
    {
        var component = componentOutputs?.FirstOrDefault(item => item.ComponentId == componentId);
        if (component is null ||
            component.Inspection.ValueKind != JsonValueKind.Object ||
            !component.Inspection.TryGetProperty("outputs", out var inspected) ||
            inspected.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        foreach (var output in inspected.EnumerateArray())
        {
            if (output.ValueKind == JsonValueKind.Object &&
                output.TryGetProperty("name", out var nameElement) &&
                nameElement.ValueKind == JsonValueKind.String &&
                string.Equals(nameElement.GetString(), range.OutputName, StringComparison.Ordinal) &&
                output.TryGetProperty("dataCount", out var countElement) &&
                countElement.ValueKind == JsonValueKind.Number)
            {
                var count = countElement.GetInt32();
                return count >= range.Min && count <= range.Max;
            }
        }
        return false;
    }

    /// <summary>Parses "outputName:min:max" (max may be "*") into a name and a double range.</summary>
    internal static bool TryParseNumericOutputRange(string? expectedValue, out string outputName, out double min, out double max)
    {
        outputName = string.Empty;
        min = 0;
        max = 0;
        if (string.IsNullOrWhiteSpace(expectedValue))
        {
            return false;
        }
        var parts = expectedValue.Split(':');
        if (parts.Length != 3)
        {
            return false;
        }
        outputName = parts[0].Trim();
        if (outputName.Length == 0 ||
            !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out min) ||
            min < 0)
        {
            return false;
        }
        max = string.Equals(parts[2].Trim(), "*", StringComparison.Ordinal)
            ? double.PositiveInfinity
            : double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedMax)
                ? parsedMax
                : double.NaN;
        return !double.IsNaN(max) && max >= min;
    }

    /// <summary>
    /// True when the named output's numeric <paramref name="field"/> ("area", "branchCount", ...) is
    /// within [min,max]. Reads the post-solve inspection; fails closed on any missing data.
    /// </summary>
    internal static bool EvaluateNumericOutputInRange(
        IReadOnlyList<JobComponentOutputs>? componentOutputs,
        Guid componentId,
        string outputName,
        string field,
        double min,
        double max)
    {
        var output = FindInspectedOutput(componentOutputs, componentId, outputName);
        if (output is not { } value ||
            !value.TryGetProperty(field, out var fieldElement) ||
            fieldElement.ValueKind != JsonValueKind.Number)
        {
            return false;
        }
        var measured = fieldElement.GetDouble();
        return measured >= min && measured <= max;
    }

    /// <summary>True when every geometry in the named output is closed (inspection "closed" == true).</summary>
    internal static bool EvaluateGeometryClosed(
        IReadOnlyList<JobComponentOutputs>? componentOutputs,
        Guid componentId,
        string outputName)
    {
        var output = FindInspectedOutput(componentOutputs, componentId, outputName);
        return output is { } value &&
            value.TryGetProperty("closed", out var closedElement) &&
            closedElement.ValueKind == JsonValueKind.True;
    }

    /// <summary>Parses "outputName:axis:min:max" (axis = x|y|z|diagonal, max may be "*").</summary>
    internal static bool TryParseBoundingBoxRange(string? expectedValue, out string outputName, out string axis, out double min, out double max)
    {
        outputName = string.Empty;
        axis = string.Empty;
        min = 0;
        max = 0;
        if (string.IsNullOrWhiteSpace(expectedValue))
        {
            return false;
        }
        var parts = expectedValue.Split(':');
        if (parts.Length != 4)
        {
            return false;
        }
        outputName = parts[0].Trim();
        axis = parts[1].Trim().ToLowerInvariant();
        if (outputName.Length == 0 ||
            axis is not ("x" or "y" or "z" or "diagonal") ||
            !double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out min) ||
            min < 0)
        {
            return false;
        }
        max = string.Equals(parts[3].Trim(), "*", StringComparison.Ordinal)
            ? double.PositiveInfinity
            : double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedMax)
                ? parsedMax
                : double.NaN;
        return !double.IsNaN(max) && max >= min;
    }

    /// <summary>True when the named output's bounding-box extent on the axis (or diagonal) is within [min,max].</summary>
    internal static bool EvaluateBoundingBoxInRange(
        IReadOnlyList<JobComponentOutputs>? componentOutputs,
        Guid componentId,
        string outputName,
        string axis,
        double min,
        double max)
    {
        if (FindInspectedOutput(componentOutputs, componentId, outputName) is not { } output ||
            !output.TryGetProperty("geometryBounds", out var boundsElement) ||
            boundsElement.ValueKind != JsonValueKind.Object ||
            !boundsElement.TryGetProperty("size", out var size) ||
            size.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        double extent;
        if (string.Equals(axis, "diagonal", StringComparison.Ordinal))
        {
            if (!size.TryGetProperty("x", out var sx) || sx.ValueKind != JsonValueKind.Number ||
                !size.TryGetProperty("y", out var sy) || sy.ValueKind != JsonValueKind.Number ||
                !size.TryGetProperty("z", out var sz) || sz.ValueKind != JsonValueKind.Number)
            {
                return false;
            }
            double x = sx.GetDouble(), y = sy.GetDouble(), z = sz.GetDouble();
            extent = Math.Sqrt((x * x) + (y * y) + (z * z));
        }
        else if (size.TryGetProperty(axis, out var axisElement) && axisElement.ValueKind == JsonValueKind.Number)
        {
            extent = axisElement.GetDouble();
        }
        else
        {
            return false;
        }
        return extent >= min && extent <= max;
    }

    private static JsonElement? FindInspectedOutput(
        IReadOnlyList<JobComponentOutputs>? componentOutputs,
        Guid componentId,
        string outputName)
    {
        var component = componentOutputs?.FirstOrDefault(item => item.ComponentId == componentId);
        if (component is null ||
            component.Inspection.ValueKind != JsonValueKind.Object ||
            !component.Inspection.TryGetProperty("outputs", out var inspected) ||
            inspected.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var output in inspected.EnumerateArray())
        {
            if (output.ValueKind == JsonValueKind.Object &&
                output.TryGetProperty("name", out var nameElement) &&
                nameElement.ValueKind == JsonValueKind.String &&
                string.Equals(nameElement.GetString(), outputName, StringComparison.Ordinal))
            {
                return output;
            }
        }
        return null;
    }

    internal static string? DescribeCommitQuality(
        IReadOnlyList<JobDiagnostic> diagnostics,
        IReadOnlyList<JobComponentOutputs>? outputs)
    {
        try
        {
            var warningCount = diagnostics.Count(item =>
                item.Severity == BridgeDiagnosticSeverity.Warning);
            var emptyOutputs = new List<string>();
            foreach (var component in outputs ?? Array.Empty<JobComponentOutputs>())
            {
                if (component.Inspection.ValueKind != JsonValueKind.Object ||
                    !component.Inspection.TryGetProperty("outputs", out var inspected) ||
                    inspected.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                foreach (var output in inspected.EnumerateArray())
                {
                    if (output.ValueKind != JsonValueKind.Object ||
                        !output.TryGetProperty("name", out var nameElement) ||
                        nameElement.ValueKind != JsonValueKind.String ||
                        !output.TryGetProperty("dataCount", out var countElement) ||
                        countElement.ValueKind != JsonValueKind.Number ||
                        countElement.GetInt32() != 0)
                    {
                        continue;
                    }
                    var name = nameElement.GetString();
                    // The script console output 'out' is routinely empty; reporting it would be
                    // noise, not signal.
                    if (string.IsNullOrWhiteSpace(name) ||
                        string.Equals(name, "out", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    emptyOutputs.Add(name);
                }
            }
            var parts = new List<string>();
            if (warningCount > 0)
            {
                parts.Add($"{warningCount} runtime warning(s)");
            }
            if (emptyOutputs.Count > 0)
            {
                var names = string.Join(
                    ", ",
                    emptyOutputs.Distinct(StringComparer.Ordinal).Select(name => $"'{name}'"));
                parts.Add($"output(s) {names} empty");
            }
            return parts.Count == 0 ? null : $"{string.Join("; ", parts)}.";
        }
        catch (Exception)
        {
            // Never-demote discipline: a quality-summary bug must never affect the commit.
            return null;
        }
    }

    /// <summary>
    /// RecoveryRequired manifest: which operations completed their bridge round trip, which one
    /// was in flight when the failure surfaced (outcome honestly unknown — never reported as
    /// failed), and which never dispatched. Returned as message text plus the same facts as
    /// Information diagnostics so the job projection and problem log both carry them.
    /// </summary>
    internal static (string Message, IReadOnlyList<JobDiagnostic> Diagnostics) BuildRecoveryManifest(
        IReadOnlyList<TypedOperation> operations,
        IReadOnlyList<string> completedOperationIds,
        string? inFlightOperationId)
    {
        var completed = new HashSet<string>(completedOperationIds, StringComparer.Ordinal);
        var notDispatched = operations
            .Select(operation => operation.OperationId)
            .Where(id => !completed.Contains(id) &&
                !string.Equals(id, inFlightOperationId, StringComparison.Ordinal))
            .ToArray();
        var applied = completedOperationIds.Count > 0
            ? string.Join(", ", completedOperationIds)
            : "none";
        var unknown = inFlightOperationId is null
            ? "none"
            : $"{inFlightOperationId} (in flight at failure)";
        var pending = notDispatched.Length > 0 ? string.Join(", ", notDispatched) : "none";
        var message = $"Applied: {applied}. Unknown outcome: {unknown}. Not dispatched: {pending}.";
        var manifestDiagnostics = new List<JobDiagnostic>
        {
            new(string.Empty, BridgeDiagnosticSeverity.Information, "recovery_applied", applied),
            new(
                inFlightOperationId ?? string.Empty,
                BridgeDiagnosticSeverity.Information,
                "recovery_unknown",
                unknown),
            new(string.Empty, BridgeDiagnosticSeverity.Information, "recovery_not_dispatched", pending),
        };
        return (message, manifestDiagnostics);
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
                    BridgeAdapterOwner.CordycepsCanvas,
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
                OperationKind.ReferenceRhinoObjects or
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
            case PredicateKind.OutputCountInRange:
                if (predicate.Resource?.Kind != ResourceKind.GrasshopperComponent ||
                    !TryParseOutputCountRange(predicate.ExpectedValue, out _))
                {
                    throw new InvalidOperationException(
                        "OutputCountInRange requires a grasshopperComponent resource and expectedValue " +
                        "\"outputName:min:max\" (max may be \"*\").");
                }
                return;
            case PredicateKind.AreaInRange:
            case PredicateKind.DataTreeBranchCountInRange:
            case PredicateKind.VolumeInRange:
                if (predicate.Resource?.Kind != ResourceKind.GrasshopperComponent ||
                    !TryParseNumericOutputRange(predicate.ExpectedValue, out _, out _, out _))
                {
                    throw new InvalidOperationException(
                        $"{predicate.Kind} requires a grasshopperComponent resource and expectedValue " +
                        "\"outputName:min:max\" (max may be \"*\").");
                }
                return;
            case PredicateKind.BoundingBoxInRange:
                if (predicate.Resource?.Kind != ResourceKind.GrasshopperComponent ||
                    !TryParseBoundingBoxRange(predicate.ExpectedValue, out _, out _, out _, out _))
                {
                    throw new InvalidOperationException(
                        "BoundingBoxInRange requires a grasshopperComponent resource and expectedValue " +
                        "\"outputName:axis:min:max\" (axis = x|y|z|diagonal, max may be \"*\").");
                }
                return;
            case PredicateKind.GeometryClosed:
                if (predicate.Resource?.Kind != ResourceKind.GrasshopperComponent ||
                    string.IsNullOrWhiteSpace(predicate.ExpectedValue))
                {
                    throw new InvalidOperationException(
                        "GeometryClosed requires a grasshopperComponent resource and expectedValue = the output name.");
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
        Guid? grasshopperDocumentId,
        Guid projectId)
    {
        foreach (var expectation in changeSet.ReadSet.Concat(changeSet.WriteSet))
        {
            ValidateResourceAddress(expectation.Resource, grasshopperDocumentId, projectId);
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
                ValidateResourceAddress(resource, grasshopperDocumentId, projectId);
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
                ValidateResourceAddress(resource, grasshopperDocumentId, projectId);
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
            ValidateResourceAddress(predicate.Resource!, grasshopperDocumentId, projectId);
            if (!prepared.SelectMany(item => item.Operation.Reads.Concat(item.Operation.Writes))
                    .Any(resource => ExactDomainOverlaps(resource, predicate.Resource!)))
            {
                throw new InvalidOperationException(
                    $"Acceptance predicate '{predicate.Name}' targets a resource not declared by any operation.");
            }
        }
        foreach (var beforeImage in changeSet.RollbackBeforeImages)
        {
            ValidateResourceAddress(beforeImage.Resource, grasshopperDocumentId, projectId);
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
                     OperationKind.CreateComponent or OperationKind.ReferenceRhinoObjects or
                     OperationKind.CreateRhinoPrimitive or
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

            case "rhino.moveObjectsToLayer":
                // Every moved object carries its own fingerprint, and each must match that
                // object's writeSet expectation — a batch does not get to be vaguer than N
                // single-object writes.
                foreach (var item in arguments.GetProperty("items").EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object ||
                        !item.TryGetProperty("objectId", out var itemId) ||
                        !Guid.TryParse(itemId.GetString(), out var movedId) ||
                        !item.TryGetProperty("expectedFingerprint", out var itemFingerprint) ||
                        itemFingerprint.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidOperationException(
                            $"Operation '{operation.OperationId}' has an invalid layer-move item.");
                    }
                    RequirePayloadFingerprint(
                        changeSet,
                        operation,
                        new ResourceAddress(ResourceKind.RhinoObject, movedId.ToString("D")),
                        itemFingerprint.GetString()!);
                }
                return;

            case "rhino.updateLayer":
            case "rhino.deleteLayer":
                RequirePayloadFingerprint(
                    changeSet,
                    operation,
                    new ResourceAddress(
                        ResourceKind.RhinoLayer,
                        RequireArgumentGuid(arguments, "layerId", operation.OperationId).ToString("D")),
                    RequireArgumentString(arguments, "expectedFingerprint", operation.OperationId));
                return;

            case "rhino.fixEndpointPair":
                // The write expectation must pin the MOVE object at its audited fingerprint; the
                // adapter re-verifies both fingerprints at execution.
                RequirePayloadFingerprint(
                    changeSet,
                    operation,
                    new ResourceAddress(
                        ResourceKind.RhinoObject,
                        RequireArgumentGuid(arguments, "moveObjectId", operation.OperationId).ToString("D")),
                    RequireArgumentString(arguments, "expectedFingerprint", operation.OperationId));
                // The ANCHOR must have a declared readSet expectation, and a concrete declared
                // fingerprint must match the payload's — the same submit-time teaching the move
                // side gets, instead of a late execution failure. A gptino:auto declaration is
                // allowed (the server resolves it; the adapter still verifies the concrete value).
                var anchorResource = new ResourceAddress(
                    ResourceKind.RhinoObject,
                    RequireArgumentGuid(arguments, "anchorObjectId", operation.OperationId).ToString("D"));
                var anchorExpectation = FindExpectation(changeSet.ReadSet, anchorResource)
                    ?? throw new InvalidOperationException(
                        $"Operation '{operation.OperationId}' requires a readSet expectation for its " +
                        "endpoint-fix anchor (the audited anchor fingerprint).");
                var anchorPayload = RequireArgumentString(
                    arguments, "expectedAnchorFingerprint", operation.OperationId);
                // (fall through to the shared anchor check below)
                if (!string.Equals(
                        anchorExpectation.ExpectedFingerprint,
                        ResourceExpectation.AutoFingerprint,
                        StringComparison.Ordinal) &&
                    !string.Equals(anchorExpectation.ExpectedFingerprint, anchorPayload, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Operation '{operation.OperationId}' anchor fingerprint does not match its " +
                        "declared readSet expectation; use the audited fingerprint in both.");
                }
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

    private static void ValidateResourceAddress(
        ResourceAddress resource,
        Guid? grasshopperDocumentId,
        Guid projectId)
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
            if (grasshopperDocumentId is not { } documentId)
            {
                throw new InvalidOperationException(
                    "No Grasshopper document is open, so there is no document resource to address. " +
                    "Open a Grasshopper definition to work on the canvas.");
            }

            if (!string.Equals(resource.Id, documentId.ToString("D"), StringComparison.Ordinal))
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

        if (resource.Kind == ResourceKind.RhinoLayerTable)
        {
            // The Rhino layer table is addressed by the RHINO-scoped ProjectId. It used to borrow
            // the bound Grasshopper document id, which was wrong twice over: a Rhino layer table has
            // nothing to do with Grasshopper, and sibling .gh documents open on one Rhino file would
            // each name the same physical table by a different id — so two layer writes could not
            // see each other's CAS domain. Curator work also has no .gh to borrow an id from.
            if (!string.Equals(resource.Id, projectId.ToString("D"), StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "rhinoLayerTable resource ids must be the project id (the Rhino document's " +
                    "identity) in D format.");
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
            case OperationKind.ReferenceRhinoObjects:
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
            case OperationKind.EnsureRhinoLayer:
                // ensureLayer is create-or-update by path: a brand-new layer declares its intended
                // id with the absent sentinel, an existing one declares its concrete fingerprint.
                resource = new ResourceAddress(
                    ResourceKind.RhinoLayer,
                    RequireArgumentGuid(arguments, "layerId", prepared.Operation.OperationId).ToString("D"));
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

    internal sealed record JobDiagnostic(
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
    internal sealed record JobComponentOutputs(Guid ComponentId, JsonElement Inspection);

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

    // internal (not private) so the pure CanvasAutoPlacement.ResolveAutoPivots wrapper in this same
    // assembly can accept the prepared list and return a rewritten one without a broader refactor.
    internal sealed record PreparedOperation(
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

        /// <summary>
        /// Resolved user-approval items (objectId -> audited fingerprint) from the ChangeSet's
        /// approvalGrantId; null when no grant was supplied. In-memory only: interrupted jobs
        /// never execute after a restart, so grants need no durability.
        /// </summary>
        public IReadOnlyDictionary<Guid, string>? ApprovalItems { get; init; }

        /// <summary>Source grant id, so a committed job can consume its covered objects.</summary>
        public string? ApprovalGrantId { get; init; }
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
