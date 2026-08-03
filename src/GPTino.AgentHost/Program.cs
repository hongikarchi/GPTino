using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Codex;
using GPTino.AgentHost.Data;
using GPTino.AgentHost.Hosting;
using GPTino.AgentHost.Runtime;
using GPTino.AgentHost.Security;
using GPTino.BridgeContract;

// Before anything else: drop any disk-file handle inherited from the Rhino parent at spawn. The
// stdio-redirected launch forces handle inheritance, leaking Rhino's open .3dm handle into this
// long-lived process and blocking the user's saves. See InheritedHandleGuard.
var releasedInheritedHandles = InheritedHandleGuard.CloseInheritedDiskHandles();

var packagedWebRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = Directory.Exists(packagedWebRoot) ? packagedWebRoot : null
});
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "HH:mm:ss ";
});

var options = AgentHostArguments.Parse(args, builder.Configuration);
var developmentDataDirectory = DevelopmentDataDirectoryPolicy.ResolveFromEnvironment();
if (developmentDataDirectory is not null &&
    !string.Equals(
        developmentDataDirectory,
        options.ResolveDataDirectory(),
        StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        "The explicit AgentHost data directory does not match the validated development run directory.");
}
using var runtimeInstance = RuntimeInstanceLock.Acquire(options.ResolveDataDirectory());
// One-time legacy adoption must run while this process owns the new root's instance lock and
// before the SessionStore below opens runtime.db. It only applies to the default fingerprint
// root: an explicit --data-directory (dev-mode/benchmark sandboxes) is skipped inside TryAdopt
// so production project data is never imported into an isolated run. The app logger pipeline
// does not exist until Build(), so adoption logs through a short-lived console factory matching
// the app's format.
using (var bootstrapLoggers = LoggerFactory.Create(logging => logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "HH:mm:ss ";
})))
{
    LegacyDataDirectoryAdoption.TryAdopt(
        options,
        bootstrapLoggers.CreateLogger(nameof(LegacyDataDirectoryAdoption)));
}
var identity = new RuntimeIdentity(
    options.ProjectId,
    options.RhinoPath,
    options.GrasshopperPath,
    options.ProjectDirectory,
    DateTimeOffset.UtcNow);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(identity);
builder.Services.AddSingleton(new SessionStore(Path.Combine(options.ResolveDataDirectory(), "runtime.db")));
builder.Services.AddSingleton(new AttachmentStore(options.ResolveDataDirectory()));
builder.Services.AddSingleton<ImageUrlAttachmentFetcher>();
builder.Services.AddSingleton(new ProjectContextStore(options.ResolveDataDirectory()));
builder.Services.AddSingleton(new ProjectArchiveReader(
    ProjectArchiveReader.DefaultProjectsParentDirectory(),
    options.ResolveDataDirectory()));
builder.Services.AddSingleton<SkillLibrary>();
builder.Services.AddSingleton<SessionActivityLog>();
builder.Services.AddSingleton<IThreadInstructionComposer, InstructionAssembler>();
builder.Services.AddSingleton<RuntimeControl>();
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<EndpointRegistry>();
builder.Services.AddSingleton<PanelBootstrapNonceStore>();
builder.Services.AddSingleton<ProblemLog>();
builder.Services.AddSingleton<LiveDocumentBackend>();
builder.Services.AddSingleton<ILiveDocumentBackend>(services =>
    services.GetRequiredService<LiveDocumentBackend>());
builder.Services.AddSingleton<ILiveDocumentQueueControl>(services =>
    services.GetRequiredService<LiveDocumentBackend>());
builder.Services.AddSingleton<ISelectionContextSource>(services =>
    services.GetRequiredService<LiveDocumentBackend>());
