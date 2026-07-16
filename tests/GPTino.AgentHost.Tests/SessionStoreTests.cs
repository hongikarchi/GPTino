using GPTino.AgentHost.Api;
using GPTino.AgentHost.Data;

namespace GPTino.AgentHost.Tests;

public sealed class SessionStoreTests
{
    [Fact]
    public async Task InitializeAndCreatePersistsNormalizedSessionsInInsertionOrder()
    {
        using var directory = new TestDirectory();
        var databasePath = directory.GetPath("state/sessions.db");
        var store = new SessionStore(databasePath);

        await store.InitializeAsync();
        var first = await store.CreateSessionAsync(new CreateSessionRequest("  Facade Study  ", " PLANNER ", " FAST "));
        var second = await store.CreateSessionAsync(new CreateSessionRequest("Detailing"));

        var (sessions, orderVersion) = await store.ReadStateAsync();
        Assert.Equal(0, orderVersion);
        Assert.Collection(
            sessions,
            session =>
            {
                Assert.Equal(first.Id, session.Id);
                Assert.Equal("Facade Study", session.Name);
                Assert.Equal("planner", session.Role);
                Assert.Equal("fast", session.ModelProfile);
                Assert.Equal(0, session.Order);
                Assert.Equal(SessionStates.Idle, session.State);
            },
            session =>
            {
                Assert.Equal(second.Id, session.Id);
                Assert.Equal("modeler", session.Role);
                Assert.Equal("auto", session.ModelProfile);
                Assert.Equal(1, session.Order);
            });

        var reopened = new SessionStore(databasePath);
        await reopened.InitializeAsync();
        var (reloaded, reloadedVersion) = await reopened.ReadStateAsync();
        Assert.Equal(orderVersion, reloadedVersion);
        Assert.Equal(sessions.Select(session => session.Id), reloaded.Select(session => session.Id));
    }

    [Fact]
    public async Task ReorderUsesCompareAndSwapVersionAndRejectsStaleOrder()
    {
        using var directory = new TestDirectory();
        var store = await CreateInitializedStoreAsync(directory);
        var first = await store.CreateSessionAsync(new CreateSessionRequest("One"));
        var second = await store.CreateSessionAsync(new CreateSessionRequest("Two"));

        var nextVersion = await store.ReorderAsync([second.Id, first.Id], expectedVersion: 0);
        var stale = await Assert.ThrowsAsync<SessionOrderConcurrencyException>(
            () => store.ReorderAsync([first.Id, second.Id], expectedVersion: 0));

        Assert.Equal(1, nextVersion);
        Assert.Equal(0, stale.Expected);
        Assert.Equal(1, stale.Actual);
        var (sessions, orderVersion) = await store.ReadStateAsync();
        Assert.Equal(1, orderVersion);
        Assert.Equal([second.Id, first.Id], sessions.Select(session => session.Id));
    }

    [Fact]
    public async Task ConcurrentReordersWithSameVersionAllowExactlyOneWinner()
    {
        using var directory = new TestDirectory();
        var store = await CreateInitializedStoreAsync(directory);
        var first = await store.CreateSessionAsync(new CreateSessionRequest("One"));
        var second = await store.CreateSessionAsync(new CreateSessionRequest("Two"));
        var third = await store.CreateSessionAsync(new CreateSessionRequest("Three"));
        using var start = new ManualResetEventSlim(false);

        var ascending = Task.Run(async () =>
        {
            start.Wait();
            return await TryReorderAsync(store, [first.Id, second.Id, third.Id]);
        });
        var descending = Task.Run(async () =>
        {
            start.Wait();
            return await TryReorderAsync(store, [third.Id, second.Id, first.Id]);
        });

        start.Set();
        var outcomes = await Task.WhenAll(ascending, descending);
        Assert.Single(outcomes, outcome => outcome.Applied);
        var loser = Assert.Single(outcomes, outcome => !outcome.Applied);
        Assert.IsType<SessionOrderConcurrencyException>(loser.Error);

        var (sessions, orderVersion) = await store.ReadStateAsync();
        Assert.Equal(1, orderVersion);
        var winningOrder = Assert.Single(outcomes, outcome => outcome.Applied).Order;
        Assert.Equal(winningOrder, sessions.Select(session => session.Id));
    }

