using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Codex;
using GPTino.AgentHost.Data;
using GPTino.AgentHost.Hosting;
using GPTino.AgentHost.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace GPTino.AgentHost.Tests;

public sealed class SessionOrchestratorTests
{
    [Fact]
    public async Task LostNotificationsAreRecoveredByAuthoritativePolling()
    {
        using var directory = new TestDirectory();
        var client = new FakeCodexSessionClient
        {
            ReadTurn = (_, _, _) => Task.FromResult<CodexTurnReadResult?>(Completed("Recovered answer"))
        };
        using var harness = await CreateHarnessAsync(directory, client);

        await harness.Orchestrator.SubmitMessageAsync(
            harness.Session.Id,
            new SendMessageRequest("inspect the model", "user-1"),
            CancellationToken.None);

        var session = await WaitForStateAsync(harness.Store, harness.Session.Id, SessionStates.Idle);
        var messages = await harness.Store.ReadMessagesAsync(harness.Session.Id);
        Assert.Null(session.CurrentTask);
        var assistant = Assert.Single(messages, message => message.Role == "assistant");
        Assert.Equal("Recovered answer", assistant.Content);
        Assert.Equal("final_answer", assistant.Phase);
    }

    [Fact]
    public async Task TurnInputCarriesSelectionContextHintWithoutAlteringTheStoredMessage()
    {
        using var directory = new TestDirectory();
        var client = new FakeCodexSessionClient
        {
            ReadTurn = (_, _, _) => Task.FromResult<CodexTurnReadResult?>(Completed("done"))
        };
        var selectionContext = new StaticSelectionContext(new GPTino.BridgeContract.SelectionChangedEvent(
            [Guid.Parse("7f2a4c31-9a41-4c8e-b6a1-2f6d3a5e9c01")],
            "Facade::Panels",
            DateTimeOffset.UtcNow));
        using var harness = await CreateHarnessAsync(directory, client, selectionContext: selectionContext);

        await harness.Orchestrator.SubmitMessageAsync(
            harness.Session.Id,
            new SendMessageRequest("선택한 객체를 위로 이동해줘", "selection-1"),
            CancellationToken.None);

        await WaitForStateAsync(harness.Store, harness.Session.Id, SessionStates.Idle);
        var startedTurn = Assert.Single(client.StartedTurns);
        Assert.StartsWith("<gptino_context>", startedTurn.Message, StringComparison.Ordinal);
        Assert.Contains("7f2a4c31-9a41-4c8e-b6a1-2f6d3a5e9c01", startedTurn.Message, StringComparison.Ordinal);
        Assert.Contains("Facade::Panels", startedTurn.Message, StringComparison.Ordinal);
        Assert.EndsWith("선택한 객체를 위로 이동해줘", startedTurn.Message, StringComparison.Ordinal);

        var messages = await harness.Store.ReadMessagesAsync(harness.Session.Id);
        var user = Assert.Single(messages, message => message.Role == "user");
        Assert.Equal("선택한 객체를 위로 이동해줘", user.Content);
    }

    private sealed class StaticSelectionContext(GPTino.BridgeContract.SelectionChangedEvent? selection)
        : ISelectionContextSource
    {
        public GPTino.BridgeContract.SelectionChangedEvent? CurrentSelection { get; } = selection;
    }

