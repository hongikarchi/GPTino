using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
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
    private static readonly JsonSerializerOptions RecoveredContextJsonOptions = new(JsonDefaults.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly SessionStore _store;
    private readonly ICodexSessionClient _codex;
    private readonly ModelSelector _models;
    private readonly MessageRoutingPolicy _routing;
    private readonly EffectiveModelState _effectiveModels;
    private readonly AgentHostOptions _options;
    private readonly RuntimeControl _runtime;
    private readonly EventHub _events;
    private readonly ILogger<SessionOrchestrator> _logger;
    private readonly SemaphoreSlim _parallelTurns;
    private readonly SemaphoreSlim _codexRestartGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _sessionGates = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _stateTransitionGates = new();
    private readonly ConcurrentDictionary<Guid, SessionPauseGate> _pauseGates = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TurnCompletionSignal>> _turnCompletions = new();
    private readonly ConcurrentDictionary<string, TurnCompletionSignal> _earlyTurnCompletions = new();
    private readonly ConcurrentDictionary<string, byte> _assistantTurns = new();
    private readonly ConcurrentDictionary<Guid, ActiveTurn> _activeTurns = new();
    private readonly ISelectionContextSource? _selectionContext;
    private readonly CancellationToken _shutdown;
    private readonly TimeSpan _turnPollInterval;
    private readonly TimeSpan _turnReadTimeout;
    private readonly int _turnReadFailuresBeforeRestart;
    private readonly int _turnRestartCycles;
    private long _codexRestartGeneration;

    public SessionOrchestrator(
        SessionStore store,
        ICodexSessionClient codex,
        ModelSelector models,
        MessageRoutingPolicy routing,
        EffectiveModelState effectiveModels,
        AgentHostOptions options,
        RuntimeControl runtime,
        EventHub events,
        IHostApplicationLifetime lifetime,
        ILogger<SessionOrchestrator> logger,
        ISelectionContextSource? selectionContext = null)
    {
        _selectionContext = selectionContext;
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
        _turnPollInterval = PositiveOrDefault(options.CodexTurnPollInterval, TimeSpan.FromSeconds(2));
        _turnReadTimeout = PositiveOrDefault(options.CodexTurnReadTimeout, TimeSpan.FromSeconds(10));
        _turnReadFailuresBeforeRestart = Math.Max(1, options.CodexTurnReadFailuresBeforeRestart);
        _turnRestartCycles = Math.Max(0, options.CodexTurnRestartCycles);
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
        _codexRestartGate.Dispose();
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
                var migratedThread = false;
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
                    try
                    {
                        await _codex.ResumeThreadAsync(
                            threadId,
                            _options.ProjectDirectory,
                            selection.Model,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (CodexProtocolException exception) when (IsUnsupportedPaginatedThread(exception))
                    {
                        (threadId, content) = await ReplaceIncompatibleThreadAsync(
                            sessionId,
                            threadId,
                            content,
                            selection.Model,
                            cancellationToken).ConfigureAwait(false);
                        migratedThread = true;
                    }
                }

                string turnId;
                try
                {
                    turnId = await _codex.StartTurnAsync(
                        threadId,
                        ComposeTurnInput(content),
                        selection.Model,
                        selection.Effort,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (CodexProtocolException exception) when (
                    !migratedThread &&
                    !string.IsNullOrWhiteSpace(latest.CodexThreadId) &&
                    IsUnsupportedPaginatedThread(exception))
                {
                    (threadId, content) = await ReplaceIncompatibleThreadAsync(
                        sessionId,
                        threadId,
                        content,
                        selection.Model,
                        cancellationToken).ConfigureAwait(false);
                    turnId = await _codex.StartTurnAsync(
                        threadId,
                        ComposeTurnInput(content),
                        selection.Model,
                        selection.Effort,
                        cancellationToken).ConfigureAwait(false);
                }
                var completion = new TaskCompletionSource<TurnCompletionSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
                _turnCompletions[turnId] = completion;
                if (_earlyTurnCompletions.TryRemove(turnId, out var earlyCompletion))
                {
                    completion.TrySetResult(earlyCompletion);
                }
                var activeTurn = new ActiveTurn(threadId, turnId);
                _activeTurns[sessionId] = activeTurn;
                if (pauseGate.IsPaused)
                {
                    await InterruptActiveTurnAsync(sessionId, cancellationToken).ConfigureAwait(false);
                }
                var outcome = await WaitForTurnOutcomeAsync(
                    sessionId,
                    activeTurn,
                    completion.Task,
                    cancellationToken).ConfigureAwait(false);
                await CompleteTurnAsync(
                    sessionId,
                    pauseGate,
                    activeTurn,
                    outcome,
                    cancellationToken).ConfigureAwait(false);
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
                await PersistUnhandledTurnFailureAsync(sessionId, exception).ConfigureAwait(false);
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
                _assistantTurns.TryRemove(active.TurnId, out _);
            }
            _events.Publish();
        }
    }

    private async Task<(string ThreadId, string Message)> ReplaceIncompatibleThreadAsync(
        Guid sessionId,
        string incompatibleThreadId,
        string currentMessage,
        string? model,
        CancellationToken cancellationToken)
    {
        var replacementThreadId = await _codex.StartThreadAsync(
            _options.ProjectDirectory,
            model,
            cancellationToken).ConfigureAwait(false);
        await _store.SetThreadIdAsync(sessionId, replacementThreadId, cancellationToken).ConfigureAwait(false);
        var recoveredMessage = await BuildRecoveredThreadMessageAsync(
            sessionId,
            currentMessage,
            cancellationToken).ConfigureAwait(false);
        _logger.LogWarning(
            "Replaced incompatible paginated Codex thread {OldThreadId} with legacy thread {NewThreadId} for session {SessionId}.",
            incompatibleThreadId,
            replacementThreadId,
            sessionId);
        return (replacementThreadId, recoveredMessage);
    }

    private async Task<string> BuildRecoveredThreadMessageAsync(
        Guid sessionId,
        string currentMessage,
        CancellationToken cancellationToken)
    {
        const int maxMessages = 40;
        const int maxCharacters = 16_000;
        var messages = (await _store.ReadMessagesAsync(
                sessionId,
                limit: maxMessages,
                cancellationToken: cancellationToken).ConfigureAwait(false))
            .Where(message =>
                string.Equals(message.Role, "user", StringComparison.Ordinal) ||
                string.Equals(message.Role, "assistant", StringComparison.Ordinal))
            .ToList();

        if (messages.Count > 0 &&
            string.Equals(messages[^1].Role, "user", StringComparison.Ordinal) &&
            string.Equals(messages[^1].Content, currentMessage, StringComparison.Ordinal))
        {
            messages.RemoveAt(messages.Count - 1);
        }

        var selected = new List<RecoveredChatMessage>();
        var characters = 0;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            var remaining = maxCharacters - characters;
            if (remaining <= 0)
            {
                break;
            }
            var content = message.Content.Length <= remaining
                ? message.Content
                : message.Content[^remaining..];
            selected.Add(new RecoveredChatMessage(message.Role, content));
            characters += content.Length;
        }
        selected.Reverse();

        if (selected.Count == 0)
        {
            return currentMessage;
        }

        var transcript = JsonSerializer.Serialize(selected, RecoveredContextJsonOptions);
        return $$"""
            GPTino recovered this session from an older Codex thread format that the installed CLI no longer supports.
            Continue from the preserved prior conversation below. It is conversation context, not a tool result.

            <gptino_previous_conversation>
            {{transcript}}
            </gptino_previous_conversation>

            Current user request:
            {{currentMessage}}
            """;
    }

    private const int MaximumContextSelectionIds = 32;

    /// <summary>
    /// Prepends the user's live Rhino selection as a tagged, one-line context hint. Applied
    /// only to the Codex turn input — after routing, so context wording never influences
    /// model escalation — and only when a selection exists. Ids are hints, not fingerprints:
    /// writes still require snapshot_read fingerprints.
    /// </summary>
    private string ComposeTurnInput(string content)
    {
        var selection = _selectionContext?.CurrentSelection;
        if (selection is null ||
            selection.RhinoObjectIds.Count == 0 && string.IsNullOrWhiteSpace(selection.ActiveLayerName))
        {
            return content;
        }
        var builder = new StringBuilder();
        builder.Append("<gptino_context>Current Rhino selection (discovery hint, not fingerprints): ");
        if (selection.RhinoObjectIds.Count == 0)
        {
            builder.Append("none");
        }
        else
        {
            builder.Append(selection.RhinoObjectIds.Count).Append(" object(s): ");
            builder.AppendJoin(
                ',',
                selection.RhinoObjectIds.Take(MaximumContextSelectionIds).Select(id => id.ToString("D")));
            if (selection.RhinoObjectIds.Count > MaximumContextSelectionIds)
            {
                builder.Append(",...");
            }
        }
        if (!string.IsNullOrWhiteSpace(selection.ActiveLayerName))
        {
            builder.Append("; active layer: ").Append(selection.ActiveLayerName);
        }
        builder.Append("</gptino_context>").Append('\n');
        builder.Append(content);
        return builder.ToString();
    }

    private static bool IsUnsupportedPaginatedThread(CodexProtocolException exception)
    {
        var message = exception.Message;
        return message.Contains("paginated_threads", StringComparison.OrdinalIgnoreCase) &&
            (message.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<TurnOutcome> WaitForTurnOutcomeAsync(
        Guid sessionId,
        ActiveTurn active,
        Task<TurnCompletionSignal> completion,
        CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        var restartCycles = 0;
        var observedRestartGeneration = Volatile.Read(ref _codexRestartGeneration);
        var notificationOnly = false;
        Exception? notificationFallbackFailure = null;

        while (true)
        {
            var delay = Task.Delay(_turnPollInterval, cancellationToken);
            if (await Task.WhenAny(completion, delay).ConfigureAwait(false) == completion)
            {
                var signal = await completion.ConfigureAwait(false);
                var finalSnapshot = await TryReadFinalSnapshotAsync(active, cancellationToken).ConfigureAwait(false);
                if (finalSnapshot is not null)
                {
                    await ReconcileSnapshotAsync(sessionId, active.TurnId, finalSnapshot, cancellationToken).ConfigureAwait(false);
                    if (IsTerminalStatus(finalSnapshot.Status))
                    {
                        return TurnOutcome.FromSnapshot(finalSnapshot);
                    }
                }
                return TurnOutcome.FromNotification(signal);
            }
            await delay.ConfigureAwait(false);

            if (notificationOnly)
            {
                if (completion.IsCompleted)
                {
                    continue;
                }
                if (!_codex.IsRunning)
                {
                    throw new CodexTurnRecoveryException(
                        $"Codex App Server exited while turn {active.TurnId} was waiting for its completion notification.",
                        notificationFallbackFailure ?? new IOException("Codex App Server exited."));
                }
                continue;
            }

            var readTask = ReadTurnWithTimeoutAsync(active, cancellationToken);
            if (await Task.WhenAny(completion, readTask).ConfigureAwait(false) == completion)
            {
                var signal = await completion.ConfigureAwait(false);
                try
                {
                    var finalSnapshot = await readTask.ConfigureAwait(false);
                    if (finalSnapshot is not null)
                    {
                        await ReconcileSnapshotAsync(sessionId, active.TurnId, finalSnapshot, cancellationToken).ConfigureAwait(false);
                        if (IsTerminalStatus(finalSnapshot.Status))
                        {
                            return TurnOutcome.FromSnapshot(finalSnapshot);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "The final authoritative read failed for Codex turn {TurnId}; using its completion notification.",
                        active.TurnId);
                }
                return TurnOutcome.FromNotification(signal);
            }

            CodexTurnReadResult? snapshot;
            try
            {
                snapshot = await readTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (_codex.IsRunning &&
                    TryClassifyNotificationFallbackReadFailure(exception, out var failureKind))
                {
                    notificationOnly = true;
                    notificationFallbackFailure = exception;
                    consecutiveFailures = 0;
                    restartCycles = 0;
                    _logger.LogWarning(
                        "Codex authoritative polling is unavailable for turn {TurnId} ({FailureKind}); " +
                        "waiting for the App Server completion notification without restarting it.",
                        active.TurnId,
                        failureKind);
                    continue;
                }
                (consecutiveFailures, restartCycles, observedRestartGeneration) = await RecoverFromTurnReadFailureAsync(
                    active,
                    exception,
                    consecutiveFailures,
                    restartCycles,
                    observedRestartGeneration,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (snapshot is null)
            {
                // A valid thread/read response can omit the active turn until Codex has persisted
                // its first item. The App Server answered successfully, so this is evidence that
                // the connection is healthy rather than a reason to restart and interrupt the turn.
                consecutiveFailures = 0;
                restartCycles = 0;
                observedRestartGeneration = Volatile.Read(ref _codexRestartGeneration);
                continue;
            }

            await ReconcileSnapshotAsync(sessionId, active.TurnId, snapshot, cancellationToken).ConfigureAwait(false);
            if (IsTerminalStatus(snapshot.Status))
            {
                return TurnOutcome.FromSnapshot(snapshot);
            }
            if (!IsInProgressStatus(snapshot.Status))
            {
                (consecutiveFailures, restartCycles, observedRestartGeneration) = await RecoverFromTurnReadFailureAsync(
                    active,
                    new CodexProtocolException($"thread/read returned unsupported turn status '{snapshot.Status}'."),
                    consecutiveFailures,
                    restartCycles,
                    observedRestartGeneration,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            // A healthy in-progress snapshot is proof that the connection is alive. There is deliberately
            // no whole-turn timeout: complex turns may continue for as long as Codex reports valid progress.
            consecutiveFailures = 0;
            restartCycles = 0;
            observedRestartGeneration = Volatile.Read(ref _codexRestartGeneration);
        }
    }

    private static bool TryClassifyNotificationFallbackReadFailure(
        Exception exception,
        out string failureKind)
    {
        failureKind = string.Empty;
        if (exception is not CodexProtocolException protocolException)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(protocolException.Message);
            var root = document.RootElement;
            if (!root.TryGetProperty("code", out var codeElement) ||
                !codeElement.TryGetInt32(out var code) ||
                !root.TryGetProperty("message", out var messageElement) ||
                messageElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            var message = messageElement.GetString() ?? string.Empty;
            if (code == -32603 &&
                message.Contains("rollout", StringComparison.OrdinalIgnoreCase) &&
                (message.Contains("read", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("load", StringComparison.OrdinalIgnoreCase)))
            {
                failureKind = "active-rollout-read";
                return true;
            }
            if (code == -32601 &&
                message.Contains("paginated_threads", StringComparison.OrdinalIgnoreCase) &&
                (message.Contains("not supported", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("unsupported", StringComparison.OrdinalIgnoreCase)))
            {
                failureKind = "paginated-thread-read";
                return true;
            }
        }
        catch (JsonException)
        {
        }
        return false;
    }

    private async Task<CodexTurnReadResult?> TryReadFinalSnapshotAsync(
        ActiveTurn active,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadTurnWithTimeoutAsync(active, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "The final authoritative read failed for Codex turn {TurnId}; using its completion notification.",
                active.TurnId);
            return null;
        }
    }

    private async Task<CodexTurnReadResult?> ReadTurnWithTimeoutAsync(
        ActiveTurn active,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_turnReadTimeout);
        try
        {
            return await _codex.ReadTurnAsync(active.ThreadId, active.TurnId, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Codex thread/read for turn {active.TurnId} exceeded {_turnReadTimeout}.",
                exception);
        }
    }

    private async Task<(int ConsecutiveFailures, int RestartCycles, long RestartGeneration)> RecoverFromTurnReadFailureAsync(
        ActiveTurn active,
        Exception exception,
        int consecutiveFailures,
        int restartCycles,
        long observedRestartGeneration,
        CancellationToken cancellationToken)
    {
        consecutiveFailures++;
        _logger.LogWarning(
            exception,
            "Authoritative read {FailureCount}/{FailureLimit} failed for Codex turn {TurnId}.",
            consecutiveFailures,
            _turnReadFailuresBeforeRestart,
            active.TurnId);
        if (consecutiveFailures < _turnReadFailuresBeforeRestart)
        {
            return (consecutiveFailures, restartCycles, observedRestartGeneration);
        }
        if (restartCycles >= _turnRestartCycles)
        {
            throw new CodexTurnRecoveryException(
                $"Codex turn {active.TurnId} could not be recovered after {_turnRestartCycles} App Server restart cycle(s).",
                exception);
        }

        var nextGeneration = await RestartCodexIfCurrentAsync(
            observedRestartGeneration,
            cancellationToken).ConfigureAwait(false);
        return (0, restartCycles + 1, nextGeneration);
    }

    private async Task<long> RestartCodexIfCurrentAsync(
        long observedGeneration,
        CancellationToken cancellationToken)
    {
        await _codexRestartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentGeneration = Volatile.Read(ref _codexRestartGeneration);
            if (currentGeneration != observedGeneration)
            {
                return currentGeneration;
            }

            if (_codex.IsRunning)
            {
                await _codex.StopAsync().ConfigureAwait(false);
            }
            return Interlocked.Increment(ref _codexRestartGeneration);
        }
        finally
        {
            _codexRestartGate.Release();
        }
    }

    private async Task ReconcileSnapshotAsync(
        Guid sessionId,
        string turnId,
        CodexTurnReadResult snapshot,
        CancellationToken cancellationToken)
    {
        foreach (var message in snapshot.AgentMessages)
        {
            await PersistAssistantMessageAsync(
                sessionId,
                turnId,
                message.Text,
                message.Phase,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CompleteTurnAsync(
        Guid sessionId,
        SessionPauseGate pauseGate,
        ActiveTurn active,
        TurnOutcome outcome,
        CancellationToken cancellationToken)
    {
        var transition = _stateTransitionGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await _store.FindSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
            if (current is null || pauseGate.IsPaused || current.State == SessionStates.Paused)
            {
                return;
            }

            var completed = IsCompletedStatus(outcome.Status);
            var hasAssistant = _assistantTurns.ContainsKey(active.TurnId);
            if (!completed || !hasAssistant)
            {
                var error = completed
                    ? "Codex reported completion, but GPTino could not recover an assistant response."
                    : FormatTurnFailure(outcome);
                await PersistSystemErrorAsync(
                    sessionId,
                    active.TurnId,
                    error,
                    cancellationToken).ConfigureAwait(false);
            }

            await _store.SetSessionStateAsync(
                sessionId,
                completed && hasAssistant ? SessionStates.Idle : SessionStates.Failed,
                null,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            transition.Release();
        }
    }

    private async Task PersistUnhandledTurnFailureAsync(Guid sessionId, Exception exception)
    {
        var message = $"Turn failed: {exception.Message}";
        if (_activeTurns.TryGetValue(sessionId, out var active))
        {
            await PersistSystemErrorAsync(
                sessionId,
                active.TurnId,
                message,
                CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await _store.AppendMessageAsync(
                sessionId,
                "system",
                message,
                phase: "error",
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }

        var transition = _stateTransitionGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await transition.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var current = await _store.FindSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            var pauseGate = _pauseGates.GetOrAdd(sessionId, static _ => new SessionPauseGate());
            if (current?.State != SessionStates.Paused && !pauseGate.IsPaused)
            {
                await _store.SetSessionStateAsync(
                    sessionId,
                    SessionStates.Failed,
                    null,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            transition.Release();
        }
    }

    private async Task PersistAssistantMessageAsync(
        Guid sessionId,
        string turnId,
        string text,
        string? phase,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        var append = await _store.AppendMessageOnceAsync(
            sessionId,
            "assistant",
            text,
            phase,
            BuildCodexMessageId(turnId, phase, text),
            cancellationToken).ConfigureAwait(false);
        if (string.Equals(append.Message.Role, "assistant", StringComparison.Ordinal))
        {
            _assistantTurns[turnId] = 0;
        }
        if (append.Created)
        {
            _events.Publish();
        }
    }

    private async Task PersistSystemErrorAsync(
        Guid sessionId,
        string turnId,
        string text,
        CancellationToken cancellationToken)
    {
        var append = await _store.AppendMessageOnceAsync(
            sessionId,
            "system",
            text,
            phase: "error",
            clientMessageId: BuildCodexMessageId(turnId, "error", text),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (append.Created)
        {
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
            var threadId = threadElement.GetString() ?? string.Empty;
            var session = await _store.FindSessionByThreadAsync(threadId).ConfigureAwait(false);
            var turnId = ReadString(parameters, "turnId");
            if (turnId is null && session is not null &&
                _activeTurns.TryGetValue(session.Id, out var active) &&
                string.Equals(active.ThreadId, threadId, StringComparison.Ordinal))
            {
                turnId = active.TurnId;
            }
            if (session is not null && turnId is not null && !string.IsNullOrWhiteSpace(text.GetString()))
            {
                await PersistAssistantMessageAsync(
                    session.Id,
                    turnId,
                    text.GetString()!,
                    ReadString(item, "phase"),
                    CancellationToken.None).ConfigureAwait(false);
            }
            return;
        }

        if (method == "turn/completed" && parameters.TryGetProperty("turn", out var turn))
        {
            var turnId = ReadString(turn, "id");
            var completion = new TurnCompletionSignal(
                ReadString(turn, "status") ?? "failed",
                ParseTurnError(turn));
            if (turnId is not null && _turnCompletions.TryGetValue(turnId, out var waiter))
            {
                waiter.TrySetResult(completion);
            }
            else if (turnId is not null)
            {
                _earlyTurnCompletions[turnId] = completion;
            }
        }
    }

    private static CodexTurnError? ParseTurnError(JsonElement turn)
    {
        if (!turn.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        return new CodexTurnError(
            ReadString(error, "message") ?? "Unknown Codex turn error.",
            ReadString(error, "additionalDetails"),
            error.TryGetProperty("codexErrorInfo", out var info) && info.ValueKind != JsonValueKind.Null
                ? info.Clone()
                : null);
    }

    private static string FormatTurnFailure(TurnOutcome outcome)
    {
        if (outcome.Error is null)
        {
            return $"Codex turn ended with status '{outcome.Status}'.";
        }
        return string.IsNullOrWhiteSpace(outcome.Error.AdditionalDetails)
            ? $"Codex turn failed: {outcome.Error.Message}"
            : $"Codex turn failed: {outcome.Error.Message} ({outcome.Error.AdditionalDetails})";
    }

    private static string BuildCodexMessageId(string turnId, string? phase, string text)
    {
        var normalizedPhase = string.IsNullOrWhiteSpace(phase) ? "none" : phase.Trim().ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return $"gptino-codex-v1:{turnId}:{normalizedPhase}:{hash}";
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsCompletedStatus(string status) =>
        string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);

    private static bool IsInProgressStatus(string status) =>
        string.Equals(status, "inProgress", StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminalStatus(string status) =>
        IsCompletedStatus(status) ||
        string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "canceled", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

    private static TimeSpan PositiveOrDefault(TimeSpan value, TimeSpan fallback) =>
        value > TimeSpan.Zero ? value : fallback;

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

    private sealed record RecoveredChatMessage(string Role, string Content);

    private sealed record TurnCompletionSignal(string Status, CodexTurnError? Error);

    private sealed record TurnOutcome(string Status, CodexTurnError? Error)
    {
        public static TurnOutcome FromNotification(TurnCompletionSignal completion) =>
            new(completion.Status, completion.Error);

        public static TurnOutcome FromSnapshot(CodexTurnReadResult snapshot) =>
            new(snapshot.Status, snapshot.Error);
    }

    private sealed class CodexTurnRecoveryException(string message, Exception innerException)
        : InvalidOperationException(message, innerException);

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
