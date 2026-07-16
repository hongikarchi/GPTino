using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Codex;
using GPTino.AgentHost.Data;
using GPTino.AgentHost.Hosting;
using GPTino.Contracts;

namespace GPTino.AgentHost.Tests;

public sealed class DynamicToolDispatcherTests
{
    [Fact]
    public async Task ArtifactWriteAndReadRoundTripWithinManagedStorage()
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, _) = await CreateDispatcherAsync(directory);
        var session = await BindSessionAsync(store, "thread");

        var written = await dispatcher.DispatchAsync(
            Call("artifact_write", """{"path":"drafts/component.py","content":"print('ok')"}"""),
            CancellationToken.None);
        var read = await dispatcher.DispatchAsync(
            Call("artifact_read", """{"path":"drafts/component.py"}"""),
            CancellationToken.None);

        Assert.True(written.Success, written.Text);
        Assert.True(read.Success, read.Text);
        using var writePayload = JsonDocument.Parse(written.Text);
        using var readPayload = JsonDocument.Parse(read.Text);
        Assert.Equal("drafts/component.py", writePayload.RootElement.GetProperty("path").GetString());
        Assert.False(writePayload.RootElement.GetProperty("liveDocumentChanged").GetBoolean());
        Assert.Equal("drafts/component.py", readPayload.RootElement.GetProperty("path").GetString());
        Assert.Equal("print('ok')", readPayload.RootElement.GetProperty("content").GetString());
        Assert.Equal(
            "print('ok')",
            await File.ReadAllTextAsync(directory.GetPath(
                $"data/artifacts/{session.Id:N}/drafts/component.py")));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("nested/../../outside.txt")]
    public async Task ArtifactWriteRejectsTraversalWithoutCreatingOutsideFile(string path)
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, _) = await CreateDispatcherAsync(directory);
        await BindSessionAsync(store, "thread");
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { path, content = "escape" }));

        var result = await dispatcher.DispatchAsync(
            new DynamicToolCall("call", "thread", "turn", "gptino_v1", "artifact_write", arguments.RootElement.Clone()),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("escapes managed storage", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(directory.GetPath("outside.txt")));
    }

    [Theory]
    [InlineData(".gptino-reserved/jobs/abc/operations/0000.json")]
    [InlineData("drafts/../.gptino-reserved/payload.json")]
    public async Task ArtifactWriteRejectsBrokerReservedNamespace(string path)
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, _) = await CreateDispatcherAsync(directory);
        await BindSessionAsync(store, "thread");
        using var arguments = JsonDocument.Parse(JsonSerializer.Serialize(new { path, content = "{}" }));

        var result = await dispatcher.DispatchAsync(
            new DynamicToolCall("call", "thread", "turn", "gptino_v1", "artifact_write", arguments.RootElement.Clone()),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("reserved", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("planner")]
    [InlineData("read-only")]
    public async Task ChangeSubmitRejectsReadOnlyRolesBeforeCallingBackend(string role)
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, backend) = await CreateDispatcherAsync(directory);
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Restricted", role));
        await store.SetThreadIdAsync(session.Id, "restricted-thread");

        var result = await dispatcher.DispatchAsync(
            Call("change_submit", "{}", threadId: "restricted-thread"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("cannot submit live changes", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, backend.SubmitCount);
    }

    [Fact]
    public async Task ChangeSubmitAllowsModelerAndForwardsBoundSession()
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, backend) = await CreateDispatcherAsync(directory);
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Modeler", "modeler"));
        await store.SetThreadIdAsync(session.Id, "modeler-thread");

        var result = await dispatcher.DispatchAsync(
            Call("change_submit", """{"summary":"Move point"}""", threadId: "modeler-thread"),
            CancellationToken.None);

        Assert.True(result.Success, result.Text);
        Assert.Equal(1, backend.SubmitCount);
        Assert.Equal(session.Id, backend.SubmittedSession?.Id);
    }

    [Theory]
    [InlineData("component_catalog", "{\"query\":\"point\"}", "matches")]
    [InlineData("rhino_list", "{\"limit\":10}", "objects")]
    public async Task ReadOnlyDiscoveryToolsForwardToLiveBackend(
        string tool,
        string arguments,
        string expectedProperty)
    {
        using var directory = new TestDirectory();
        var (dispatcher, _, _) = await CreateDispatcherAsync(directory);

        var result = await dispatcher.DispatchAsync(
            Call(tool, arguments, threadId: "unbound-read-thread"),
            CancellationToken.None);

        Assert.True(result.Success, result.Text);
        using var payload = JsonDocument.Parse(result.Text);
        Assert.True(payload.RootElement.TryGetProperty(expectedProperty, out _));
    }

    [Fact]
    public async Task SnapshotReadReturnsCallingSessionIdentityForChangeSetBinding()
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, _) = await CreateDispatcherAsync(directory);
        var session = await BindSessionAsync(store, "snapshot-thread");

        var result = await dispatcher.DispatchAsync(
            Call("snapshot_read", "{}", threadId: "snapshot-thread"),
            CancellationToken.None);

        Assert.True(result.Success, result.Text);
        using var payload = JsonDocument.Parse(result.Text);
        Assert.Equal(
            session.Id,
            payload.RootElement.GetProperty("sessionId").GetGuid());
    }

    private static async Task<(DynamicToolDispatcher Dispatcher, SessionStore Store, FakeLiveDocumentBackend Backend)>
        CreateDispatcherAsync(TestDirectory directory)
    {
        var store = new SessionStore(directory.GetPath("state.db"));
        await store.InitializeAsync();
        var backend = new FakeLiveDocumentBackend();
        var options = new AgentHostOptions { DataDirectory = directory.GetPath("data") };
        return (new DynamicToolDispatcher(store, backend, options), store, backend);
    }

    private static async Task<SessionRecord> BindSessionAsync(SessionStore store, string threadId)
    {
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Artifacts"));
        await store.SetThreadIdAsync(session.Id, threadId);
        return session;
    }

    private static DynamicToolCall Call(string tool, string arguments, string threadId = "thread")
    {
        using var document = JsonDocument.Parse(arguments);
        return new DynamicToolCall(
            Guid.NewGuid().ToString("N"),
            threadId,
            "turn",
            "gptino_v1",
            tool,
            document.RootElement.Clone());
    }

    private sealed class FakeLiveDocumentBackend : ILiveDocumentBackend
    {
        public bool IsConnected => true;

        public DocumentRuntime? CurrentTarget => null;

        public int QueueLength => 0;

        public string? WriterSessionId => null;

        public int SubmitCount { get; private set; }

        public SessionRecord? SubmittedSession { get; private set; }

        public Task<object> ReadSnapshotAsync(
            SessionRecord session,
            JsonElement arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { sessionId = session.Id, snapshotId = "snapshot-1" });

        public Task<object> SearchComponentCatalogAsync(
            JsonElement arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { matches = Array.Empty<object>() });

        public Task<object> ListRhinoObjectsAsync(
            JsonElement arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { objects = Array.Empty<object>() });

        public Task<object> SubmitChangeAsync(
            SessionRecord session,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            SubmitCount++;
            SubmittedSession = session;
            return Task.FromResult<object>(new { jobId = "job-1" });
        }

        public Task<object> ReadJobAsync(JsonElement arguments, CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { state = "queued" });

        public Task StopCurrentAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
