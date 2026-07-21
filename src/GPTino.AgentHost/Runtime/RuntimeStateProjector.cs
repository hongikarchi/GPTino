using GPTino.AgentHost.Codex;
using GPTino.AgentHost.Data;
using GPTino.AgentHost.Hosting;
using GPTino.Contracts;

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

    public RuntimeStateProjector(
        SessionStore store,
        AgentHostOptions options,
        RuntimeIdentity identity,
        RuntimeControl control,
        ILiveDocumentBackend backend,
        EffectiveModelState effectiveModels,
        EventHub events,
        TerminalLauncher? terminals = null,
        ProjectContextStore? contextStore = null)
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
                paused = session.State == Api.SessionStates.Paused,
                terminalOpen = _terminals?.IsOpen(session.Id) ?? false,
                unread = 0,
                messages,
                job = queueBySession.TryGetValue(session.Id, out var sessionJob)
                    ? new
                    {
                        id = sessionJob.JobId,
                        title = sessionJob.Summary,
                        phase = ProjectQueueState(sessionJob.State),
                        baseRevision = (long?)null
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
        var projectedQueue = queueItems.Select(item => new
        {
            id = item.JobId,
            sessionId = item.SessionId,
            title = item.Summary,
            state = ProjectQueueState(item.State),
            resource = (string?)null,
            waitingFor = (string?)null
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
                    : $"{item.Resource.Kind}:{item.Resource.Id}:{item.Resource.Field}"
            })
            .Concat((queueControl?.ReadRecentProblems() ?? Array.Empty<LiveProblemItem>())
                .Select(item => new
                {
                    id = $"problem-{item.JobId:N}",
                    title = $"{item.State}: {item.Summary}",
                    detail = item.Message ?? "This job requires review before another live write.",
                    sessionIds = new[] { item.SessionId.ToString("D") },
                    resource = (string?)null
                }))
            .ToArray();
        var writerQueueItem = queueItems.FirstOrDefault(item =>
            item.State is JobState.Executing or JobState.Verifying);
        return new
        {
            projectId,
            projectName = rhinoName,
            rhinoFile = rhinoPath ?? "Untitled.3dm",
            grasshopperFile = grasshopperPath ?? "No Grasshopper definition",
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
            currentSelection = live?.CurrentSelection is { } selection
                ? new
                {
                    rhinoObjectCount = selection.RhinoObjectIds.Count,
                    rhinoObjectIds = selection.RhinoObjectIds
                        .Take(32)
                        .Select(id => id.ToString("D"))
                        .ToArray(),
                    activeLayer = selection.ActiveLayerName,
                    observedAt = selection.ObservedAt
                }
                : null,
            lastUpdatedAt = DateTimeOffset.UtcNow
        };
    }

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
