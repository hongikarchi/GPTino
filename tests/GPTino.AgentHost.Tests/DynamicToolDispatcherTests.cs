using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Codex;
using GPTino.AgentHost.Data;
using GPTino.AgentHost.Hosting;
using GPTino.AgentHost.Runtime;
using GPTino.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

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
        if (role == "planner")
        {
            // The planner denial teaches the mode by name and the way out, instead of the bare
            // role-permission error the model only understood after wasting a full authoring turn.
            Assert.Contains("plan mode", result.Text, StringComparison.Ordinal);
            Assert.Contains("change_submit is disabled by design", result.Text, StringComparison.Ordinal);
            Assert.Contains("Present the plan to the user", result.Text, StringComparison.Ordinal);
            Assert.Contains("switch this session to auto", result.Text, StringComparison.Ordinal);
        }
        else
        {
            // A role denial must not promise that a mode flip would help — the role is permanent.
            Assert.Contains("cannot submit live changes", result.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("session that can write", result.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("switch this session to auto", result.Text, StringComparison.Ordinal);
        }
        Assert.Equal(0, backend.SubmitCount);

        // Role denials create no job; the problem log is the only structured record of them.
        var logPath = Path.Combine(directory.GetPath("data"), "problem-log.jsonl");
        Assert.True(File.Exists(logPath), "Role denial did not write a problem-log row.");
        var rows = (await File.ReadAllLinesAsync(logPath))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
        var denial = Assert.Single(rows, row => row.GetProperty("kind").GetString() == "role-denial");
        Assert.Equal(session.Id, denial.GetProperty("sessionId").GetGuid());
        // Plan mode gates the write (the 'planner' role is only a creation alias now), so the log
        // names the mode, not a role — "modeler denied change_submit" would read as nonsense.
        Assert.Equal(role == "planner" ? "plan-mode" : role, denial.GetProperty("role").GetString());
        Assert.Equal("change_submit", denial.GetProperty("tool").GetString());
        Assert.Equal(result.Text, denial.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ReadOnlyRoleOutranksPlanModeInDenialMessageAndProblemLog()
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, backend) = await CreateDispatcherAsync(directory);
        // The overlap state is reachable: nothing gates PUT /mode by role, so a read-only session
        // can sit in plan mode. The denial must not claim that switching to auto would let the
        // write through (it would not), and the problem log must name the permanent denier.
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Viewer", "read-only"));
        await store.SetModeAsync(session.Id, "plan");
        await store.SetThreadIdAsync(session.Id, "viewer-thread");

        var result = await dispatcher.DispatchAsync(
            Call("change_submit", "{}", threadId: "viewer-thread"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("read-only", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("switch this session to auto", result.Text, StringComparison.Ordinal);
        Assert.Equal(0, backend.SubmitCount);

        var logPath = Path.Combine(directory.GetPath("data"), "problem-log.jsonl");
        var rows = (await File.ReadAllLinesAsync(logPath))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();
        var denial = Assert.Single(rows, row => row.GetProperty("kind").GetString() == "role-denial");
        Assert.Equal("read-only", denial.GetProperty("role").GetString());
    }

    [Fact]
    public async Task CuratorSubmitsInAutoModeAndIsGatedOnlyWhileInPlanMode()
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, backend) = await CreateDispatcherAsync(directory);
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Doc care", "curator"));
        await store.SetThreadIdAsync(session.Id, "curator-thread");

        // Role and mode are orthogonal: a curator writes through the same broker as a modeler.
        var allowed = await dispatcher.DispatchAsync(
            Call("change_submit", """{"summary":"Purge unused"}""", threadId: "curator-thread"),
            CancellationToken.None);
        Assert.True(allowed.Success, allowed.Text);
        Assert.Equal(1, backend.SubmitCount);
        Assert.Equal("curator", backend.SubmittedSession?.Role);

        // Flipping to plan mode gates the write without erasing the curatorship...
        await store.SetModeAsync(session.Id, "plan");
        var gated = await dispatcher.DispatchAsync(
            Call("change_submit", """{"summary":"Purge unused"}""", threadId: "curator-thread"),
            CancellationToken.None);
        Assert.False(gated.Success);
        Assert.Contains("plan mode", gated.Text, StringComparison.Ordinal);
        Assert.Equal(1, backend.SubmitCount);

        // ...and flipping back restores the write path with the role intact.
        await store.SetModeAsync(session.Id, "auto");
        var restored = await dispatcher.DispatchAsync(
            Call("change_submit", """{"summary":"Purge unused"}""", threadId: "curator-thread"),
            CancellationToken.None);
        Assert.True(restored.Success, restored.Text);
        Assert.Equal(2, backend.SubmitCount);
        Assert.Equal("curator", backend.SubmittedSession?.Role);
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
        var problems = new ProblemLog(options, NullLogger<ProblemLog>.Instance);
        return (new DynamicToolDispatcher(store, backend, options, problems: problems), store, backend);
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

        public Task<object> InspectCanvasOutputsAsync(
            SessionRecord session,
            JsonElement arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { outputs = Array.Empty<object>(), sessionId = session.Id });

        public Task<object> InspectCanvasOutputsAsync(
            JsonElement arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { outputs = Array.Empty<object>() });

        public Task<object> SubmitChangeAsync(
            SessionRecord session,
            JsonElement arguments,
            CancellationToken cancellationToken)
        {
            SubmitCount++;
            SubmittedSession = session;
            return Task.FromResult<object>(new { jobId = "job-1" });
        }

        public Task<object> ArrangeLayoutAsync(
            SessionRecord session,
            JsonElement arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { status = "already-tidy", moved = 0 });

        public Task<object> ReadJobAsync(JsonElement arguments, CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { state = "queued" });

        public Task StopCurrentAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