    [Fact]
    public async Task CompletionNotificationStillPerformsFinalReadAndDeduplicatesItsItem()
    {
        using var directory = new TestDirectory();
        var client = new FakeCodexSessionClient
        {
            ReadTurn = (_, _, _) => Task.FromResult<CodexTurnReadResult?>(Completed("Authoritative answer"))
        };
        client.TurnStarted = async (threadId, turnId) =>
        {
            await client.RaiseItemCompletedAsync(threadId, turnId, "Authoritative answer", "final_answer");
            await client.RaiseTurnCompletedAsync(
                turnId,
                "failed",
                new CodexTurnError("stale notification", "snapshot must win", null));
        };
        using var harness = await CreateHarnessAsync(directory, client);

        await harness.Orchestrator.SubmitMessageAsync(
            harness.Session.Id,
            new SendMessageRequest("read status", "user-2"),
            CancellationToken.None);

        await WaitForStateAsync(harness.Store, harness.Session.Id, SessionStates.Idle);
        var messages = await harness.Store.ReadMessagesAsync(harness.Session.Id);
        var assistant = Assert.Single(messages, message => message.Role == "assistant");
        Assert.Equal("Authoritative answer", assistant.Content);
        Assert.DoesNotContain(messages, message => message.Role == "system" && message.Phase == "error");

        var persistedId = await ReadAssistantClientMessageIdAsync(harness.DatabasePath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("Authoritative answer")));
        Assert.Equal($"gptino-codex-v1:turn-1:final_answer:{hash}", persistedId);
    }

    [Fact]
    public async Task UnsupportedPaginatedThreadIsReboundAndPriorConversationIsRecovered()
    {
        using var directory = new TestDirectory();
        var client = new FakeCodexSessionClient
        {
            ThreadToStart = "legacy-thread",
            ResumeThread = (_, _, _, _) => throw new CodexProtocolException(
                "{\"code\":-32601,\"message\":\"paginated_threads is not supported yet\"}"),
            ReadTurn = (_, _, _) => Task.FromResult<CodexTurnReadResult?>(Completed("Recovered answer"))
        };
        using var harness = await CreateHarnessAsync(directory, client);
        await harness.Store.SetThreadIdAsync(harness.Session.Id, "paginated-thread");
        await harness.Store.AppendMessageAsync(harness.Session.Id, "user", "이전 질문");
        await harness.Store.AppendMessageAsync(harness.Session.Id, "assistant", "이전 답변");

        await harness.Orchestrator.SubmitMessageAsync(
            harness.Session.Id,
            new SendMessageRequest("현재 요청", "paginated-recovery"),
            CancellationToken.None);

        var session = await WaitForStateAsync(harness.Store, harness.Session.Id, SessionStates.Idle);
        var startedTurn = Assert.Single(client.StartedTurns);
        Assert.Equal("legacy-thread", session.CodexThreadId);
        Assert.Equal(1, client.StartThreadCount);
        Assert.Equal("legacy-thread", startedTurn.ThreadId);
        Assert.Contains("이전 질문", startedTurn.Message, StringComparison.Ordinal);
        Assert.Contains("이전 답변", startedTurn.Message, StringComparison.Ordinal);
        Assert.Contains("현재 요청", startedTurn.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PaginatedFailureAtTurnStartIsReboundOnceAndRetried()
    {
        using var directory = new TestDirectory();
        var client = new FakeCodexSessionClient
        {
            ThreadToStart = "legacy-thread",
            StartTurn = (threadId, _, _, _, _) => threadId == "paginated-thread"
                ? throw new CodexProtocolException(
                    "{\"code\":-32601,\"message\":\"paginated_threads is not supported yet\"}")
                : Task.FromResult("turn-1"),
            ReadTurn = (_, _, _) => Task.FromResult<CodexTurnReadResult?>(Completed("Retried answer"))
        };
        using var harness = await CreateHarnessAsync(directory, client);
        await harness.Store.SetThreadIdAsync(harness.Session.Id, "paginated-thread");

        await harness.Orchestrator.SubmitMessageAsync(
            harness.Session.Id,
            new SendMessageRequest("retry current request", "paginated-turn-retry"),
            CancellationToken.None);

        var session = await WaitForStateAsync(harness.Store, harness.Session.Id, SessionStates.Idle);
        Assert.Equal("legacy-thread", session.CodexThreadId);
        Assert.Equal(1, client.StartThreadCount);
        Assert.Equal(2, client.StartTurnCount);
        Assert.Equal("legacy-thread", client.StartedTurns[^1].ThreadId);
    }

    [Fact]
    public async Task ConsecutiveReadTimeoutsRestartOnceAndThenRecover()
    {
        using var directory = new TestDirectory();
        var client = new FakeCodexSessionClient();
        client.ReadTurn = async (_, _, cancellationToken) =>
        {
            if (client.StopCount == 0)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return Completed("Answer after restart");
        };
        using var harness = await CreateHarnessAsync(
            directory,
            client,
            failuresBeforeRestart: 2,
            restartCycles: 1,
            readTimeout: TimeSpan.FromMilliseconds(15));

        await harness.Orchestrator.SubmitMessageAsync(
            harness.Session.Id,
            new SendMessageRequest("read after restart"),
            CancellationToken.None);

        await WaitForStateAsync(harness.Store, harness.Session.Id, SessionStates.Idle);
        Assert.Equal(1, client.StopCount);
        Assert.True(client.ReadCount >= 3);
    }

    [Fact]
    public async Task HealthyReadCanOmitActiveTurnWithoutRestartingIt()
    {
        using var directory = new TestDirectory();
        var reads = 0;
        var client = new FakeCodexSessionClient
        {
            ReadTurn = (_, _, _) => Task.FromResult<CodexTurnReadResult?>(
                Interlocked.Increment(ref reads) <= 3
                    ? null
                    : Completed("Answer after the turn became visible"))
        };
        using var harness = await CreateHarnessAsync(
            directory,
            client,
            failuresBeforeRestart: 1,
            restartCycles: 0);

        await harness.Orchestrator.SubmitMessageAsync(
            harness.Session.Id,
            new SendMessageRequest("wait for the active turn"),
            CancellationToken.None);

        await WaitForStateAsync(harness.Store, harness.Session.Id, SessionStates.Idle);
        var messages = await harness.Store.ReadMessagesAsync(harness.Session.Id);
        Assert.Equal(0, client.StopCount);
        Assert.True(client.ReadCount >= 4);
        Assert.Contains(
            messages,
            message =>
                message.Role == "assistant" &&
                message.Content == "Answer after the turn became visible");
    }

    [Fact]
    public async Task ActiveRolloutReadFailureUsesCompletionNotificationWithoutRestarting()
    {
        using var directory = new TestDirectory();
        Task notificationTask = Task.CompletedTask;
        var client = new FakeCodexSessionClient
        {
            ReadTurn = (_, _, _) => throw new CodexProtocolException(
                "{\"code\":-32603,\"message\":\"failed to read active rollout while it is being written\"}")
        };
        client.TurnStarted = (threadId, turnId) =>
        {
            notificationTask = Task.Run(async () =>
            {
                await Task.Delay(25);
                await client.RaiseItemCompletedAsync(
                    threadId,
                    turnId,
                    "Answer from completion notification",
                    "final_answer");
                await client.RaiseTurnCompletedAsync(turnId, "completed", null);
            });
            return Task.CompletedTask;
        };
        using var harness = await CreateHarnessAsync(
            directory,
            client,
            failuresBeforeRestart: 1,
            restartCycles: 1);

        await harness.Orchestrator.SubmitMessageAsync(
            harness.Session.Id,
            new SendMessageRequest("wait for the compatible completion path"),
            CancellationToken.None);

        await WaitForStateAsync(harness.Store, harness.Session.Id, SessionStates.Idle);
        await notificationTask;
        var messages = await harness.Store.ReadMessagesAsync(harness.Session.Id);
        Assert.Equal(0, client.StopCount);
        Assert.InRange(client.ReadCount, 1, 2);
        Assert.Contains(
            messages,
            message => message.Role == "assistant" &&
                message.Content == "Answer from completion notification");
    }

    [Fact]
    public async Task ConcurrentSessionsShareOneRestartAndRecoverWithoutMixingTurns()
    {
        using var directory = new TestDirectory();
        var firstReadsArrived = 0;
        var firstReadsReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFailureObserved = 0;
        var disruptedReadObserved = 0;
        var readsByTurn = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(
            StringComparer.Ordinal);
        var client = new FakeCodexSessionClient();
        client.StartTurn = (threadId, _, _, _, _) => Task.FromResult(threadId switch
        {
            "thread-alpha" => "turn-alpha",
            "thread-beta" => "turn-beta",
            _ => throw new InvalidOperationException($"Unexpected test thread '{threadId}'.")
        });
        client.ReadTurn = async (threadId, turnId, cancellationToken) =>
        {
            var attempt = readsByTurn.AddOrUpdate(turnId, 1, static (_, current) => current + 1);
            if (attempt == 1)
            {
                if (Interlocked.Increment(ref firstReadsArrived) == 2)
                {
                    firstReadsReady.TrySetResult();
                }
                await firstReadsReady.Task.WaitAsync(cancellationToken);

                if (turnId == "turn-alpha")
                {
                    Interlocked.Exchange(ref firstFailureObserved, 1);
                    throw new IOException("the first session lost the shared App Server");
                }

                await client.Stopped.Task.WaitAsync(cancellationToken);
                Interlocked.Exchange(ref disruptedReadObserved, 1);
                throw new IOException("the second session's pending read was disrupted by restart");
            }

            var answer = (threadId, turnId) switch
            {
                ("thread-alpha", "turn-alpha") => "Alpha recovered answer",
                ("thread-beta", "turn-beta") => "Beta recovered answer",
                _ => throw new InvalidOperationException(
                    $"Cross-session read detected for thread '{threadId}' and turn '{turnId}'.")
            };
            return new CodexTurnReadResult(
                turnId,
                "completed",
                null,
                [new CodexAgentMessage($"item-{turnId}", answer, "final_answer")]);
        };
        using var harness = await CreateHarnessAsync(
            directory,
            client,
            failuresBeforeRestart: 1,
            restartCycles: 1,
            readTimeout: TimeSpan.FromSeconds(1),
            maxParallelTurns: 2);
        var secondSession = await harness.Store.CreateSessionAsync(new CreateSessionRequest(
            "Second orchestrator test",
            ModelProfile: "standard",
            Model: "gpt-test"));
        await harness.Store.SetThreadIdAsync(harness.Session.Id, "thread-alpha");
        await harness.Store.SetThreadIdAsync(secondSession.Id, "thread-beta");

        await Task.WhenAll(
            harness.Orchestrator.SubmitMessageAsync(
                harness.Session.Id,
                new SendMessageRequest("run alpha", "concurrent-alpha"),
                CancellationToken.None),
            harness.Orchestrator.SubmitMessageAsync(
                secondSession.Id,
                new SendMessageRequest("run beta", "concurrent-beta"),
                CancellationToken.None));

        await Task.WhenAll(
            WaitForStateAsync(harness.Store, harness.Session.Id, SessionStates.Idle),
            WaitForStateAsync(harness.Store, secondSession.Id, SessionStates.Idle));

        var alphaMessages = await harness.Store.ReadMessagesAsync(harness.Session.Id);
        var betaMessages = await harness.Store.ReadMessagesAsync(secondSession.Id);
        var alphaAssistant = Assert.Single(alphaMessages, message => message.Role == "assistant");
        var betaAssistant = Assert.Single(betaMessages, message => message.Role == "assistant");

        Assert.Equal(1, Volatile.Read(ref firstFailureObserved));
        Assert.Equal(1, Volatile.Read(ref disruptedReadObserved));
        Assert.Equal(1, client.StopCount);
        Assert.Equal("Alpha recovered answer", alphaAssistant.Content);
        Assert.Equal("Beta recovered answer", betaAssistant.Content);
        Assert.DoesNotContain(alphaMessages, message => message.Content.Contains("Beta", StringComparison.Ordinal));
        Assert.DoesNotContain(betaMessages, message => message.Content.Contains("Alpha", StringComparison.Ordinal));
        Assert.Contains(client.StartedTurns, turn => turn == ("thread-alpha", "run alpha"));
        Assert.Contains(client.StartedTurns, turn => turn == ("thread-beta", "run beta"));
    }

    [Fact]
    public async Task PersistentReadFailureFailsTransparentlyAndClearsCurrentTask()
    {
        using var directory = new TestDirectory();
        var client = new FakeCodexSessionClient
        {
            ReadTurn = async (_, _, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return null;
            }
        };
        using var harness = await CreateHarnessAsync(
            directory,
            client,
            failuresBeforeRestart: 1,
            restartCycles: 1,
            readTimeout: TimeSpan.FromMilliseconds(15));

        await harness.Orchestrator.SubmitMessageAsync(
            harness.Session.Id,
            new SendMessageRequest("read forever"),
            CancellationToken.None);

        var session = await WaitForStateAsync(harness.Store, harness.Session.Id, SessionStates.Failed);
        var messages = await harness.Store.ReadMessagesAsync(harness.Session.Id);
        Assert.Null(session.CurrentTask);
        Assert.Equal(1, client.StopCount);
        Assert.Contains(
            messages,
            message => message.Role == "system" &&
                message.Phase == "error" &&
                message.Content.Contains("could not be recovered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HealthyInProgressSnapshotsHaveNoWholeTurnTimeout()
    {
        using var directory = new TestDirectory();
        var remaining = 20;
        var client = new FakeCodexSessionClient
        {
            ReadTurn = (_, _, _) => Task.FromResult<CodexTurnReadResult?>(
                Interlocked.Decrement(ref remaining) >= 0
                    ? InProgress()
                    : Completed("Long turn finished"))
        };
        using var harness = await CreateHarnessAsync(
            directory,
            client,
            failuresBeforeRestart: 1,
            restartCycles: 0,
            readTimeout: TimeSpan.FromMilliseconds(100));

        await harness.Orchestrator.SubmitMessageAsync(
            harness.Session.Id,
            new SendMessageRequest("perform a complex operation"),
            CancellationToken.None);

        await WaitForStateAsync(harness.Store, harness.Session.Id, SessionStates.Idle);
        Assert.Equal(0, client.StopCount);
        Assert.True(client.ReadCount >= 21);
    }

    [Fact]
    public async Task FailedSnapshotPersistsCodexErrorAndClearsCurrentTask()
    {
        using var directory = new TestDirectory();
        var client = new FakeCodexSessionClient
        {
            ReadTurn = (_, _, _) => Task.FromResult<CodexTurnReadResult?>(new CodexTurnReadResult(
                "turn-1",
                "failed",
                new CodexTurnError("quota exhausted", "retry later", null),
                []))
        };
        using var harness = await CreateHarnessAsync(directory, client);

        await harness.Orchestrator.SubmitMessageAsync(
            harness.Session.Id,
            new SendMessageRequest("read quota"),
            CancellationToken.None);

        var session = await WaitForStateAsync(harness.Store, harness.Session.Id, SessionStates.Failed);
        var messages = await harness.Store.ReadMessagesAsync(harness.Session.Id);
        Assert.Null(session.CurrentTask);
        Assert.Contains(
            messages,
            message => message.Role == "system" &&
                message.Phase == "error" &&
                message.Content.Contains("quota exhausted", StringComparison.Ordinal) &&
                message.Content.Contains("retry later", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LiveCompletionErrorIsPersistedWhenFinalReadCannotRecover()
    {
        using var directory = new TestDirectory();
        var client = new FakeCodexSessionClient
        {
            ReadTurn = (_, _, _) => throw new IOException("read loop closed")
        };
        client.TurnStarted = (_, turnId) => client.RaiseTurnCompletedAsync(
            turnId,
            "failed",
            new CodexTurnError("remote failed", "live details", null));
        using var harness = await CreateHarnessAsync(directory, client);

        await harness.Orchestrator.SubmitMessageAsync(
            harness.Session.Id,
            new SendMessageRequest("read live error"),
            CancellationToken.None);

        await WaitForStateAsync(harness.Store, harness.Session.Id, SessionStates.Failed);
        var messages = await harness.Store.ReadMessagesAsync(harness.Session.Id);
        Assert.Contains(
            messages,
            message => message.Role == "system" &&
                message.Content.Contains("remote failed", StringComparison.Ordinal) &&
                message.Content.Contains("live details", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompletedTurnWithoutRecoverableAssistantFailsExplicitly()
    {
        using var directory = new TestDirectory();
        var client = new FakeCodexSessionClient
        {
            ReadTurn = (_, _, _) => Task.FromResult<CodexTurnReadResult?>(new CodexTurnReadResult(
                "turn-1",
                "completed",
                null,
                []))
        };
        using var harness = await CreateHarnessAsync(directory, client);

        await harness.Orchestrator.SubmitMessageAsync(
            harness.Session.Id,
            new SendMessageRequest("return no answer"),
            CancellationToken.None);

        await WaitForStateAsync(harness.Store, harness.Session.Id, SessionStates.Failed);
        var messages = await harness.Store.ReadMessagesAsync(harness.Session.Id);
        Assert.Contains(
            messages,
            message => message.Role == "system" &&
                message.Content.Contains("could not recover an assistant response", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PauseWinsAgainstTerminalReconciliation()
    {
        using var directory = new TestDirectory();
        var interrupted = 0;
        var finalReadObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeCodexSessionClient();
        client.ReadTurn = (_, _, _) =>
        {
            if (Volatile.Read(ref interrupted) != 0)
            {
                finalReadObserved.TrySetResult();
                return Task.FromResult<CodexTurnReadResult?>(new CodexTurnReadResult(
                    "turn-1",
                    "interrupted",
                    null,
                    []));
            }
            return Task.FromResult<CodexTurnReadResult?>(InProgress());
        };
        client.TurnInterrupted = async (threadId, turnId) =>
        {
            Interlocked.Exchange(ref interrupted, 1);
            await client.RaiseTurnCompletedAsync(turnId, "interrupted", null);
        };
        using var harness = await CreateHarnessAsync(directory, client);

        await harness.Orchestrator.SubmitMessageAsync(
            harness.Session.Id,
            new SendMessageRequest("pause this turn"),
            CancellationToken.None);
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await harness.Orchestrator.SetSessionPausedAsync(harness.Session.Id, true, CancellationToken.None);
        await finalReadObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var session = await harness.Store.FindSessionAsync(harness.Session.Id);
        var messages = await harness.Store.ReadMessagesAsync(harness.Session.Id);
        Assert.Equal(SessionStates.Paused, session?.State);
        Assert.DoesNotContain(messages, message => message.Role == "system" && message.Phase == "error");
    }

    private static CodexTurnReadResult Completed(string text) =>
        new("turn-1", "completed", null, [new CodexAgentMessage("item-1", text, "final_answer")]);

    private static CodexTurnReadResult InProgress() =>
        new("turn-1", "inProgress", null, []);

    private static async Task<OrchestratorHarness> CreateHarnessAsync(
        TestDirectory directory,
        FakeCodexSessionClient client,
        int failuresBeforeRestart = 3,
        int restartCycles = 2,
        TimeSpan? readTimeout = null,
        int maxParallelTurns = 1,
        ISelectionContextSource? selectionContext = null)
    {
        var databasePath = directory.GetPath("runtime.db");
        var store = new SessionStore(databasePath);
        await store.InitializeAsync();
        var session = await store.CreateSessionAsync(new CreateSessionRequest(
            "Orchestrator test",
            ModelProfile: "standard",
            Model: "gpt-test"));
        var options = new AgentHostOptions
        {
            ProjectDirectory = directory.Path,
            MaxParallelTurns = maxParallelTurns,
            CodexTurnPollInterval = TimeSpan.FromMilliseconds(2),
            CodexTurnReadTimeout = readTimeout ?? TimeSpan.FromMilliseconds(250),
            CodexTurnReadFailuresBeforeRestart = failuresBeforeRestart,
            CodexTurnRestartCycles = restartCycles
        };
        var lifetime = new TestHostApplicationLifetime();
        var selector = new ModelSelector(client, NullLogger<ModelSelector>.Instance);
        var orchestrator = new SessionOrchestrator(
            store,
            client,
            selector,
            new MessageRoutingPolicy(),
            new EffectiveModelState(),
            options,
            new RuntimeControl(),
            new EventHub(),
            lifetime,
            NullLogger<SessionOrchestrator>.Instance,
            selectionContext);
        return new OrchestratorHarness(databasePath, store, session, orchestrator, lifetime);
    }

    private static async Task<SessionRecord> WaitForStateAsync(
        SessionStore store,
        Guid sessionId,
        string expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var session = await store.FindSessionAsync(sessionId, timeout.Token)
                ?? throw new InvalidOperationException("The test session disappeared.");
            if (session.State == expected)
            {
                return session;
            }
            await Task.Delay(5, timeout.Token);
        }
    }

    private static async Task<string?> ReadAssistantClientMessageIdAsync(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT client_message_id FROM messages WHERE role='assistant' LIMIT 1;";
        return (string?)await command.ExecuteScalarAsync();
    }

    private sealed record OrchestratorHarness(
        string DatabasePath,
        SessionStore Store,
        SessionRecord Session,
        SessionOrchestrator Orchestrator,
        TestHostApplicationLifetime Lifetime) : IDisposable
    {
        public void Dispose()
        {
            Lifetime.StopApplication();
            Orchestrator.Dispose();
        }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => _stopped.Token;

        public void StopApplication()
        {
            _stopping.Cancel();
            _stopped.Cancel();
        }
    }

    private sealed class FakeCodexSessionClient : ICodexSessionClient, IModelCatalog
    {
        private readonly object _startedTurnsGate = new();
        private readonly List<(string ThreadId, string Message)> _startedTurns = [];
        private int _readCount;
        private int _stopCount;
        private int _startThreadCount;
        private int _startTurnCount;

        public event Func<string, JsonElement, Task>? NotificationReceived;

        public bool IsRunning { get; private set; } = true;

        public int ReadCount => Volatile.Read(ref _readCount);

        public int StopCount => Volatile.Read(ref _stopCount);

        public int StartThreadCount => Volatile.Read(ref _startThreadCount);

        public int StartTurnCount => Volatile.Read(ref _startTurnCount);

        public string ThreadToStart { get; set; } = "thread-1";

        public IReadOnlyList<(string ThreadId, string Message)> StartedTurns
        {
            get
            {
                lock (_startedTurnsGate)
                {
                    return [.. _startedTurns];
                }
            }
        }

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Stopped { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<string, string, CancellationToken, Task<CodexTurnReadResult?>> ReadTurn { get; set; } =
            (_, _, _) => Task.FromResult<CodexTurnReadResult?>(InProgress());

        public Func<string, string, Task>? TurnStarted { get; set; }

        public Func<string, string, Task>? TurnInterrupted { get; set; }

        public Func<string, string, string?, CancellationToken, Task>? ResumeThread { get; set; }

        public Func<string, string, string?, string?, CancellationToken, Task<string>>? StartTurn { get; set; }

        public Task<IReadOnlyList<ModelView>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelView>>([
                new ModelView(
                    "gpt-test",
                    "gpt-test",
                    "GPT Test",
                    "Test model",
                    true,
                    ["low", "medium", "high"])
            ]);

        public Task<string> StartThreadAsync(
            string cwd,
            string? model,
            CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            Interlocked.Increment(ref _startThreadCount);
            return Task.FromResult(ThreadToStart);
        }

        public async Task ResumeThreadAsync(
            string threadId,
            string cwd,
            string? model,
            CancellationToken cancellationToken = default)
        {
            if (ResumeThread is not null)
            {
                await ResumeThread(threadId, cwd, model, cancellationToken);
            }
        }

        public async Task<string> StartTurnAsync(
            string threadId,
            string message,
            string? model,
            string? effort,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _startTurnCount);
            lock (_startedTurnsGate)
            {
                _startedTurns.Add((threadId, message));
            }
            var turnId = StartTurn is null
                ? "turn-1"
                : await StartTurn(threadId, message, model, effort, cancellationToken);
            Started.TrySetResult();
            if (TurnStarted is not null)
            {
                await TurnStarted(threadId, turnId);
            }
            return turnId;
        }

        public async Task InterruptTurnAsync(
            string threadId,
            string turnId,
            CancellationToken cancellationToken = default)
        {
            if (TurnInterrupted is not null)
            {
                await TurnInterrupted(threadId, turnId);
            }
        }

        public Task<CodexTurnReadResult?> ReadTurnAsync(
            string threadId,
            string turnId,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readCount);
            IsRunning = true;
            return ReadTurn(threadId, turnId, cancellationToken);
        }

        public Task StopAsync()
        {
            Interlocked.Increment(ref _stopCount);
            IsRunning = false;
            Stopped.TrySetResult();
            return Task.CompletedTask;
        }

        public Task RaiseItemCompletedAsync(
            string threadId,
            string turnId,
            string text,
            string? phase) => RaiseAsync(
                "item/completed",
                JsonSerializer.SerializeToElement(new
                {
                    threadId,
                    turnId,
                    item = new { id = "item-1", type = "agentMessage", text, phase }
                }));

        public Task RaiseTurnCompletedAsync(string turnId, string status, CodexTurnError? error) => RaiseAsync(
            "turn/completed",
            JsonSerializer.SerializeToElement(new
            {
                turn = new
                {
                    id = turnId,
                    status,
                    error = error is null
                        ? null
                        : new
                        {
                            message = error.Message,
                            additionalDetails = error.AdditionalDetails,
                            codexErrorInfo = error.CodexErrorInfo
                        }
                }
            }));

        private async Task RaiseAsync(string method, JsonElement parameters)
        {
            var handlers = NotificationReceived;
            if (handlers is null)
            {
                return;
            }
            foreach (var handler in handlers.GetInvocationList().Cast<Func<string, JsonElement, Task>>())
            {
                await handler(method, parameters);
            }
        }
    }
}
