using System.Text;
using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Data;
using GPTino.AgentHost.Hosting;
using GPTino.AgentHost.Runtime;
using GPTino.AgentHost.Security;
using GPTino.Contracts;

namespace GPTino.AgentHost.Codex;

public interface ILiveDocumentBackend
{
    bool IsConnected { get; }

    DocumentRuntime? CurrentTarget { get; }

    int QueueLength { get; }

    string? WriterSessionId { get; }

    Task<object> ReadSnapshotAsync(
        SessionRecord session,
        JsonElement arguments,
        CancellationToken cancellationToken);

    Task<object> SearchComponentCatalogAsync(JsonElement arguments, CancellationToken cancellationToken);

    Task<object> ListRhinoObjectsAsync(JsonElement arguments, CancellationToken cancellationToken);

    Task<object> InspectCanvasOutputsAsync(
        SessionRecord session,
        JsonElement arguments,
        CancellationToken cancellationToken);

    Task<object> InspectCanvasOutputsAsync(JsonElement arguments, CancellationToken cancellationToken);

    Task<object> SubmitChangeAsync(SessionRecord session, JsonElement arguments, CancellationToken cancellationToken);

    Task<object> ReadJobAsync(JsonElement arguments, CancellationToken cancellationToken);

    Task StopCurrentAsync(CancellationToken cancellationToken);
}

public sealed class DisconnectedDocumentBackend : ILiveDocumentBackend
{
    public bool IsConnected => false;

    public DocumentRuntime? CurrentTarget => null;

    public int QueueLength => 0;

    public string? WriterSessionId => null;

