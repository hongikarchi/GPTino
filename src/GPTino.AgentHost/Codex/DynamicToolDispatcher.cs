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

    Task<object> ArrangeLayoutAsync(SessionRecord session, JsonElement arguments, CancellationToken cancellationToken);

    Task<object> ReadJobAsync(JsonElement arguments, CancellationToken cancellationToken);

    Task<object> ResumeSessionAsync(SessionRecord session, JsonElement arguments, CancellationToken cancellationToken);

    Task<object> ReadDataFlowAsync(SessionRecord session, CancellationToken cancellationToken);

    Task<object> ReadRhinoAuditAsync(JsonElement arguments, CancellationToken cancellationToken);

    Task<object> ReadStructuralExtractAsync(JsonElement arguments, CancellationToken cancellationToken);

    Task<object> ReadRhinoLayersAsync(CancellationToken cancellationToken);

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

    public Task<object> ArrangeLayoutAsync(SessionRecord session, JsonElement arguments, CancellationToken cancellationToken) =>
        Task.FromException<object>(new InvalidOperationException("The Rhino/Grasshopper bridge is not connected."));

    public Task<object> ReadJobAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        Task.FromException<object>(new InvalidOperationException("The Rhino/Grasshopper bridge is not connected."));

    public Task<object> ResumeSessionAsync(SessionRecord session, JsonElement arguments, CancellationToken cancellationToken) =>
        Task.FromException<object>(new InvalidOperationException("The Rhino/Grasshopper bridge is not connected."));

    public Task<object> ReadDataFlowAsync(SessionRecord session, CancellationToken cancellationToken) =>
        Task.FromException<object>(new InvalidOperationException("The Rhino/Grasshopper bridge is not connected."));

    public Task<object> ReadRhinoAuditAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        Task.FromException<object>(new InvalidOperationException("The Rhino/Grasshopper bridge is not connected."));

    public Task<object> ReadStructuralExtractAsync(JsonElement arguments, CancellationToken cancellationToken) =>
        Task.FromException<object>(new InvalidOperationException("The Rhino/Grasshopper bridge is not connected."));

    public Task<object> ReadRhinoLayersAsync(CancellationToken cancellationToken) =>
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
    private readonly DataLibrary? _data;
    private readonly SessionActivityLog? _activity;
    private readonly ProjectContextStore? _context;
    private readonly ProblemLog? _problems;
    private readonly IStructuralSolver? _structuralSolver;

    public DynamicToolDispatcher(
        SessionStore store,
        ILiveDocumentBackend backend,
        AgentHostOptions options,
        SkillLibrary? skills = null,
        SessionActivityLog? activity = null,
        ProjectContextStore? context = null,
        ProblemLog? problems = null,
        DataLibrary? data = null,
        IStructuralSolver? structuralSolver = null)
    {
        _store = store;
        _backend = backend;
        _skills = skills;
        _data = data;
        _activity = activity;
        _context = context;
        _problems = problems;
        _structuralSolver = structuralSolver;
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
                "data_flow_read" => DynamicToolResult.Ok(
                    await ReadDataFlowAsync(call, cancellationToken).ConfigureAwait(false)),
                "rhino_audit" => DynamicToolResult.Ok(
                    await _backend.ReadRhinoAuditAsync(call.Arguments, cancellationToken).ConfigureAwait(false)),
                "structural_extract" => DynamicToolResult.Ok(
                    await ExtractStructuralAsync(call, cancellationToken).ConfigureAwait(false)),
                "structural_solve" => DynamicToolResult.Ok(
                    await SolveStructuralAsync(call, cancellationToken).ConfigureAwait(false)),
                "rhino_layers" => DynamicToolResult.Ok(
                    await _backend.ReadRhinoLayersAsync(cancellationToken).ConfigureAwait(false)),
                "artifact_read" => DynamicToolResult.Ok(await ReadArtifactAsync(call, cancellationToken).ConfigureAwait(false)),
                "artifact_write" => DynamicToolResult.Ok(await WriteArtifactAsync(call, cancellationToken).ConfigureAwait(false)),
                "change_submit" => DynamicToolResult.Ok(await SubmitChangeAsync(call, cancellationToken).ConfigureAwait(false)),
                "arrange_layout" => DynamicToolResult.Ok(await ArrangeLayoutAsync(call, cancellationToken).ConfigureAwait(false)),
                "job_status" => DynamicToolResult.Ok(
                    await _backend.ReadJobAsync(call.Arguments, cancellationToken).ConfigureAwait(false)),
                "recovery_resume" => DynamicToolResult.Ok(
                    await ResumeRecoveryAsync(call, cancellationToken).ConfigureAwait(false)),
                "skill_read" => DynamicToolResult.Ok(RequireSkills().Read(TryString(call.Arguments, "name"))),
                "memory_append" => AppendMemory(call),
                "goal_propose" => DynamicToolResult.Ok(
                    await ProposeGoalAsync(call, cancellationToken).ConfigureAwait(false)),
                "goal_score" => DynamicToolResult.Ok(
                    await ScoreGoalAsync(call, cancellationToken).ConfigureAwait(false)),
                "approval_request" => DynamicToolResult.Ok(
                    await RequestApprovalAsync(call, cancellationToken).ConfigureAwait(false)),
                "data_read" => DynamicToolResult.Ok(RequireData().Read(TryString(call.Arguments, "name"))),
                _ => DynamicToolResult.Fail($"Unsupported GPTino tool: {call.Tool}")
            };
            // Dev-only latency stream: EVERY call (incl. job_status polls, which RecordActivityAsync
            // filters out) so a benchmark can split turn wall-clock into model-inference gaps vs
            // GPTino tool-handling. No-op unless GPTINO_DEV_MODE is set.
            GPTino.BridgeContract.AuthoringLatencyTrace.TryToolCall(
                call.Tool, stopwatch.ElapsedMilliseconds, ok: true, call.ThreadId);
            await RecordActivityAsync(call, ok: true, stopwatch.ElapsedMilliseconds, cancellationToken)
                .ConfigureAwait(false);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            GPTino.BridgeContract.AuthoringLatencyTrace.TryToolCall(
                call.Tool, stopwatch.ElapsedMilliseconds, ok: false, call.ThreadId);
            await RecordActivityAsync(
                call,
                ok: false,
                stopwatch.ElapsedMilliseconds,
                cancellationToken,
                exception.Message).ConfigureAwait(false);
            return DynamicToolResult.Fail(exception.Message);
        }
    }

    private async Task<object> ReadDataFlowAsync(DynamicToolCall call, CancellationToken cancellationToken)
    {
        var session = await _store.FindSessionByThreadAsync(call.ThreadId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The calling Codex thread is not bound to a GPTino session.");
        return await _backend.ReadDataFlowAsync(session, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> ResumeRecoveryAsync(DynamicToolCall call, CancellationToken cancellationToken)
    {
        // Deliberately NOT gated on pause: recovery_resume only lifts the halt latch of the
        // calling session — it performs no document write.
        var session = await RequireCallingSessionAsync(call.ThreadId, cancellationToken).ConfigureAwait(false);
        return await _backend.ResumeSessionAsync(session, call.Arguments, cancellationToken).ConfigureAwait(false);
    }

    private SkillLibrary RequireSkills() =>
        _skills ?? throw new InvalidOperationException("The skill library is not available in this runtime.");

    private DataLibrary RequireData() =>
        _data ?? throw new InvalidOperationException("The data library is not available in this runtime.");

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
        "data_flow_read" => "Reading the Rhino-GH data-flow ledger",
        "rhino_audit" => "Auditing the Rhino document",
        "structural_extract" => "Extracting structural member axes",
        "structural_solve" => "Solving the structural model (PyNite)",
        "rhino_layers" => "Reading the Rhino layer table",
        "inspect_outputs" => "Inspecting component outputs",
        "artifact_read" => $"Reading draft {TryString(call.Arguments, "path")}",
        "artifact_write" => $"Drafting {TryString(call.Arguments, "path")}",
        "change_submit" => $"Submitting: {TryString(call.Arguments, "summary")}",
        "arrange_layout" => "Tidying the canvas layout",
        "job_status" => "Polling job status",
        "recovery_resume" => "Resuming after a recovery halt",
        "skill_read" => $"Reading skill {TryString(call.Arguments, "name")}",
        "memory_append" => "Saving a project memory note",
        "data_read" => $"Reading data {TryString(call.Arguments, "name")}",
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
        if (session.State == SessionStates.Paused)
        {
            throw new InvalidOperationException("This session is paused.");
        }
        return await _backend.SubmitChangeAsync(session, call.Arguments, cancellationToken).ConfigureAwait(false);
    }

    private async Task<object> ArrangeLayoutAsync(DynamicToolCall call, CancellationToken cancellationToken)
    {
        // arrange_layout is a write (it submits a canvas.move), so it carries the same gate as
        // change_submit.
        var session = await _store.FindSessionByThreadAsync(call.ThreadId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The calling Codex thread is not bound to a GPTino session.");
        if (session.State == SessionStates.Paused)
        {
            throw new InvalidOperationException("This session is paused.");
        }
        return await _backend.ArrangeLayoutAsync(session, call.Arguments, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// structural_extract: server-computed member axes from the bridge, section identity matched
    /// against the shipped KS catalog, full member list persisted as a session artifact, and only
    /// the SUMMARY returned to the model. 1,199 members in the tool result would be pure context
    /// waste; structural_solve and artifact_read take the artifact path instead.
    /// </summary>
    private async Task<object> ExtractStructuralAsync(DynamicToolCall call, CancellationToken cancellationToken)
    {
        var session = await RequireCallingSessionAsync(call.ThreadId, cancellationToken).ConfigureAwait(false);
        var wrapped = JsonSerializer.SerializeToElement(
            await _backend.ReadStructuralExtractAsync(call.Arguments, cancellationToken).ConfigureAwait(false),
            JsonDefaults.Options);
        var result = wrapped.GetProperty("result");

        // Section identity: matching happens HERE, not in the Rhino adapter, because the catalog
        // is shipped AgentHost data — the adapter reports geometry facts only. Prototype solids
        // are drawn either at EXACT nominal dims or at nominal × 1.02 (both conventions exist in
        // the same production file: the parked display copies were ×1.02 while the definition
        // geometry is exact, and a fixed ÷1.02 misidentified the two column marks whose catalog
        // neighbors sit ~2% apart). Try both hypotheses and keep whichever fits better.
        var guesses = new SortedDictionary<string, object>(StringComparer.Ordinal);
        if (_data is not null && result.TryGetProperty("prototypes", out var prototypes))
        {
            var catalog = LoadSectionCatalog();
            foreach (var prototype in prototypes.EnumerateArray())
            {
                var mark = prototype.GetProperty("mark").GetString() ?? string.Empty;
                var outerX = prototype.GetProperty("outerX").GetDouble();
                var outerY = prototype.GetProperty("outerY").GetDouble();
                (string Name, double Error)? best = null;
                foreach (var scale in new[] { 1.0, 1.02 })
                {
                    var depth = Math.Max(outerX, outerY) / scale;
                    var width = Math.Min(outerX, outerY) / scale;
                    foreach (var (name, h, b) in catalog)
                    {
                        var error = Math.Abs(h - depth) + Math.Abs(b - width);
                        if (best is null || error < best.Value.Error)
                        {
                            best = (name, error);
                        }
                    }
                }
                if (best is { } match && !guesses.ContainsKey(mark))
                {
                    guesses[mark] = new { section = match.Name, errorMm = Math.Round(match.Error, 1) };
                }
            }
        }

        const string artifactPath = "structural/members.json";
        var artifact = JsonSerializer.Serialize(
            new { extraction = result, sectionGuesses = guesses },
            JsonDefaults.Options);
        await WriteManagedArtifactAsync(session.Id, artifactPath, artifact, cancellationToken).ConfigureAwait(false);

        var members = result.GetProperty("members");
        var freeEnds = result.GetProperty("freeEnds");
        var byMark = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var byKind = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var member in members.EnumerateArray())
        {
            var mark = member.GetProperty("mark").GetString() ?? string.Empty;
            var kind = member.GetProperty("kind").GetString() ?? string.Empty;
            byMark[mark] = byMark.GetValueOrDefault(mark) + 1;
            byKind[kind] = byKind.GetValueOrDefault(kind) + 1;
        }
        // Free ends ride the summary WITH their source object ids: they are the ask-back items,
        // and the agent needs real ids to point at them with focus chips before solving.
        var freeEndSummaries = freeEnds.EnumerateArray()
            .Take(20)
            .Select(free => new
            {
                point = free.GetProperty("point"),
                sourceObjectIds = free.GetProperty("sourceObjectIds"),
            })
            .ToArray();
        return new
        {
            docUnits = result.GetProperty("docUnits").GetString(),
            scannedObjects = result.GetProperty("scannedObjects").GetInt32(),
            memberCount = members.GetArrayLength(),
            byMark,
            byKind,
            mergedDuplicateAxes = result.GetProperty("mergedDuplicateAxes").GetInt32(),
            obliqueExactAxes = result.GetProperty("obliqueExactAxes").GetInt32(),
            skippedByReason = result.GetProperty("skippedByReason"),
            freeEndCount = freeEnds.GetArrayLength(),
            freeEnds = freeEndSummaries,
            sectionGuesses = guesses,
            truncated = result.GetProperty("truncated").GetBoolean(),
            fingerprint = wrapped.GetProperty("fingerprint"),
            membersArtifact = artifactPath,
        };
    }

    /// <summary>
    /// structural_solve: composes the solver input from the extraction artifact + the shipped KS
    /// catalog + the user's ask-back answers, runs the SHIPPED out-of-process PyNite solver, and
    /// returns the verdict summary. Failed members ride the summary WITH source object ids so the
    /// agent can point at the real solids; the full report (every check, viz nodes) goes to the
    /// structural/results.json artifact.
    /// </summary>
    private async Task<object> SolveStructuralAsync(DynamicToolCall call, CancellationToken cancellationToken)
    {
        if (_structuralSolver is null)
        {
            throw new InvalidOperationException("The structural solver is not available on this host.");
        }
        var session = await RequireCallingSessionAsync(call.ThreadId, cancellationToken).ConfigureAwait(false);
        var membersArtifact = TryString(call.Arguments, "membersArtifact") ?? "structural/members.json";
        var artifactFile = ResolveArtifact(session.Id, membersArtifact);
        if (!File.Exists(artifactFile))
        {
            throw new InvalidOperationException(
                $"Extraction artifact '{membersArtifact}' was not found — run structural_extract first.");
        }
        using var artifact = JsonDocument.Parse(
            await File.ReadAllTextAsync(artifactFile, cancellationToken).ConfigureAwait(false));
        var extraction = artifact.RootElement.GetProperty("extraction");

        // Members: extraction stores endpoints as {x,y,z}; the solver contract is arrays.
        var members = new List<object>();
        foreach (var member in extraction.GetProperty("members").EnumerateArray())
        {
            var a = member.GetProperty("a");
            var b = member.GetProperty("b");
            members.Add(new
            {
                mark = member.GetProperty("mark").GetString(),
                a = new[] { a.GetProperty("x").GetDouble(), a.GetProperty("y").GetDouble(), a.GetProperty("z").GetDouble() },
                b = new[] { b.GetProperty("x").GetDouble(), b.GetProperty("y").GetDouble(), b.GetProperty("z").GetDouble() },
                kind = member.GetProperty("kind").GetString(),
                sourceObjectIds = member.GetProperty("sourceObjectIds"),
            });
        }
        if (members.Count == 0)
        {
            throw new InvalidOperationException("The extraction artifact holds no members to solve.");
        }

        // Sections: the FULL catalog rows are injected — the Python side never touches host paths.
        var sections = new SortedDictionary<string, object>(StringComparer.Ordinal);
        string? defaultSection = null;
        var catalogPayload = JsonSerializer.SerializeToElement(
            RequireData().Read("structural/sections-ks.json"), JsonDefaults.Options);
        using (var catalog = JsonDocument.Parse(catalogPayload.GetProperty("content").GetString() ?? "{}"))
        {
            foreach (var section in catalog.RootElement.GetProperty("sections").EnumerateArray())
            {
                var name = section.GetProperty("name").GetString() ?? string.Empty;
                sections[name] = new
                {
                    H = section.GetProperty("H").GetDouble(),
                    B = section.GetProperty("B").GetDouble(),
                    tw = section.GetProperty("tw").GetDouble(),
                    tf = section.GetProperty("tf").GetDouble(),
                    A = section.GetProperty("A").GetDouble(),
                    Ix = section.GetProperty("Ix").GetDouble(),
                    Iy = section.GetProperty("Iy").GetDouble(),
                };
                defaultSection ??= name;
                if (name == "H-300x300x10x15")
                {
                    defaultSection = name;
                }
            }
        }

        // Mark → section: the extraction's geometric guesses, overridable per mark by the answers
        // (the user may know the schedule better than the geometric heuristic).
        var markSections = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (artifact.RootElement.TryGetProperty("sectionGuesses", out var guesses))
        {
            foreach (var guess in guesses.EnumerateObject())
            {
                var name = guess.Value.GetProperty("section").GetString();
                if (name is not null)
                {
                    markSections[guess.Name] = name;
                }
            }
        }
        // Variant marks resolve by PREFIX: members on "SC5 (Bracing)" ARE SC5s, and without this
        // alias they silently fell to the default section — the real-model gate showed 38 bracing
        // members solving 90% too heavy against the validated baseline.
        foreach (var member in extraction.GetProperty("members").EnumerateArray())
        {
            var mark = member.GetProperty("mark").GetString() ?? string.Empty;
            if (markSections.ContainsKey(mark))
            {
                continue;
            }
            var space = mark.IndexOf(' ');
            if (space > 0 && markSections.TryGetValue(mark[..space], out var prefixSection))
            {
                markSections[mark] = prefixSection;
            }
        }
        var answers = call.Arguments.TryGetProperty("answers", out var answersElement) &&
            answersElement.ValueKind == JsonValueKind.Object
                ? answersElement
                : default;
        if (answers.ValueKind == JsonValueKind.Object &&
            answers.TryGetProperty("markSections", out var overrides) &&
            overrides.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in overrides.EnumerateObject())
            {
                if (item.Value.ValueKind == JsonValueKind.String)
                {
                    markSections[item.Name] = item.Value.GetString()!;
                }
            }
        }

        var options = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (answers.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[]
                     {
                         "repairFreeEnds", "cantileverPoints", "extraDistributedKnPerM",
                         "deflectionLimitRatio", "columnMarkPrefixes", "snapMm", "gridMm", "repairSnapMm",
                     })
            {
                if (answers.TryGetProperty(name, out var value))
                {
                    options[name] = value.Clone();
                }
            }
        }

        // NO naming policy here: the solver contract uses the catalog's exact field casing
        // ("H", "Ix"), and the Web default would silently camelCase them into KeyErrors.
        var input = JsonSerializer.Serialize(
            new { members, sections, markSections, defaultSection, options },
            SolverInputJson);
        var reportJson = await _structuralSolver.SolveAsync(input, cancellationToken).ConfigureAwait(false);
        // Clone detaches the element from its document: pieces of this report ride the returned
        // summary, which is serialized AFTER this method's scope would have disposed the document.
        JsonElement root;
        using (var report = JsonDocument.Parse(reportJson))
        {
            root = report.RootElement.Clone();
        }
        if (root.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException($"The structural solver refused the model: {error.GetString()}");
        }

        const string resultsArtifact = "structural/results.json";
        await WriteManagedArtifactAsync(session.Id, resultsArtifact, reportJson, cancellationToken)
            .ConfigureAwait(false);

        var failed = root.GetProperty("failedMembers");
        return new
        {
            solveSeconds = root.GetProperty("solveSeconds").GetDouble(),
            edgesSolved = root.GetProperty("edgesSolved").GetInt32(),
            nodes = root.GetProperty("nodes").GetInt32(),
            supports = root.GetProperty("supports").GetInt32(),
            islandEdgesDropped = root.GetProperty("islandEdgesDropped").GetInt32(),
            islandMembers = root.GetProperty("islandMembers"),
            snappedFreeEnds = root.GetProperty("snappedFreeEnds").GetInt32(),
            tJunctionSplits = root.GetProperty("tJunctionSplits").GetInt32(),
            repairedFreeEnds = root.GetProperty("repairedFreeEnds").GetInt32(),
            freeEndsRemaining = root.GetProperty("freeEndsRemaining"),
            totalLoadKn = root.GetProperty("totalLoadKn").GetDouble(),
            sumReactionsFzKn = root.GetProperty("sumReactionsFzKn").GetDouble(),
            equilibriumErrorPercent = root.GetProperty("equilibriumErrorPercent").GetDouble(),
            maxDisplacementMm = root.GetProperty("maxDisplacementMm").GetDouble(),
            maxDisplacementXyzMm = root.GetProperty("maxDisplacementXyzMm"),
            deflectionLimit = root.GetProperty("deflectionLimit").GetString(),
            memberChecks = root.GetProperty("memberChecks"),
            // Top failures only, WITH ids — the agent points, the artifact holds the rest.
            worstMembers = failed.EnumerateArray().Take(5).ToArray(),
            missingSectionMarks = root.GetProperty("missingSectionMarks"),
            resultsArtifact,
        };
    }

    private static readonly JsonSerializerOptions SolverInputJson = new();

    /// <summary>(name, H, B) rows of the KS section catalog, tolerant of a missing/foreign file.</summary>
    private List<(string Name, double H, double B)> LoadSectionCatalog()
    {
        var rows = new List<(string, double, double)>();
        try
        {
            var payload = JsonSerializer.SerializeToElement(_data!.Read("structural/sections-ks.json"), JsonDefaults.Options);
            var content = payload.GetProperty("content").GetString() ?? "{}";
            using var parsed = JsonDocument.Parse(content);
            foreach (var section in parsed.RootElement.GetProperty("sections").EnumerateArray())
            {
                rows.Add((
                    section.GetProperty("name").GetString() ?? string.Empty,
                    section.GetProperty("H").GetDouble(),
                    section.GetProperty("B").GetDouble()));
            }
        }
        catch (Exception)
        {
            // No catalog, no guesses — the extraction is still fully usable; the summary simply
            // carries no sectionGuesses and the agent can consult data_read itself.
        }
        return rows;
    }

    /// <summary>
    /// Writes a server-produced artifact into the session's managed storage with the same atomic
    /// pattern as artifact_write. Unlike artifact_write this may exceed the draft cap — the member
    /// list of a large model is server output, not a model-typed draft.
    /// </summary>
    private async Task WriteManagedArtifactAsync(
        Guid sessionId,
        string relativePath,
        string content,
        CancellationToken cancellationToken)
    {
        var sessionRoot = SessionArtifactRoot(sessionId);
        var path = ResolveArtifact(sessionId, relativePath);
        ReservedArtifactStorage.RejectUserPath(sessionRoot, path);
        var parent = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(parent);
        ConstrainedPath.RejectExistingReparsePoints(sessionRoot, parent, "Artifact");
        var temporary = Path.Combine(parent, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.gptino-tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Stores the agent's proposed goal card and hands the turn back to the user. Nothing about
    /// the document changes here — this is the "frame it before you build it" step, and the tool
    /// result deliberately tells the agent to stop rather than proceed on an unconfirmed reading.
    /// </summary>
    private async Task<object> ProposeGoalAsync(DynamicToolCall call, CancellationToken cancellationToken)
    {
        var session = await RequireCallingSessionAsync(call.ThreadId, cancellationToken).ConfigureAwait(false);
        var card = new GoalCard(
            Status: "proposing",
            Objective: TryString(call.Arguments, "objective") ?? string.Empty,
            Criteria: TryStringList(call.Arguments, "criteria"),
            Assumptions: TryStringList(call.Arguments, "assumptions"),
            OutOfScope: TryStringList(call.Arguments, "outOfScope"),
            Options: TryOptions(call.Arguments),
            ProposedAt: DateTimeOffset.UtcNow);
        await _store.SetGoalCardAsync(
            session.Id,
            JsonSerializer.Serialize(card, GoalJson),
            cancellationToken).ConfigureAwait(false);
        return new
        {
            status = "awaiting_user_confirmation",
            message = "The goal card is on screen. End your turn now — do not start the work. "
                + "The confirmed card (with any edits the user makes) arrives with the next turn.",
        };
    }

    /// <summary>Records the agent's self-score against the confirmed card's own criteria.</summary>
    private async Task<object> ScoreGoalAsync(DynamicToolCall call, CancellationToken cancellationToken)
    {
        var session = await RequireCallingSessionAsync(call.ThreadId, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(session.GoalCard))
        {
            throw new InvalidOperationException("This session has no goal card to score.");
        }
        var card = JsonSerializer.Deserialize<GoalCard>(session.GoalCard!, GoalJson)
            ?? throw new InvalidOperationException("The stored goal card could not be read.");
        var scores = new List<GoalCriterionScore>();
        if (call.Arguments.ValueKind == JsonValueKind.Object &&
            call.Arguments.TryGetProperty("scores", out var raw) &&
            raw.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in raw.EnumerateArray())
            {
                scores.Add(new GoalCriterionScore(
                    TryString(item, "criterion") ?? string.Empty,
                    item.TryGetProperty("passed", out var passed) && passed.ValueKind == JsonValueKind.True,
                    TryString(item, "evidence") ?? string.Empty));
            }
        }
        var updated = card with { Status = "scored", Scores = scores };
        await _store.SetGoalCardAsync(
            session.Id,
            JsonSerializer.Serialize(updated, GoalJson),
            cancellationToken).ConfigureAwait(false);
        return new { status = "scored", criteria = scores.Count, passed = scores.Count(s => s.Passed) };
    }

    /// <summary>
    /// Stores what the agent wants approved on the user's own geometry and hands the turn back.
    /// Nothing is granted here — the user grants, item by item, from the card.
    /// </summary>
    private async Task<object> RequestApprovalAsync(DynamicToolCall call, CancellationToken cancellationToken)
    {
        var session = await RequireCallingSessionAsync(call.ThreadId, cancellationToken).ConfigureAwait(false);
        var items = new List<ApprovalItem>();
        if (call.Arguments.ValueKind == JsonValueKind.Object &&
            call.Arguments.TryGetProperty("items", out var rawItems) &&
            rawItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in rawItems.EnumerateArray())
            {
                var targets = new List<ApprovalGrantItem>();
                if (item.TryGetProperty("targets", out var rawTargets) && rawTargets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var target in rawTargets.EnumerateArray())
                    {
                        if (Guid.TryParse(TryString(target, "objectId"), out var objectId))
                        {
                            // Label/role/impact are optional model-authored display strings (SHARED
                            // CONTRACT with the panel); absent stays null and old cards keep loading.
                            // Clamped to a sane display length so a runaway generation cannot flood
                            // the stored card or the approval UI.
                            targets.Add(new ApprovalGrantItem(
                                objectId,
                                TryString(target, "fingerprint") ?? string.Empty,
                                ClampDisplayText(TryString(target, "label")),
                                ClampDisplayText(TryString(target, "role")),
                                ClampDisplayText(TryString(target, "impact"))));
                        }
                    }
                }
                if (targets.Count == 0) continue; // an item nothing can be pinned to is not reviewable
                items.Add(new ApprovalItem(
                    TryString(item, "id") ?? Guid.NewGuid().ToString("N")[..8],
                    TryString(item, "label") ?? string.Empty,
                    TryString(item, "measure"),
                    targets,
                    TryStringList(item, "choices") is { Count: > 0 } choices ? choices : null));
            }
        }
        if (items.Count == 0)
        {
            throw new InvalidOperationException(
                "approval_request needs at least one item with objectId+fingerprint targets.");
        }
        var card = new ApprovalCard(
            Status: "proposing",
            Summary: TryString(call.Arguments, "summary") ?? string.Empty,
            Items: items,
            ProposedAt: DateTimeOffset.UtcNow);
        await _store.SetApprovalCardAsync(
            session.Id,
            JsonSerializer.Serialize(card, GoalJson),
            cancellationToken).ConfigureAwait(false);
        return new
        {
            status = "awaiting_user_approval",
            items = items.Count,
            message = "The approval card is on screen. End your turn now — nothing is granted yet. "
                + "Whatever the user approves comes back with a grantId on the next turn; put that id "
                + "in the ChangeSet's approvalGrantId and touch ONLY the approved items.",
        };
    }

    /// <summary>
    /// Display-string clamp for model-authored approval-card text (target label/role/impact):
    /// anything past 300 chars is truncated with an ellipsis. 300 comfortably fits the longest
    /// honest one-line explanation while keeping a runaway generation from flooding the card.
    /// </summary>
    internal static string? ClampDisplayText(string? value) =>
        value is { Length: > 300 } ? value[..299] + "…" : value;

    private static readonly JsonSerializerOptions GoalJson = new(JsonSerializerDefaults.Web);

    private static IReadOnlyList<string> TryStringList(JsonElement arguments, string property) =>
        arguments.ValueKind == JsonValueKind.Object &&
        arguments.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray()
            : [];

    private static IReadOnlyList<GoalOption>? TryOptions(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("options", out var raw) ||
            raw.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var options = new List<GoalOption>();
        foreach (var item in raw.EnumerateArray())
        {
            var ids = new List<Guid>();
            if (item.TryGetProperty("objectIds", out var idArray) && idArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in idArray.EnumerateArray())
                {
                    if (id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var parsed))
                    {
                        ids.Add(parsed);
                    }
                }
            }
            options.Add(new GoalOption(
                TryString(item, "id") ?? string.Empty,
                TryString(item, "label") ?? string.Empty,
                TryString(item, "detail"),
                ids.Count > 0 ? ids : null));
        }
        return options.Count > 0 ? options : null;
    }

    private string SessionArtifactRoot(Guid sessionId) =>
        Path.Combine(_artifactRoot, sessionId.ToString("N"));

    private string ResolveArtifact(Guid sessionId, string? relativePath)
    {
        var sessionRoot = SessionArtifactRoot(sessionId);
        return ConstrainedPath.Resolve(sessionRoot, relativePath, "Artifact");
    }
}
