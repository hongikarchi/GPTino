using System.Collections.Concurrent;
using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Codex;
using GPTino.AgentHost.Data;
using GPTino.AgentHost.Hosting;
using GPTino.Contracts;
using GPTino.Core;

namespace GPTino.AgentHost.Runtime;

public sealed class SessionOrchestrator : IDisposable
{
    private readonly SessionStore _store;
    private readonly CodexAppServerClient _codex;
    private readonly ModelSelector _models;
    private readonly MessageRoutingPolicy _routing;
    private readonly EffectiveModelState _effectiveModels;
    private readonly AgentHostOptions _options;
    private readonly RuntimeControl _runtime;
    private readonly EventHub _events;
    private readonly ILogger<SessionOrchestrator> _logger;
    private readonly SemaphoreSlim _parallelTurns;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _sessionGates = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _stateTransitionGates = new();
    private readonly ConcurrentDictionary<Guid, SessionPauseGate> _pauseGates = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _turnCompletions = new();
    private readonly ConcurrentDictionary<string, string> _earlyTurnCompletions = new();
    private readonly ConcurrentDictionary<Guid, ActiveTurn> _activeTurns = new();
    private readonly CancellationToken _shutdown;

    public SessionOrchestrator(
        SessionStore store,
        CodexAppServerClient codex,
        ModelSelector models,
        MessageRoutingPolicy routing,
        EffectiveModelState effectiveModels,
        AgentHostOptions options,
        RuntimeControl runtime,
        EventHub events,
        IHostApplicationLifetime lifetime,
        ILogger<SessionOrchestrator> logger)
    {
        _store = store;
        _codex = codex;
        _models = models;
        _routing = routing;
        _effectiveModels = effectiveModels;
        _options = options;
        _runtime = runtime;
        _events = events;
        _logger = logger;
        _parallelTurns = new SemaphoreSlim(Math.Clamp(options.MaxParallelTurns, 1, 16));
        _shutdown = lifetime.ApplicationStopping;
        _codex.NotificationReceived += HandleNotificationAsync;
    }

    public async Task<AcceptedTurn> SubmitMessageAsync(
        Guid sessionId,
        SendMessageRequest request,
        CancellationToken cancellationToken)
    {
        var session = await _store.FindSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session {sessionId:D} was not found.");
        if (session.State == SessionStates.Paused)
        {
            throw new SessionPausedException(sessionId);
        }
        var append = await _store.AppendMessageOnceAsync(
            sessionId,
            "user",
            request.Content,
            clientMessageId: request.ClientMessageId,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!append.Created)
        {
            return new AcceptedTurn(sessionId, append.Message.Id, session.State);
        }
        await _store.SetSessionStateAsync(sessionId, SessionStates.Waiting, request.Content, cancellationToken).ConfigureAwait(false);
        _events.Publish();
        _ = Task.Run(() => RunTurnAsync(sessionId, request.Content, _shutdown), CancellationToken.None);
        return new AcceptedTurn(sessionId, append.Message.Id, SessionStates.Waiting);
    }

    public async Task SetSessionPausedAsync(Guid sessionId, bool paused, CancellationToken cancellationToken)
    {
        var session = await _store.FindSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session {sessionId:D} was not found.");
        var pauseGate = _pauseGates.GetOrAdd(sessionId, static _ => new SessionPauseGate());
        if (!paused && session.State != SessionStates.Paused && !pauseGate.IsPaused)
        {
            return;
        }
        if (paused)
        {
            pauseGate.Pause();
        }
        var transition = _stateTransitionGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _store.SetSessionStateAsync(
                sessionId,
                paused
                    ? SessionStates.Paused
                    : pauseGate.WaiterCount > 0 ? SessionStates.Waiting : SessionStates.Idle,
                paused ? session.CurrentTask : null,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            transition.Release();
        }
        if (paused)
        {
            await InterruptActiveTurnAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            pauseGate.Resume();
        }
        _events.Publish();
    }