builder.Services.AddHostedService(services => services.GetRequiredService<LiveDocumentBackend>());
builder.Services.AddSingleton<CodexAppServerClient>();
builder.Services.AddSingleton<ICodexSessionClient>(services => services.GetRequiredService<CodexAppServerClient>());
builder.Services.AddSingleton<IModelCatalog>(services => services.GetRequiredService<CodexAppServerClient>());
builder.Services.AddSingleton<EffectiveModelState>();
builder.Services.AddSingleton<SessionUsageState>();
builder.Services.AddSingleton<ModelSelector>();
builder.Services.AddSingleton<DynamicToolDispatcher>();
builder.Services.AddSingleton<SessionOrchestrator>();
builder.Services.AddSingleton<RuntimeStateProjector>();
builder.Services.AddSingleton<TerminalLauncher>();
builder.Services.AddSingleton<CodexAuthProbe>();
builder.Services.AddSingleton<CodexLoginLauncher>();
builder.Services.AddHostedService<ReadySignalService>();
builder.Services.AddHostedService<ParentProcessMonitor>();

var app = builder.Build();
if (releasedInheritedHandles.Count > 0)
{
    app.Logger.LogInformation(
        "Released {Count} inherited disk-file handle(s) leaked from the Rhino parent at launch: {Paths}",
        releasedInheritedHandles.Count,
        string.Join("; ", releasedInheritedHandles));
}
var store = app.Services.GetRequiredService<SessionStore>();
await store.InitializeAsync();
app.Services.GetRequiredService<ProjectContextStore>().EnsureScaffolded(
    identity.ProjectId,
    string.IsNullOrWhiteSpace(options.RhinoPath)
        ? "Untitled Rhino"
        : Path.GetFileNameWithoutExtension(options.RhinoPath),
    options.RhinoPath,
    options.GrasshopperPath);
var events = app.Services.GetRequiredService<EventHub>();
var control = app.Services.GetRequiredService<RuntimeControl>();
var backend = app.Services.GetRequiredService<ILiveDocumentBackend>();
var codex = app.Services.GetRequiredService<CodexAppServerClient>();
var dispatcher = app.Services.GetRequiredService<DynamicToolDispatcher>();
var queueControl = app.Services.GetRequiredService<ILiveDocumentQueueControl>();
_ = app.Services.GetRequiredService<SessionOrchestrator>();
codex.DynamicToolHandler = dispatcher.DispatchAsync;
// Resident curator: every project gets exactly one document-hygiene session, provisioned before
// the first schedule snapshot so the panel always sees it. Idempotent across restarts; the codex
// thread itself stays lazy until the first message.
var (liveSessions, _) = await store.ReadStateAsync();
if (!liveSessions.Any(session => string.Equals(session.Role, "curator", StringComparison.OrdinalIgnoreCase)))
{
    // A legacy soft-deleted curator (from before the delete guard) is restored rather than
    // duplicated — the guard would leave a second, unpurgeable copy in the trash forever.
    var deletedCurator = (await store.ReadDeletedSessionsAsync())
        .FirstOrDefault(session => string.Equals(session.Role, "curator", StringComparison.OrdinalIgnoreCase));
    if (deletedCurator is not null)
    {
        await store.SetSessionDeletedAsync(deletedCurator.Id, deleted: false);
    }
    else
    {
        await store.CreateSessionAsync(new CreateSessionRequest("Document care", "curator", ModelProfile: "xhigh"));
    }
}
await queueControl.RefreshScheduleAsync();