    [Fact]
    public async Task DuplicateClientMessageIdPersistsOnlyTheOriginalMessage()
    {
        using var directory = new TestDirectory();
        var store = await CreateInitializedStoreAsync(directory);
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Chat"));

        var first = await store.AppendMessageOnceAsync(
            session.Id,
            "user",
            "Move the point",
            phase: "request",
            clientMessageId: "browser-message-7");
        var duplicate = await store.AppendMessageOnceAsync(
            session.Id,
            "assistant",
            "This retry must not create a second row",
            phase: "retry",
            clientMessageId: "browser-message-7");

        var messages = await store.ReadMessagesAsync(session.Id);
        var persisted = Assert.Single(messages);
        Assert.True(first.Created);
        Assert.False(duplicate.Created);
        Assert.Equal(first.Message.Id, duplicate.Message.Id);
        Assert.Equal(first.Message.Content, duplicate.Message.Content);
        Assert.Equal(first.Message.Role, duplicate.Message.Role);
        Assert.Equal(first.Message.Phase, duplicate.Message.Phase);
        Assert.Equal(first.Message.CreatedAt, duplicate.Message.CreatedAt);
        Assert.Equal(first.Message.Id, persisted.Id);
        Assert.Equal("Move the point", persisted.Content);
        Assert.Equal("user", persisted.Role);
        Assert.Equal("request", persisted.Phase);
        Assert.Equal(first.Message.CreatedAt, persisted.CreatedAt);
    }

    [Fact]
    public async Task MessagePaginationLoadsNewestInitialWindowAndOldestIncrementalWindow()
    {
        using var directory = new TestDirectory();
        var store = await CreateInitializedStoreAsync(directory);
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Long chat"));
        var appended = new List<ChatMessage>();
        for (var index = 1; index <= 6; index++)
        {
            appended.Add(await store.AppendMessageAsync(session.Id, "user", $"message-{index}"));
        }

        var initial = await store.ReadMessagesAsync(session.Id, after: 0, limit: 3);
        var incremental = await store.ReadMessagesAsync(session.Id, after: appended[1].Id, limit: 2);

        Assert.Equal(["message-4", "message-5", "message-6"], initial.Select(message => message.Content));
        Assert.Equal(appended.Skip(3).Select(message => message.Id), initial.Select(message => message.Id));
        Assert.Equal(["message-3", "message-4"], incremental.Select(message => message.Content));
        Assert.Equal(appended.Skip(2).Take(2).Select(message => message.Id), incremental.Select(message => message.Id));
    }

    [Fact]
    public async Task InitializeAtomicallyMarksInterruptedSessionsForRecoveryOnlyOnce()
    {
        using var directory = new TestDirectory();
        var databasePath = directory.GetPath("restart/sessions.db");
        var original = new SessionStore(databasePath);
        await original.InitializeAsync();
        var running = await original.CreateSessionAsync(new CreateSessionRequest("Running"));
        var waiting = await original.CreateSessionAsync(new CreateSessionRequest("Waiting"));
        var idle = await original.CreateSessionAsync(new CreateSessionRequest("Idle"));
        await original.SetSessionStateAsync(running.Id, SessionStates.Running, "Apply facade changes");
        await original.SetSessionStateAsync(waiting.Id, SessionStates.Waiting, "Waiting for writer");

        var restarted = new SessionStore(databasePath);
        await restarted.InitializeAsync();
        var (sessions, _) = await restarted.ReadStateAsync();

        Assert.Collection(
            sessions,
            session =>
            {
                Assert.Equal(running.Id, session.Id);
                Assert.Equal(SessionStates.Failed, session.State);
                Assert.Null(session.CurrentTask);
            },
            session =>
            {
                Assert.Equal(waiting.Id, session.Id);
                Assert.Equal(SessionStates.Failed, session.State);
                Assert.Null(session.CurrentTask);
            },
            session =>
            {
                Assert.Equal(idle.Id, session.Id);
                Assert.Equal(SessionStates.Idle, session.State);
            });

        foreach (var interruptedId in new[] { running.Id, waiting.Id })
        {
            var message = Assert.Single(await restarted.ReadMessagesAsync(interruptedId));
            Assert.Equal("system", message.Role);
            Assert.Equal("recovery", message.Phase);
            Assert.Contains("AgentHost restart", message.Content, StringComparison.Ordinal);
        }
        Assert.Empty(await restarted.ReadMessagesAsync(idle.Id));

        var initializedAgain = new SessionStore(databasePath);
        await initializedAgain.InitializeAsync();
        Assert.Single(await initializedAgain.ReadMessagesAsync(running.Id));
        Assert.Single(await initializedAgain.ReadMessagesAsync(waiting.Id));
    }

    private static async Task<SessionStore> CreateInitializedStoreAsync(TestDirectory directory)
    {
        var store = new SessionStore(directory.GetPath("sessions.db"));
        await store.InitializeAsync();
        return store;
    }

    private static async Task<ReorderOutcome> TryReorderAsync(
        SessionStore store,
        IReadOnlyList<Guid> order)
    {
        try
        {
            await store.ReorderAsync(order, expectedVersion: 0);
            return new ReorderOutcome(true, order, null);
        }
        catch (SessionOrderConcurrencyException exception)
        {
            return new ReorderOutcome(false, order, exception);
        }
    }

    private sealed record ReorderOutcome(
        bool Applied,
        IReadOnlyList<Guid> Order,
        Exception? Error);
}
