using GPTino.AgentHost.Codex;
using GPTino.AgentHost.Data;
using GPTino.AgentHost.Hosting;
using GPTino.Contracts;
using GPTino.Core;

namespace GPTino.AgentHost.Runtime;

public sealed class RuntimeStateProjector
{
    private readonly SessionStore _store;
    private readonly AgentHostOptions _options;
    private readonly RuntimeIdentity _identity;
    private readonly RuntimeControl _control;
    private readonly ILiveDocumentBackend _backend;
    private readonly EffectiveModelState _effectiveModels;
    private readonly EventHub _events;
    private readonly TerminalLauncher? _terminals;
    private readonly ProjectContextStore? _contextStore;
    private readonly SessionActivityLog? _activity;
    private readonly CodexAuthProbe? _codexAuth;
    private readonly SessionUsageState? _usage;

    public RuntimeStateProjector(
        SessionStore store,
        AgentHostOptions options,
        RuntimeIdentity identity,
        RuntimeControl control,
        ILiveDocumentBackend backend,
        EffectiveModelState effectiveModels,
        EventHub events,
        TerminalLauncher? terminals = null,
        ProjectContextStore? contextStore = null,
        SessionActivityLog? activity = null,
        CodexAuthProbe? codexAuth = null,
        SessionUsageState? usage = null)
    {
        _store = store;
        _options = options;
        _identity = identity;
        _control = control;
        _backend = backend;
        _effectiveModels = effectiveModels;
        _events = events;
        _terminals = terminals;
        _contextStore = contextStore;
        _activity = activity;
        _codexAuth = codexAuth;
        _usage = usage;
    }