app.Use(async (context, next) =>
{
    var remoteAddress = context.Connection.RemoteIpAddress;
    if (remoteAddress is not null && !IPAddress.IsLoopback(remoteAddress))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new ApiError("loopback_required", "GPTino only accepts loopback clients."));
        return;
    }

    if (context.Request.Headers.TryGetValue("Origin", out var originValues) &&
        !RequestOriginPolicy.AllowsPresentedOrigin(
            originValues,
            context.Request.Scheme,
            context.Request.Host.Value))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new ApiError("origin_rejected", "The request origin is not this GPTino runtime."));
        return;
    }

    if (context.Request.Path.StartsWithSegments("/api") &&
        !HasValidApiToken(context, options.ApiToken))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new ApiError(
            "authentication_required",
            "A valid GPTino runtime token is required."));
        return;
    }

    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'";
    try
    {
        await next();
    }
    catch (SessionOrderConcurrencyException exception)
    {
        await WriteErrorAsync(context, StatusCodes.Status409Conflict, "order_version_conflict", exception.Message);
    }
    catch (SessionPausedException exception)
    {
        await WriteErrorAsync(context, StatusCodes.Status409Conflict, "session_paused", exception.Message);
    }
    catch (KeyNotFoundException exception)
    {
        await WriteErrorAsync(context, StatusCodes.Status404NotFound, "not_found", exception.Message);
    }
    catch (ArgumentException exception)
    {
        await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request", exception.Message);
    }
    catch (InvalidOperationException exception)
    {
        await WriteErrorAsync(context, StatusCodes.Status409Conflict, "invalid_state", exception.Message);
    }
    catch (GPTino.BridgeContract.BridgeProtocolException exception)
    {
        // Bridge reads (e.g. GET /data-flow) surface adapter failures as protocol exceptions;
        // a typed 502 beats a raw 500 with a non-ApiError body.
        await WriteErrorAsync(context, StatusCodes.Status502BadGateway, "bridge_error", exception.Message);
    }
    catch (TimeoutException exception)
    {
        await WriteErrorAsync(context, StatusCodes.Status504GatewayTimeout, "bridge_timeout", exception.Message);
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapPost("/panel/bootstrap", (HttpContext context, PanelBootstrapNonceStore panelBootstrap) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    var parentCredential = context.Request.Headers["X-GPTino-Panel-Parent"].FirstOrDefault();
    var documentSerialText = context.Request.Query["documentSerial"].FirstOrDefault();
    if (!uint.TryParse(documentSerialText, NumberStyles.None, CultureInfo.InvariantCulture, out var documentSerial) ||
        !panelBootstrap.TryIssue(parentCredential, documentSerial, out var nonce))
    {
        return Results.Json(
            new ApiError(
                "panel_parent_rejected",
                "The Rhino panel parent credential or target document is invalid."),
            statusCode: StatusCodes.Status401Unauthorized);
    }

    return Results.Ok(new { nonce });
});

app.MapGet("/panel", async (HttpContext context, PanelBootstrapNonceStore panelBootstrap) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    var supplied = context.Request.Query["bootstrap"].FirstOrDefault();
    var documentSerialText = context.Request.Query["documentSerial"].FirstOrDefault();
    if (!uint.TryParse(documentSerialText, NumberStyles.None, CultureInfo.InvariantCulture, out var documentSerial) ||
        !panelBootstrap.IsBoundDocument(documentSerial))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new ApiError(
            "panel_bootstrap_rejected",
            "The Rhino panel bootstrap nonce or target document is missing, expired, or invalid."));
        return;
    }

    if (HasValidApiToken(context, options.ApiToken))
    {
        context.Response.Redirect("/");
        return;
    }

    if (!panelBootstrap.TryConsume(supplied, documentSerial))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new ApiError(
            "panel_bootstrap_rejected",
            "The Rhino panel bootstrap nonce is missing, expired, or invalid."));
        return;
    }

    context.Response.Cookies.Append("gptino_runtime", options.ApiToken, new CookieOptions
    {
        HttpOnly = true,
        IsEssential = true,
        SameSite = SameSiteMode.Strict,
        Secure = false,
        Path = "/"
    });
    context.Response.Redirect("/");
});

var api = app.MapGroup("/api/v1");

api.MapGet("/runtime", async (RuntimeStateProjector projector, CancellationToken cancellationToken) =>
    Results.Ok(await projector.BuildAsync(cancellationToken)));

api.MapGet("/events", async (HttpContext context, RuntimeStateProjector projector, EventHub eventHub) =>
{
    context.Response.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache, no-store";
    context.Response.Headers.Connection = "keep-alive";
    using var subscription = eventHub.Subscribe();
    await SendStateEventAsync(context, projector, context.RequestAborted);
    await foreach (var _ in subscription.Reader.ReadAllAsync(context.RequestAborted))
    {
        await SendStateEventAsync(context, projector, context.RequestAborted);
    }
});