    public void Dispose()
    {
        _codex.NotificationReceived -= HandleNotificationAsync;
        _parallelTurns.Dispose();
        foreach (var gate in _sessionGates.Values)
        {
            gate.Dispose();
        }
        foreach (var gate in _stateTransitionGates.Values)
        {
            gate.Dispose();
        }
    }

    private async Task RunTurnAsync(Guid sessionId, string content, CancellationToken cancellationToken)
    {
        var sessionGate = _sessionGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        var parallelAcquired = false;
        try
        {
            await sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var pauseGate = _pauseGates.GetOrAdd(sessionId, static _ => new SessionPauseGate());
                SessionRecord latest;
                while (true)
                {
                    await pauseGate.WaitUntilResumedAsync(cancellationToken).ConfigureAwait(false);
                    await _runtime.WaitUntilResumedAsync(cancellationToken).ConfigureAwait(false);
                    await _parallelTurns.WaitAsync(cancellationToken).ConfigureAwait(false);
                    parallelAcquired = true;
                    var transition = _stateTransitionGates.GetOrAdd(
                        sessionId,
                        static _ => new SemaphoreSlim(1, 1));
                    await transition.WaitAsync(cancellationToken).ConfigureAwait(false);
                    var canStart = false;
                    try
                    {
                        latest = await _store.FindSessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
                            ?? throw new KeyNotFoundException($"Session {sessionId:D} was not found.");
                        canStart = !pauseGate.IsPaused && latest.State != SessionStates.Paused;
                        if (canStart)
                        {
                            await _store.SetSessionStateAsync(
                                sessionId,
                                SessionStates.Running,
                                content,
                                cancellationToken).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        transition.Release();
                    }
                    if (canStart)
                    {
                        break;
                    }
                    _parallelTurns.Release();
                    parallelAcquired = false;
                }
                _events.Publish();

                var previousContextFloor = _effectiveModels.TryGet(sessionId, out var previousModel)
                    ? previousModel.EffectiveProfile
                    : (ModelProfile?)null;
                var route = _routing.Route(content, latest.ModelProfile, previousContextFloor);
                ModelSelection selection;
                try
                {
                    selection = await _models.SelectAsync(route, latest.Model, cancellationToken).ConfigureAwait(false);
                    _effectiveModels.RecordSuccess(sessionId, route, selection);
                }
                catch (ModelRoutingException exception)
                {
                    _effectiveModels.RecordFailure(sessionId, route, exception);
                    throw;
                }
                _events.Publish();
                var threadId = latest.CodexThreadId;
                if (string.IsNullOrWhiteSpace(threadId))
                {
                    threadId = await _codex.StartThreadAsync(
                        _options.ProjectDirectory,
                        selection.Model,
                        cancellationToken).ConfigureAwait(false);
                    await _store.SetThreadIdAsync(sessionId, threadId, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _codex.ResumeThreadAsync(
                        threadId,
                        _options.ProjectDirectory,
                        selection.Model,
                        cancellationToken).ConfigureAwait(false);
                }

                var turnId = await _codex.StartTurnAsync(
                    threadId,
                    content,
                    selection.Model,
                    selection.Effort,
                    cancellationToken).ConfigureAwait(false);
                var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                _turnCompletions[turnId] = completion;
                if (_earlyTurnCompletions.TryRemove(turnId, out var earlyState))
                {
                    completion.TrySetResult(earlyState);
                }
                _activeTurns[sessionId] = new ActiveTurn(threadId, turnId);
                if (pauseGate.IsPaused)
                {
                    await InterruptActiveTurnAsync(sessionId, cancellationToken).ConfigureAwait(false);
                }
                var terminalState = await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                var current = await _store.FindSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
                if (current?.State != SessionStates.Paused)
                {
                    await _store.SetSessionStateAsync(
                        sessionId,
                        terminalState == "completed" ? SessionStates.Idle : SessionStates.Failed,
                        null,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                if (parallelAcquired)
                {
                    _parallelTurns.Release();
                }
                sessionGate.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Codex turn failed for session {SessionId}.", sessionId);
            try
            {
                await _store.AppendMessageAsync(
                    sessionId,
                    "system",
                    $"Turn failed: {exception.Message}",
                    phase: "error",
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
                var current = await _store.FindSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                if (current?.State != SessionStates.Paused)
                {
                    await _store.SetSessionStateAsync(sessionId, SessionStates.Failed, null, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception persistException)
            {
                _logger.LogError(persistException, "Could not persist failure state for session {SessionId}.", sessionId);
            }
        }
        finally
        {
            if (_activeTurns.TryRemove(sessionId, out var active))
            {
                _turnCompletions.TryRemove(active.TurnId, out _);
            }
            _events.Publish();
        }
    }

    private async Task HandleNotificationAsync(string method, JsonElement parameters)
    {
        if (method == "item/completed" &&
            parameters.TryGetProperty("threadId", out var threadElement) &&
            parameters.TryGetProperty("item", out var item) &&
            item.TryGetProperty("type", out var type) &&
            string.Equals(type.GetString(), "agentMessage", StringComparison.Ordinal) &&
            item.TryGetProperty("text", out var text))
        {
            var session = await _store.FindSessionByThreadAsync(threadElement.GetString() ?? string.Empty).ConfigureAwait(false);
            if (session is not null && !string.IsNullOrWhiteSpace(text.GetString()))
            {
                var phase = item.TryGetProperty("phase", out var phaseValue) ? phaseValue.GetString() : null;
                await _store.AppendMessageAsync(
                    session.Id,
                    "assistant",
                    text.GetString()!,
                    phase,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
                _events.Publish();
            }
            return;
        }

        if (method == "turn/completed" && parameters.TryGetProperty("turn", out var turn))
        {
            var turnId = turn.TryGetProperty("id", out var id) ? id.GetString() : null;
            var status = turn.TryGetProperty("status", out var statusValue)
                ? statusValue.GetString() ?? "failed"
                : "failed";
            if (turnId is not null && _turnCompletions.TryGetValue(turnId, out var completion))
            {
                completion.TrySetResult(status);
            }
            else if (turnId is not null)
            {
                _earlyTurnCompletions[turnId] = status;
            }
        }
    }

    private async Task InterruptActiveTurnAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!_activeTurns.TryGetValue(sessionId, out var active))
        {
            return;
        }
        try
        {
            await _codex.InterruptTurnAsync(active.ThreadId, active.TurnId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not interrupt active turn for paused session {SessionId}.", sessionId);
        }
    }

    private sealed record ActiveTurn(string ThreadId, string TurnId);

    private sealed class SessionPauseGate
    {
        private readonly object _gate = new();
        private TaskCompletionSource? _resume;
        private bool _paused;
        private int _waiters;

        public bool IsPaused
        {
            get
            {
                lock (_gate)
                {
                    return _paused;
                }
            }
        }

        public int WaiterCount => Volatile.Read(ref _waiters);

        public void Pause()
        {
            lock (_gate)
            {
                if (_paused)
                {
                    return;
                }
                _paused = true;
                _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public void Resume()
        {
            TaskCompletionSource? resume;
            lock (_gate)
            {
                if (!_paused)
                {
                    return;
                }
                _paused = false;
                resume = _resume;
                _resume = null;
            }
            resume?.TrySetResult();
        }

        public async Task WaitUntilResumedAsync(CancellationToken cancellationToken)
        {
            Task? wait;
            lock (_gate)
            {
                wait = _paused ? _resume?.Task : null;
            }
            if (wait is null)
            {
                return;
            }
            Interlocked.Increment(ref _waiters);
            try
            {
                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _waiters);
            }
        }
    }
}

public sealed class SessionPausedException(Guid sessionId)
    : InvalidOperationException($"Session {sessionId:D} is paused.");