    public async Task<object> BuildAsync(CancellationToken cancellationToken = default)
    {
        var (sessions, orderVersion) = await _store.ReadStateAsync(cancellationToken).ConfigureAwait(false);
        var live = _backend as LiveDocumentBackend;
        var queueControl = _backend as ILiveDocumentQueueControl;
        var queueItems = queueControl?.ReadQueue() ?? Array.Empty<LiveQueueItem>();
        var queueBySession = queueItems
            .GroupBy(item => item.SessionId)
            .ToDictionary(group => group.Key, group => group.First());
        var projectedSessions = new List<object>(sessions.Count);
        foreach (var session in sessions)
        {
            var messages = await _store.ReadMessagesAsync(session.Id, limit: 250, cancellationToken: cancellationToken).ConfigureAwait(false);
            var hasEffectiveModel = _effectiveModels.TryGet(session.Id, out var effectiveModel);
            projectedSessions.Add(new
            {
                id = session.Id,
                title = session.Name,
                summary = session.CurrentTask,
                status = ProjectStatus(session.State),
                mode = ProjectMode(session.Role),
                modelProfile = ProjectModelProfile(session.ModelProfile),
                pinnedModel = session.Model,
                backend = "codex",
                effectiveModel = hasEffectiveModel ? effectiveModel.Model : null,
                reasoning = hasEffectiveModel ? effectiveModel.Reasoning : null,
                effectiveProfile = hasEffectiveModel ? effectiveModel.EffectiveProfile.ToString() : null,
                routingTaskClass = hasEffectiveModel ? effectiveModel.TaskClass.ToString() : null,
                routingReason = hasEffectiveModel ? effectiveModel.Rationale : null,
                routingError = hasEffectiveModel ? effectiveModel.Error : null,
                boundGrasshopperDocId = session.GrasshopperDoc,
                paused = session.State == Api.SessionStates.Paused,
                terminalOpen = _terminals?.IsOpen(session.Id) ?? false,
                unread = 0,
                currentActivity = session.State == Api.SessionStates.Running
                    ? _activity?.Latest(session.Id)?.Summary
                    : null,
                activity = _activity?.Read(session.Id).Select(entry => new
                {
                    at = entry.At,
                    kind = entry.Kind,
                    summary = entry.Summary,
                    ok = entry.Ok,
                    durationMs = entry.DurationMs
                }).ToArray() ?? [],
                messages,
                job = queueBySession.TryGetValue(session.Id, out var sessionJob)
                    ? new
                    {
                        id = sessionJob.JobId,
                        title = sessionJob.Summary,
                        phase = ProjectQueueState(sessionJob.State),
                        baseRevision = (long?)null
                    }
                    : null,
                usage = _usage is not null && _usage.TryGet(session.Id, out var usageSnapshot)
                    ? (object)new
                    {
                        totalTokens = usageSnapshot.TotalTokens,
                        contextWindow = usageSnapshot.ContextWindow,
                        contextUsedTokens = usageSnapshot.ContextUsedTokens,
                        rateLimits = usageSnapshot.RateLimits.Select(limit => new
                        {
                            label = limit.Label,
                            usedPercent = limit.UsedPercent,
                            resetsAt = limit.ResetsAt
                        }).ToArray()
                    }
                    : null
            });
        }

        var target = _backend.CurrentTarget;
        var rhinoPath = target?.RhinoPath ?? _options.RhinoPath;
        var grasshopperPath = target?.GrasshopperPath ?? _options.GrasshopperPath;
        var projectId = target?.ProjectId ?? _identity.ProjectId;
        var rhinoName = string.IsNullOrWhiteSpace(rhinoPath)
            ? "Untitled Rhino"
            : Path.GetFileNameWithoutExtension(rhinoPath);
        // Every registered Grasshopper document (durable docKey + path) in registration order.
        // Null (not an empty list) before the first registration so the panel keeps rendering its
        // legacy single grasshopperFile placeholder node.
        var registeredDocs = live?.RegisteredGrasshopperDocuments
            ?? Array.Empty<RegisteredGrasshopperDocument>();
        var projectedQueue = queueItems.Select(item => new
        {
            id = item.JobId,
            sessionId = item.SessionId,
            title = item.Summary,
            state = ProjectQueueState(item.State),
            resource = (string?)null,
            waitingFor = (string?)null,
            target = item.Target,
            targetDocId = item.TargetDoc
        }).ToArray();
        var sessionsByJob = queueItems.ToDictionary(item => item.JobId, item => item.SessionId);
        var projectedConflicts = (queueControl?.ReadConflicts() ?? Array.Empty<LiveConflictItem>())
            .Select((item, index) => new
            {
                id = $"{item.JobId:N}-{item.OtherJobId:N}-{index}",
                title = item.Kind.ToString(),
                detail = item.Message,
                sessionIds = new[]
                {
                    sessionsByJob.GetValueOrDefault(item.JobId),
                    sessionsByJob.GetValueOrDefault(item.OtherJobId)
                }.Where(id => id != Guid.Empty).Select(id => id.ToString("D")).Distinct().ToArray(),
                resource = item.Resource is null
                    ? null
                    : $"{item.Resource.Kind}:{item.Resource.Id}:{item.Resource.Field}",
                resolution = ResolutionForKind(item.Kind),
                observedAt = (DateTimeOffset?)null
            })
            .Concat((queueControl?.ReadRecentProblems() ?? Array.Empty<LiveProblemItem>())
                .Select(item => new
                {
                    id = $"problem-{item.JobId:N}",
                    title = $"{item.State}: {item.Summary}",
                    detail = item.Message ?? "This job requires review before another live write.",
                    sessionIds = new[] { item.SessionId.ToString("D") },
                    resource = item.Resource is null
                        ? null
                        : $"{item.Resource.Kind}:{item.Resource.Id}:{item.Resource.Field}",
                    resolution = ResolutionForProblem(item.State, item.ConflictKind),
                    observedAt = (DateTimeOffset?)item.UpdatedAt
                }))
            .ToArray();
        var writerQueueItem = queueItems.FirstOrDefault(item =>
            item.State is JobState.Executing or JobState.Verifying);
        var codexAuthSnapshot = _codexAuth?.Read();
        return new
        {
            projectId,
            projectName = rhinoName,
            rhinoFile = rhinoPath ?? "Untitled.3dm",
            grasshopperFile = grasshopperPath ?? "No Grasshopper definition",
            grasshopperDocs = registeredDocs.Count > 0
                ? registeredDocs.Select(doc => new { id = doc.Id, file = doc.File }).ToArray()
                : null,
            health = _backend.IsConnected ? "connected" : "disconnected",
            healthDetail = _backend.IsConnected
                ? "Explicit document bridge authenticated."
                : "Waiting for the explicit Rhino/Grasshopper bridge target.",
            revision = live?.CurrentRevision ?? 0,
            gitRevision = live?.CurrentGitCommit is null ? (long?)null : live.CurrentRevision,
            orderVersion,
            paused = _control.IsPaused,
            writer = _backend.WriterSessionId is { } writerSessionId
                ? new
                {
                    sessionId = writerSessionId,
                    jobId = writerQueueItem?.JobId.ToString("D") ?? "active",
                    label = "Applying verified document change",
                    phase = "executing",
                    startedAt = live?.WriterStartedAt ?? DateTimeOffset.UtcNow,
                    progress = (double?)null
                }
                : null,
            sessions = projectedSessions,
            queue = projectedQueue,
            conflicts = projectedConflicts,
            contextFolder = _contextStore?.ContextDirectory,
            codexAuth = codexAuthSnapshot is null
                ? null
                : new { status = codexAuthSnapshot.Wire, detail = codexAuthSnapshot.Detail },
            currentSelection = live?.CurrentSelection is { } selection
                ? new
                {
                    docId = live.CurrentSelectionDocId,
                    rhinoObjectCount = selection.RhinoObjectIds.Count,
                    rhinoObjectIds = selection.RhinoObjectIds
                        .Take(32)
                        .Select(id => id.ToString("D"))
                        .ToArray(),
                    activeLayer = selection.ActiveLayerName,
                    grasshopperObjectCount = selection.GrasshopperObjects?.Count ?? 0,
                    grasshopperObjects = (selection.GrasshopperObjects ?? [])
                        .Take(32)
                        .Select(item => new
                        {
                            id = item.ObjectId.ToString("D"),
                            nickName = string.IsNullOrWhiteSpace(item.NickName) ? item.Name : item.NickName
                        })
                        .ToArray(),
                    observedAt = selection.ObservedAt
                }
                : null,
            lastUpdatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Deterministic, human-facing remediation per conflict kind. Server-computed on purpose —
    /// resolution advice is never model self-report (same rule as the verification labels).
    /// </summary>
    private static string ResolutionForKind(ConflictKind kind) => kind switch
    {
        ConflictKind.WriteWrite =>
            "Both jobs write this resource. The single-writer queue serializes them; if the order matters, pause one session until the other commits.",
        ConflictKind.ReadWrite =>
            "One job reads what the other writes. The queue serializes them; if the reader ran first and looks stale, re-run it after the writer commits.",
        ConflictKind.Delete =>
            "A queued job deletes this resource while another still uses it. Let the delete run last, or cancel whichever job is now redundant.",
        ConflictKind.Exclusive =>
            "A document-global operation needs exclusive access, so these jobs run strictly one at a time. Cancel one if it no longer applies.",
        ConflictKind.TargetMismatch =>
            "The jobs target different documents. Check that both sessions are working on the intended Rhino/Grasshopper pair.",
        ConflictKind.Stale =>
            "The live resource changed after this job's snapshot. Ask the session to re-read the resource and resubmit with the current fingerprint.",
        ConflictKind.ManualDrift =>
            "This resource was edited by hand after the session last saw it. Human edits win: ask the session to re-read it, adopting your edit as the new baseline, then resubmit.",
        ConflictKind.Unmanaged =>
            "The resource is not under GPTino management yet. Ask the session to read it first so it becomes tracked, then resubmit.",
        _ => "Review the message above, then ask the session to re-read the affected resources and resubmit."
    };

    private static string ResolutionForProblem(JobState state, ConflictKind? kind) => kind is { } conflictKind
        ? ResolutionForKind(conflictKind)
        : state switch
        {
            JobState.Blocked =>
                "The message above names what the broker declined. Ask the session to re-read the affected resources and resubmit.",
            JobState.Failed =>
                "The change ran but did not pass verification, so nothing was committed. Review the message, then ask the session to retry with the failure addressed.",
            JobState.RecoveryRequired =>
                "The write was interrupted (crash or stop) before verification finished. Check the document state — undo if it looks wrong — then ask the session to re-run the change.",
            _ => "Review the message above and retry once the cause is addressed."
        };

    private static string ProjectStatus(string state) => state switch
    {
        Api.SessionStates.Running => "working",
        Api.SessionStates.Waiting => "queued",
        Api.SessionStates.Paused => "paused",
        Api.SessionStates.Failed => "blocked",
        _ => "idle"
    };

    private static string ProjectMode(string role) =>
        string.Equals(role, "planner", StringComparison.OrdinalIgnoreCase) ? "plan" : "auto";

    private static string ProjectModelProfile(string profile) => profile.ToLowerInvariant() switch
    {
        "auto" => "auto",
        "read-fast" or "fast-safe" => "fast",
        "high-assurance" or "recovery" => "deep",
        "standard" => "standard",
        _ => "auto"
    };

    private static string ProjectQueueState(JobState state) => state switch
    {
        JobState.Executing => "applying",
        JobState.Verifying => "verifying",
        JobState.Validating => "waiting",
        _ => "ready"
    };
}