// On-demand Rhino<->GH data-flow detail for the panel drawer: per-parameter references (with
// existence/layer) plus the stamped-bake census. ?doc= selects the GH docKey; omitted = the only
// registered document. Refreshes the summary cache as a side effect.
api.MapGet("/data-flow", async (
    string? doc,
    LiveDocumentBackend liveBackend,
    CancellationToken cancellationToken) =>
    Results.Ok(await liveBackend.ReadDataFlowDetailAsync(doc, cancellationToken)));

// Mints a user-approval grant for the audit card's Approve action: bound to exactly the
// (objectId, fingerprint) pairs the user saw, expiring, and required before destructive ops can
// touch objects without GPTino provenance stamps.
api.MapPost("/approval-grants", (
    MintApprovalGrantRequest request,
    LiveDocumentBackend liveBackend) =>
    Results.Ok(liveBackend.MintApprovalGrant(
        (request.Items ?? throw new ArgumentException("items is required."))
            .Select(item => (item.ObjectId, item.Fingerprint))
            .ToArray())));

// Document-hygiene audit for the curator tab's preset buttons. Read-only; detection is server
// code in the Rhino adapter, so the same findings render in the panel card and reach the agent.
api.MapGet("/audit", async (
    string kind,
    double? tolerance,
    double? bandFactor,
    int? limit,
    LiveDocumentBackend liveBackend,
    CancellationToken cancellationToken) =>
{
    var arguments = JsonSerializer.SerializeToElement(new
    {
        kind,
        tolerance,
        bandFactor,
        limit = limit ?? 50,
    });
    return Results.Ok(await liveBackend.ReadRhinoAuditAsync(arguments, cancellationToken));
});

api.MapPost("/sessions", async (
    CreateSessionRequest request,
    SessionStore sessionStore,
    ILiveDocumentQueueControl queue,
    CancellationToken cancellationToken) =>
{
    if (string.Equals(request.Role?.Trim(), "curator", StringComparison.OrdinalIgnoreCase))
    {
        // Singleton by construction: extra curators would be invisible on the panel yet
        // permanently undeletable (the delete guard fires for any curator-role row).
        throw new ArgumentException(
            "The resident curator session is provisioned by the runtime; sessions created here are modelers.");
    }
    // ModelProfile now carries the reasoning-effort level directly (low..ultra) — manual effort, no
    // adaptive routing. NormalizeEffort validates and maps any legacy profile value for back-compat.
    var session = await sessionStore.CreateSessionAsync(
        request with { ModelProfile = NormalizeEffort(request.ModelProfile) },
        cancellationToken);
    await queue.RefreshScheduleAsync(cancellationToken);
    events.Publish();
    return Results.Created($"/api/v1/sessions/{session.Id:D}", session);
});

api.MapPut("/sessions/order", async (
    ReorderSessionsRequest request,
    SessionStore sessionStore,
    ILiveDocumentQueueControl queue,
    CancellationToken cancellationToken) =>
{
    await sessionStore.ReorderAsync(request.OrderedSessionIds, request.OrderVersion, cancellationToken);
    await queue.RefreshScheduleAsync(cancellationToken);
    events.Publish();
    return Results.NoContent();
});

api.MapPut("/sessions/{id:guid}/pause", async (
    Guid id,
    SetPausedRequest request,
    SessionOrchestrator orchestrator,
    CancellationToken cancellationToken) =>
{
    await orchestrator.SetSessionPausedAsync(id, request.Paused, cancellationToken);
    await queueControl.RefreshScheduleAsync(cancellationToken);
    return Results.NoContent();
});