    public Task<object> ReadSnapshotAsync(
        SessionRecord session,
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        Task.FromException<object>(new InvalidOperationException("The Rhino/Grasshopper bridge is not connected."));

    public Task<object> SearchComponentCatalogAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        Task.FromException<object>(new InvalidOperationException("The Rhino/Grasshopper bridge is not connected."));

    public Task<object> ListRhinoObjectsAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        Task.FromException<object>(new InvalidOperationException("The Rhino/Grasshopper bridge is not connected."));

    public Task<object> InspectCanvasOutputsAsync(
        SessionRecord session,
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        Task.FromException<object>(new InvalidOperationException("The Rhino/Grasshopper bridge is not connected."));

    public Task<object> InspectCanvasOutputsAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        Task.FromException<object>(new InvalidOperationException("The Rhino/Grasshopper bridge is not connected."));

    public Task<object> SubmitChangeAsync(SessionRecord session, JsonElement arguments, CancellationToken cancellationToken) =>
        Task.FromException<object>(new InvalidOperationException("The Rhino/Grasshopper bridge is not connected."));

    public Task<object> ReadJobAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        Task.FromException<object>(new InvalidOperationException("The Rhino/Grasshopper bridge is not connected."));

    public Task StopCurrentAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class DynamicToolDispatcher
{
    private const int MaximumArtifactBytes = 2 * 1024 * 1024;
    private readonly SessionStore _store;
    private readonly ILiveDocumentBackend _backend;
    private readonly string _artifactRoot;
    private readonly SkillLibrary? _skills;
    private readonly SessionActivityLog? _activity;
    private readonly ProjectContextStore? _context;

    public DynamicToolDispatcher(
        SessionStore store,
        ILiveDocumentBackend backend,
        AgentHostOptions options,
        SkillLibrary? skills = null,
        SessionActivityLog? activity = null,
        ProjectContextStore? context = null)
    {
        _store = store;
        _backend = backend;
        _skills = skills;
        _activity = activity;
        _context = context;
        _artifactRoot = Path.Combine(options.ResolveDataDirectory(), "artifacts");
        Directory.CreateDirectory(_artifactRoot);
    }

    public async Task<DynamicToolResult> DispatchAsync(DynamicToolCall call, CancellationToken cancellationToken)
    {
        if (!string.Equals(call.Namespace, "gptino_v1", StringComparison.Ordinal))
        {
            return DynamicToolResult.Fail($"Unsupported tool namespace: {call.Namespace ?? "<none>"}");
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = call.Tool switch
            {
                "snapshot_read" => DynamicToolResult.Ok(
                    await ReadSnapshotAsync(call, cancellationToken).ConfigureAwait(false)),
                "component_catalog" => DynamicToolResult.Ok(
                    await _backend.SearchComponentCatalogAsync(call.Arguments, cancellationToken).ConfigureAwait(false)),
                "rhino_list" => DynamicToolResult.Ok(
                    await _backend.ListRhinoObjectsAsync(call.Arguments, cancellationToken).ConfigureAwait(false)),
                "inspect_outputs" => DynamicToolResult.Ok(
                    await InspectOutputsAsync(call, cancellationToken).ConfigureAwait(false)),
                "artifact_read" => DynamicToolResult.Ok(await ReadArtifactAsync(call, cancellationToken).ConfigureAwait(false)),
                "artifact_write" => DynamicToolResult.Ok(await WriteArtifactAsync(call, cancellationToken).ConfigureAwait(false)),
                "change_submit" => DynamicToolResult.Ok(await SubmitChangeAsync(call, cancellationToken).ConfigureAwait(false)),
                "job_status" => DynamicToolResult.Ok(
                    await _backend.ReadJobAsync(call.Arguments, cancellationToken).ConfigureAwait(false)),
                "skill_read" => DynamicToolResult.Ok(RequireSkills().Read(TryString(call.Arguments, "name"))),
                "memory_append" => AppendMemory(call),
                _ => DynamicToolResult.Fail($"Unsupported GPTino tool: {call.Tool}")
            };
            await RecordActivityAsync(call, ok: true, stopwatch.ElapsedMilliseconds, cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordActivityAsync(
                call,
                ok: false,
                stopwatch.ElapsedMilliseconds,
                cancellationToken,
                exception.Message).ConfigureAwait(false);
            return DynamicToolResult.Fail(exception.Message);
        }
    }

    private SkillLibrary RequireSkills() =>
        _skills ?? throw new InvalidOperationException("The skill library is not available in this runtime.");

    private DynamicToolResult AppendMemory(DynamicToolCall call)
    {
        var context = _context
            ?? throw new InvalidOperationException("The project context store is not available in this runtime.");
        var result = context.AppendMemory(TryString(call.Arguments, "entry"));
        return result.Appended ? DynamicToolResult.Ok(result.Message) : DynamicToolResult.Fail(result.Message);
    }

    private async Task RecordActivityAsync(
        DynamicToolCall call,
        bool ok,
        long durationMs,
        CancellationToken cancellationToken,
        string? error = null)
    {
        if (_activity is null)
        {
            return;
        }
        // Successful job_status polls arrive every few seconds and carry no new intent;
        // the writer/queue projections already cover them. Failures always surface.
        if (ok && string.Equals(call.Tool, "job_status", StringComparison.Ordinal))
        {
            return;
        }
        try
        {
            var session = await _store.FindSessionByThreadAsync(call.ThreadId, cancellationToken)
                .ConfigureAwait(false);
            if (session is null)
            {
                return;
            }
            var summary = ActivitySummary(call);
            _activity.Record(
                session.Id,
                call.Tool,
                error is null ? summary : $"{summary} — {error}",
                ok,
                durationMs);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Activity is observability sugar; it must never fail a tool call.
        }
    }

    private static string ActivitySummary(DynamicToolCall call) => call.Tool switch
    {
        "snapshot_read" => call.Arguments.TryGetProperty("scopes", out var scopes) &&
            scopes.ValueKind == JsonValueKind.Array &&
            scopes.GetArrayLength() > 0
                ? $"Reading {scopes.GetArrayLength()} snapshot scope(s)"
                : "Reading the canvas snapshot",
        "component_catalog" => $"Searching components: {TryString(call.Arguments, "query")}",
        "rhino_list" => "Listing Rhino objects",
        "inspect_outputs" => "Inspecting component outputs",
        "artifact_read" => $"Reading draft {TryString(call.Arguments, "path")}",
        "artifact_write" => $"Drafting {TryString(call.Arguments, "path")}",
        "change_submit" => $"Submitting: {TryString(call.Arguments, "summary")}",
        "job_status" => "Polling job status",
        "skill_read" => $"Reading skill {TryString(call.Arguments, "name")}",
        "memory_append" => "Saving a project memory note",
        _ => call.Tool
    };

    private static string? TryString(JsonElement arguments, string property) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private async Task<object> SubmitChangeAsync(DynamicToolCall call, CancellationToken cancellationToken)
    {
        var session = await _store.FindSessionByThreadAsync(call.ThreadId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The calling Codex thread is not bound to a GPTino session.");
        if (string.Equals(session.Role, "planner", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(session.Role, "read-only", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Session role '{session.Role}' cannot submit live changes.");
        }
        if (session.State == SessionStates.Paused)
        {
            throw new InvalidOperationException("This session is paused.");
        }
        return await _backend.SubmitChangeAsync(session, call.Arguments, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> ReadSnapshotAsync(
        DynamicToolCall call,
        CancellationToken cancellationToken)
    {
        var session = await RequireCallingSessionAsync(call.ThreadId, cancellationToken).ConfigureAwait(false);
        return await _backend.ReadSnapshotAsync(session, call.Arguments, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> InspectOutputsAsync(
        DynamicToolCall call,
        CancellationToken cancellationToken)
    {
        // Output inspection reads one Grasshopper document's live component state, so the calling
        // session is resolved and its document binding routes the read (same rule as snapshot_read).
        var session = await RequireCallingSessionAsync(call.ThreadId, cancellationToken).ConfigureAwait(false);
        return await _backend.InspectCanvasOutputsAsync(session, call.Arguments, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<object> ReadArtifactAsync(DynamicToolCall call, CancellationToken cancellationToken)
    {
        var session = await RequireCallingSessionAsync(call.ThreadId, cancellationToken).ConfigureAwait(false);
        var sessionRoot = SessionArtifactRoot(session.Id);
        var path = ResolveArtifact(session.Id, call.Arguments.GetProperty("path").GetString());
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Draft artifact was not found.", Path.GetFileName(path));
        }
        var info = new FileInfo(path);
        if (info.Length > MaximumArtifactBytes)
        {
            throw new InvalidOperationException("Draft artifact exceeds the 2 MiB limit.");
        }
        return new
        {
            path = Path.GetRelativePath(sessionRoot, path).Replace('\\', '/'),
            content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false),
            bytes = info.Length
        };
    }

    private async Task<object> WriteArtifactAsync(DynamicToolCall call, CancellationToken cancellationToken)
    {
        var session = await RequireCallingSessionAsync(call.ThreadId, cancellationToken).ConfigureAwait(false);
        var sessionRoot = SessionArtifactRoot(session.Id);
        var path = ResolveArtifact(session.Id, call.Arguments.GetProperty("path").GetString());
        ReservedArtifactStorage.RejectUserPath(sessionRoot, path);
        var content = call.Arguments.GetProperty("content").GetString() ?? string.Empty;
        var bytes = Encoding.UTF8.GetByteCount(content);
        if (bytes > MaximumArtifactBytes)
        {
            throw new InvalidOperationException("Draft artifact exceeds the 2 MiB limit.");
        }
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        ConstrainedPath.RejectExistingReparsePoints(sessionRoot, parent, "Artifact");
        var temporary = Path.Combine(parent, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.gptino-tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
        return new
        {
            path = Path.GetRelativePath(sessionRoot, path).Replace('\\', '/'),
            bytes,
            liveDocumentChanged = false
        };
    }

    private async Task<SessionRecord> RequireCallingSessionAsync(
        string threadId,
        CancellationToken cancellationToken) =>
        await _store.FindSessionByThreadAsync(threadId, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("The calling Codex thread is not bound to a GPTino session.");

    private string SessionArtifactRoot(Guid sessionId) =>
        Path.Combine(_artifactRoot, sessionId.ToString("N"));

    private string ResolveArtifact(Guid sessionId, string? relativePath)
    {
        var sessionRoot = SessionArtifactRoot(sessionId);
        return ConstrainedPath.Resolve(sessionRoot, relativePath, "Artifact");
    }
}
