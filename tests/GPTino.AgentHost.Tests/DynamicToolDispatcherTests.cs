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

    /// <summary>
    /// Session roles and plan mode are gone; pause is the only thing between a session and the
    /// broker. This pins that the write path still HAS a gate (the removal must not have taken the
    /// surviving one with it) and that the gate reverses.
    /// </summary>
    [Theory]
    [InlineData("change_submit")]
    [InlineData("arrange_layout")]
    public async Task WriteToolsAreGatedByPauseAlone(string tool)
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, backend) = await CreateDispatcherAsync(directory);
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Modeling"));
        await store.SetThreadIdAsync(session.Id, "write-thread");

        var allowed = await dispatcher.DispatchAsync(
            Call(tool, """{"summary":"Move point"}""", threadId: "write-thread"),
            CancellationToken.None);
        Assert.True(allowed.Success, allowed.Text);

        await store.SetSessionStateAsync(session.Id, SessionStates.Paused);
        var paused = await dispatcher.DispatchAsync(
            Call(tool, """{"summary":"Move point"}""", threadId: "write-thread"),
            CancellationToken.None);
        Assert.False(paused.Success);
        Assert.Contains("paused", paused.Text, StringComparison.OrdinalIgnoreCase);

        await store.SetSessionStateAsync(session.Id, SessionStates.Idle);
        var resumed = await dispatcher.DispatchAsync(
            Call(tool, """{"summary":"Move point"}""", threadId: "write-thread"),
            CancellationToken.None);
        Assert.True(resumed.Success, resumed.Text);
    }

    [Fact]
    public async Task ChangeSubmitForwardsBoundSession()
    {
        using var directory = new TestDirectory();
        var (dispatcher, store, backend) = await CreateDispatcherAsync(directory);
        var session = await store.CreateSessionAsync(new CreateSessionRequest("Modeler"));
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
        CreateDispatcherAsync(TestDirectory directory, DataLibrary? data = null)
    {
        var store = new SessionStore(directory.GetPath("state.db"));
        await store.InitializeAsync();
        var backend = new FakeLiveDocumentBackend();
        var options = new AgentHostOptions { DataDirectory = directory.GetPath("data") };
        var problems = new ProblemLog(options, NullLogger<ProblemLog>.Instance);
        return (new DynamicToolDispatcher(store, backend, options, problems: problems, data: data), store, backend);
    }

    /// <summary>
    /// structural_extract composes the model-facing SUMMARY: full member list to a session
    /// artifact (never the tool result), section identity matched dispatcher-side against the
    /// shipped KS catalog (÷1.02 of the prototype outer dims), and free ends carried WITH their
    /// source object ids because they are the ask-back items.
    /// </summary>
    [Fact]
    public async Task StructuralExtractSummarizesMatchesSectionsAndWritesTheMembersArtifact()
    {
        using var directory = new TestDirectory();
        var dataRoot = directory.GetPath("shipped-data");
        Directory.CreateDirectory(Path.Combine(dataRoot, "structural"));
        await File.WriteAllTextAsync(
            Path.Combine(dataRoot, "structural", "sections-ks.json"),
            """
            {"sections":[
              {"name":"H-300x300x10x15","H":300,"B":300},
              {"name":"H-400x200x8x13","H":400,"B":200}
            ]}
            """);
        var (dispatcher, store, _) = await CreateDispatcherAsync(directory, new DataLibrary(dataRoot));
        var session = await BindSessionAsync(store, "structural-thread");

        var result = await dispatcher.DispatchAsync(
            Call("structural_extract", """{"layerFilter":"철골"}""", threadId: "structural-thread"),
            CancellationToken.None);

        Assert.True(result.Success, result.Text);
        using var payload = JsonDocument.Parse(result.Text);
        var root = payload.RootElement;
        Assert.Equal(1, root.GetProperty("memberCount").GetInt32());
        Assert.Equal(2, root.GetProperty("mergedDuplicateAxes").GetInt32());
        // 306 / 1.02 = 300 exactly → the H-300x300 row wins with zero error.
        var guess = root.GetProperty("sectionGuesses").GetProperty("SC1");
        Assert.Equal("H-300x300x10x15", guess.GetProperty("section").GetString());
        Assert.Equal(0.0, guess.GetProperty("errorMm").GetDouble());
        // The free end arrives with its source object id — the ask-back needs a focusable target.
        var freeEnd = Assert.Single(root.GetProperty("freeEnds").EnumerateArray().ToArray());
        Assert.Equal(
            "a0b1c2d3-0001-4e4e-9f9f-000000000001",
            freeEnd.GetProperty("sourceObjectIds")[0].GetString());
        // The artifact holds the FULL extraction; the summary only points at it.
        var artifactPath = root.GetProperty("membersArtifact").GetString();
        Assert.Equal("structural/members.json", artifactPath);
        var stored = await File.ReadAllTextAsync(
            directory.GetPath($"data/artifacts/{session.Id:N}/structural/members.json"));
        using var artifact = JsonDocument.Parse(stored);
        Assert.Equal(
            "SC1",
            artifact.RootElement.GetProperty("extraction").GetProperty("members")[0].GetProperty("mark").GetString());
        Assert.DoesNotContain("\"members\":", result.Text.Replace(" ", ""));
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

        public Task<object> ReadDataFlowAsync(SessionRecord session, CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { docId = "test", references = new { }, bakes = new { } });

        public Task<object> ReadRhinoAuditAsync(JsonElement arguments, CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { kind = "purgeCandidates", findings = Array.Empty<object>() });

        // Mirrors the backend's { result, fingerprint, diagnostics } bridge-read wrapper with one
        // instance member whose prototype dims are KS nominal × 1.02 (H-300x300 → 306) and one
        // free end, so the dispatcher's section matching and summary composition are exercised.
        public Task<object> ReadStructuralExtractAsync(JsonElement arguments, CancellationToken cancellationToken) =>
            Task.FromResult<object>(new
            {
                result = new
                {
                    docUnits = "Millimeters",
                    scannedObjects = 3,
                    members = new object[]
                    {
                        new
                        {
                            mark = "SC1",
                            layer = "철골::SC1",
                            a = new { x = 0.0, y = 0.0, z = 0.0 },
                            b = new { x = 0.0, y = 0.0, z = 3000.0 },
                            length = 3000.0,
                            kind = "instance",
                            sourceObjectIds = new[] { "a0b1c2d3-0001-4e4e-9f9f-000000000001" },
                            fingerprints = new[] { "fp-sc1" },
                        },
                    },
                    prototypes = new object[]
                    {
                        new { layer = "철골::SC1", mark = "SC1", outerX = 306.0, outerY = 306.0 },
                    },
                    freeEnds = new object[]
                    {
                        new
                        {
                            memberIndex = 0,
                            end = 1,
                            point = new { x = 0.0, y = 0.0, z = 3000.0 },
                            sourceObjectIds = new[] { "a0b1c2d3-0001-4e4e-9f9f-000000000001" },
                        },
                    },
                    mergedDuplicateAxes = 2,
                    obliqueExactAxes = 0,
                    skippedByReason = new Dictionary<string, int> { ["skipped:Mesh"] = 1 },
                    truncated = false,
                    fingerprint = "extract-fp",
                },
                fingerprint = "extract-fp",
                diagnostics = Array.Empty<object>(),
            });

        public Task<object> ReadRhinoLayersAsync(CancellationToken cancellationToken) =>
            Task.FromResult<object>(new { layers = Array.Empty<object>(), namedLayerStates = Array.Empty<string>() });

        public Task StopCurrentAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