// Stop & edit: interrupt the turn and pull the last user message back for editing.
api.MapPost("/sessions/{id:guid}/retract-last", async (
    Guid id,
    SessionOrchestrator orchestrator,
    CancellationToken cancellationToken) =>
{
    var content = await orchestrator.StopAndRetractLastMessageAsync(id, cancellationToken);
    await queueControl.RefreshScheduleAsync(cancellationToken);
    return Results.Ok(new { content });
});

api.MapPut("/sessions/{id:guid}/target", async (
    Guid id,
    SetSessionTargetRequest request,
    SessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    await sessionStore.SetGrasshopperDocAsync(id, request.GrasshopperDoc, cancellationToken);
    events.Publish();
    return Results.NoContent();
});

api.MapPut("/sessions/{id:guid}/mode", async (
    Guid id,
    SetModeRequest request,
    SessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    // Mode is orthogonal to role: flipping plan/auto must never rewrite WHO the session is
    // (the pre-split encoding turned every plan toggle into a role rewrite).
    var mode = request.Mode.Trim().ToLowerInvariant() switch
    {
        "plan" => "plan",
        "auto" => "auto",
        _ => throw new ArgumentException("Mode must be 'plan' or 'auto'.")
    };
    await sessionStore.SetModeAsync(id, mode, cancellationToken);
    events.Publish();
    return Results.NoContent();
});

api.MapPut("/sessions/{id:guid}/model", async (
    Guid id,
    SetModelRequest request,
    SessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    await sessionStore.UpdatePreferencesAsync(
        id,
        NormalizeEffort(request.ModelProfile),
        request.Model,
        true,
        cancellationToken);
    events.Publish();
    return Results.NoContent();
});

api.MapPut("/sessions/{id:guid}/goal", async (
    Guid id,
    SetGoalRequest request,
    SessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    await sessionStore.SetGoalEnabledAsync(id, request.Enabled, cancellationToken);
    events.Publish();
    return Results.NoContent();
});

// Soft-delete: hide from the active list but keep everything, so it can be restored.
// The resident curator is not deletable — the store guard throws and the middleware maps it
// to a 409 invalid_state.
api.MapDelete("/sessions/{id:guid}", async (
    Guid id,
    SessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    await sessionStore.SetSessionDeletedAsync(id, deleted: true, cancellationToken);
    await queueControl.RefreshScheduleAsync(cancellationToken);
    events.Publish();
    return Results.NoContent();
});

api.MapGet("/sessions/deleted", async (
    SessionStore sessionStore,
    CancellationToken cancellationToken) =>
    Results.Ok(await sessionStore.ReadDeletedSessionsAsync(cancellationToken)));

api.MapPost("/sessions/{id:guid}/restore", async (
    Guid id,
    SessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    await sessionStore.SetSessionDeletedAsync(id, deleted: false, cancellationToken);
    await queueControl.RefreshScheduleAsync(cancellationToken);
    events.Publish();
    return Results.NoContent();
});

// Permanent delete: removes the session and its transcript for good (panel gates this behind an
// explicit confirmation).
api.MapDelete("/sessions/{id:guid}/purge", async (
    Guid id,
    SessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    await sessionStore.PurgeSessionAsync(id, cancellationToken);
    await queueControl.RefreshScheduleAsync(cancellationToken);
    events.Publish();
    return Results.NoContent();
});

api.MapGet("/sessions/{id:guid}/messages", async (
    Guid id,
    long? after,
    int? limit,
    SessionStore sessionStore,
    CancellationToken cancellationToken) =>
    Results.Ok(await sessionStore.ReadMessagesAsync(id, after ?? 0, limit ?? 250, cancellationToken)));

api.MapPost("/sessions/{id:guid}/messages", async (
    Guid id,
    SendMessageRequest request,
    SessionOrchestrator orchestrator,
    CancellationToken cancellationToken) =>
    Results.Accepted(value: await orchestrator.SubmitMessageAsync(id, request, cancellationToken)));

