using System.Text;
using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Data;
using GPTino.AgentHost.Hosting;
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

    public DynamicToolDispatcher(
        SessionStore store,
        ILiveDocumentBackend backend,
        AgentHostOptions options)
    {
        _store = store;
        _backend = backend;
        _artifactRoot = Path.Combine(options.ResolveDataDirectory(), "artifacts");
        Directory.CreateDirectory(_artifactRoot);
    }

    public async Task<DynamicToolResult> DispatchAsync(DynamicToolCall call, CancellationToken cancellationToken)
    {
        if (!string.Equals(call.Namespace, "gptino_v1", StringComparison.Ordinal))
        {
            return DynamicToolResult.Fail($"Unsupported tool namespace: {call.Namespace ?? "<none>"}");
        }

        try
        {
            return call.Tool switch
            {
                "snapshot_read" => DynamicToolResult.Ok(
                    await ReadSnapshotAsync(call, cancellationToken).ConfigureAwait(false)),
                "component_catalog" => DynamicToolResult.Ok(
                    await _backend.SearchComponentCatalogAsync(call.Arguments, cancellationToken).ConfigureAwait(false)),
                "rhino_list" => DynamicToolResult.Ok(
                    await _backend.ListRhinoObjectsAsync(call.Arguments, cancellationToken).ConfigureAwait(false)),
                "artifact_read" => DynamicToolResult.Ok(await ReadArtifactAsync(call, cancellationToken).ConfigureAwait(false)),
                "artifact_write" => DynamicToolResult.Ok(await WriteArtifactAsync(call, cancellationToken).ConfigureAwait(false)),
                "change_submit" => DynamicToolResult.Ok(await SubmitChangeAsync(call, cancellationToken).ConfigureAwait(false)),
                "job_status" => DynamicToolResult.Ok(
                    await _backend.ReadJobAsync(call.Arguments, cancellationToken).ConfigureAwait(false)),
                _ => DynamicToolResult.Fail($"Unsupported GPTino tool: {call.Tool}")
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return DynamicToolResult.Fail(exception.Message);
        }
    }

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