api.MapPost("/sessions/{id:guid}/terminal", async (
    Guid id,
    SessionStore sessionStore,
    TerminalLauncher launcher,
    CancellationToken cancellationToken) =>
{
    var session = await sessionStore.FindSessionAsync(id, cancellationToken)
        ?? throw new KeyNotFoundException($"Session {id:D} was not found.");
    await launcher.LaunchAsync(session, cancellationToken);
    return Results.NoContent();
});

api.MapPut("/runtime/pause", (SetPausedRequest request) =>
{
    control.SetPaused(request.Paused);
    queueControl.SetPaused(request.Paused);
    events.Publish();
    return Results.NoContent();
});

api.MapPost("/runtime/stop-current", async (CancellationToken cancellationToken) =>
{
    await backend.StopCurrentAsync(cancellationToken);
    events.Publish();
    return Results.NoContent();
});

api.MapPost("/runtime/login-terminal", (CodexLoginLauncher loginLauncher) =>
{
    if (loginLauncher.TryLaunch(out var message))
    {
        events.Publish();
        return Results.NoContent();
    }
    return Results.Content(message, "text/plain", System.Text.Encoding.UTF8, 409);
});

api.MapGet("/models", async (ModelSelector selector, CancellationToken cancellationToken) =>
    Results.Ok(await selector.ReadModelsAsync(cancellationToken)));

api.MapGet("/archive", async (ProjectArchiveReader archive, CancellationToken cancellationToken) =>
    Results.Ok(await archive.ListProjectsAsync(cancellationToken)));

api.MapGet("/archive/{fingerprint}/sessions/{sessionId:guid}/messages", async (
    string fingerprint,
    Guid sessionId,
    int? limit,
    ProjectArchiveReader archive,
    CancellationToken cancellationToken) =>
    Results.Ok(await archive.ReadMessagesAsync(fingerprint, sessionId, limit ?? 500, cancellationToken)));

api.MapPost("/archive/{fingerprint}/sessions/{sessionId:guid}/import", async (
    string fingerprint,
    Guid sessionId,
    ProjectArchiveReader archive,
    SessionStore sessionStore,
    ILiveDocumentQueueControl queue,
    CancellationToken cancellationToken) =>
{
    // Read-only from the foreign root, then a deterministic seed and one transactional insert into
    // the live runtime.db. The POST /sessions ritual (RefreshScheduleAsync + events.Publish) makes
    // the new session appear over SSE without any client refetch. A missing project/session is a 404
    // (KeyNotFoundException) and an unreadable root is a 409 (InvalidOperationException) via the
    // shared exception middleware.
    var export = await archive.ReadSessionForImportAsync(fingerprint, sessionId, cancellationToken);
    var seed = ImportedSessionSeedBuilder.Build(fingerprint, export);
    var session = await sessionStore.ImportSessionAsync(seed, cancellationToken);
    await queue.RefreshScheduleAsync(cancellationToken);
    events.Publish();
    return Results.Created($"/api/v1/sessions/{session.Id:D}", session);
});

api.MapGet("/health", () =>
{
    var codexProcess = codex.ReadProcessIdentity();
    return Results.Ok(new
    {
        status = "ok",
        bridgeConnected = backend.IsConnected,
        codexRunning = codexProcess is not null,
        codexProcessId = codexProcess?.ProcessId,
        codexProcessStartTimeUtc = codexProcess?.ProcessStartTimeUtc,
        processId = Environment.ProcessId
    });
});

if (developmentDataDirectory is not null)
{
    api.MapGet("/dev/snapshot", async (
        LiveDocumentBackend liveBackend,
        CancellationToken cancellationToken) =>
    {
        using var arguments = JsonDocument.Parse("{}");
        return Results.Ok(await liveBackend.ReadSnapshotAsync(
            arguments.RootElement,
            cancellationToken));
    });
    api.MapGet("/dev/rhino-objects", async (
        LiveDocumentBackend liveBackend,
        CancellationToken cancellationToken) =>
    {
        // 500 is the adapter's hard cap (ValidateListRequest); 1000 made every dev call fail.
        var arguments = JsonSerializer.SerializeToElement(new { limit = 500 });
        return Results.Ok(await liveBackend.ListRhinoObjectsAsync(arguments, cancellationToken));
    });
    api.MapGet("/dev/grasshopper/{objectId:guid}/outputs", async (
        Guid objectId,
        LiveDocumentBackend liveBackend,
        CancellationToken cancellationToken) =>
    {
        var arguments = JsonSerializer.SerializeToElement(new { objectId });
        return Results.Ok(await liveBackend.InspectCanvasOutputsAsync(
            arguments,
            cancellationToken));
    });
    api.MapGet("/dev/grasshopper/{componentId:guid}/python", async (
        Guid componentId,
        LiveDocumentBackend liveBackend,
        CancellationToken cancellationToken) =>
    {
        var arguments = JsonSerializer.SerializeToElement(new
        {
            scopes = new[]
            {
                $"wireify:{componentId:D}",
                $"wireify-messages:{componentId:D}"
            }
        });
        return Results.Ok(await liveBackend.ReadSnapshotAsync(
            arguments,
            cancellationToken));
    });
    api.MapGet("/dev/terminals/{sessionId:guid}", (
        Guid sessionId,
        TerminalLauncher launcher) =>
        Results.Ok(launcher.ReadStatus(sessionId)));
    api.MapPut("/dev/writer/pause", (
        SetPausedRequest request,
        ILiveDocumentQueueControl writerQueue,
        EventHub eventHub) =>
    {
        writerQueue.SetPaused(request.Paused);
        eventHub.Publish();
        return Results.NoContent();
    });
}

app.MapFallback(async context =>
{
    var indexPath = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"), "index.html");
    if (File.Exists(indexPath))
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(indexPath, context.RequestAborted);
        return;
    }
    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync(
        "<html><body><h1>GPTino AgentHost</h1><p>Panel assets are not installed in this build.</p></body></html>",
        context.RequestAborted);
});

await app.RunAsync();

static async Task SendStateEventAsync(
    HttpContext context,
    RuntimeStateProjector projector,
    CancellationToken cancellationToken)
{
    var state = await projector.BuildAsync(cancellationToken);
    var json = JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    await context.Response.WriteAsync($"event: state\ndata: {json}\n\n", cancellationToken);
    await context.Response.Body.FlushAsync(cancellationToken);
}

static async Task WriteErrorAsync(HttpContext context, int statusCode, string code, string message)
{
    if (context.Response.HasStarted)
    {
        return;
    }
    context.Response.StatusCode = statusCode;
    await context.Response.WriteAsJsonAsync(new ApiError(code, message));
}

static bool HasValidApiToken(HttpContext context, string expected)
{
    var header = context.Request.Headers["X-GPTino-Token"].FirstOrDefault();
    var cookie = context.Request.Cookies["gptino_runtime"];
    return TokenEquals(header, expected) || TokenEquals(cookie, expected);
}

// The session's reasoning-effort level (low..ultra) — clamped to the chosen model's advertised set at
// turn time. Legacy profile values (auto/fast/standard/deep) map to the nearest effort for back-compat.
static string NormalizeEffort(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
{
    "low" or "medium" or "high" or "xhigh" or "max" or "ultra" => (value ?? string.Empty).Trim().ToLowerInvariant(),
    "fast" or "fast-safe" => "low",
    "standard" => "medium",
    "deep" or "high-assurance" or "recovery" or "auto" or "" => "xhigh",
    "extra-high" => "xhigh",
    "maximum" => "max",
    "minimal" => "low",
    _ => throw new ArgumentException("Reasoning effort must be one of low, medium, high, xhigh, max, ultra.")
};

static bool TokenEquals(string? supplied, string expected)
{
    if (string.IsNullOrEmpty(supplied))
    {
        return false;
    }
    var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
    var expectedBytes = Encoding.UTF8.GetBytes(expected);
    return suppliedBytes.Length == expectedBytes.Length &&
        CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
}
