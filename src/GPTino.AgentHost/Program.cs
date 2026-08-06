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
// No policy cap on message-attachment size: the user decides what is worth attaching (large images
// cost tokens/context, which is their call, not ours). Kestrel's default ~28 MiB body cap would
// otherwise reject a big paste with an opaque 413, so lift it entirely. This host is loopback-only
// and token-gated (see the middleware below), so an unbounded body is not an external DoS surface.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = null);
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
builder.Services.AddSingleton<DataLibrary>();
builder.Services.AddSingleton<IStructuralSolver>(services =>
    new PythonStructuralSolver(services.GetRequiredService<DataLibrary>()));
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
builder.Services.AddSingleton<ILayoutTidyService>(services =>
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
await queueControl.RefreshScheduleAsync();

// The Codex login/install flow happens in an external terminal while the panel sits on its login
// gate; nothing in that flow touches an endpoint, so no activity-driven Publish() would ever
// re-project the flipped auth state. This watcher is the missing publisher: it re-reads the probe
// on a cadence just above its cache TTL and publishes ONLY on a status change, so the gate (and
// the header chip) lift by themselves shortly after auth.json appears — and re-gate if it goes.
var authProbe = app.Services.GetRequiredService<CodexAuthProbe>();
_ = Task.Run(async () =>
{
    var lastStatus = authProbe.Read().Status;
    try
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
        while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
        {
            var status = authProbe.Read().Status;
            if (status == lastStatus)
            {
                continue;
            }
            lastStatus = status;
            events.Publish();
        }
    }
    catch (OperationCanceledException)
    {
        // Host shutdown.
    }
});

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

// Layer table + named layer states.
api.MapGet("/layers", async (
    LiveDocumentBackend liveBackend,
    CancellationToken cancellationToken) =>
    Results.Ok(await liveBackend.ReadRhinoLayersAsync(cancellationToken)));

// Viewport focus for the audit card: select + zoom the objects behind a finding, optionally
// isolating or locking everything else so the user can judge it themselves. Not a ChangeSet step —
// a human pressed a row — and deliberately absent from the agent's tool schema.
api.MapPost("/focus", async (
    FocusRequest request,
    LiveDocumentBackend liveBackend,
    CancellationToken cancellationToken) =>
{
    var arguments = JsonSerializer.SerializeToElement(new
    {
        objectIds = request.ObjectIds ?? Array.Empty<Guid>(),
        mode = string.IsNullOrWhiteSpace(request.Mode) ? "select" : request.Mode,
        zoom = request.Zoom ?? true,
    });
    return Results.Ok(await liveBackend.FocusRhinoObjectsAsync(arguments, cancellationToken));
});

// Canvas focus for the panel's [[ghfocus:…]] chip: select + frame the Grasshopper components the
// conversation is pointing at, the canvas mirror of POST /focus. View-only (selection + viewport),
// never a ChangeSet step, and absent from the agent's tool schema.
api.MapPost("/canvas/focus", async (
    CanvasFocusEndpointRequest request,
    LiveDocumentBackend liveBackend,
    CancellationToken cancellationToken) =>
{
    var arguments = JsonSerializer.SerializeToElement(new
    {
        objectIds = request.ObjectIds ?? Array.Empty<Guid>(),
        zoom = request.Zoom ?? true,
    });
    return Results.Ok(await liveBackend.FocusCanvasObjectsAsync(arguments, cancellationToken));
});

// The complete current selection (Rhino objects + Grasshopper components) for the composer's "pin
// selection" affordance. Unlike the streamed runtime state — which caps ids to keep SSE frames slim —
// this returns every id (up to the plugin's selection cap) so a pinned set is the full selection the
// user captured, never a silent 32-object truncation. Read-only snapshot at call time.
api.MapGet("/selection/current", (LiveDocumentBackend liveBackend) =>
{
    var selection = liveBackend.CurrentSelection;
    if (selection is null)
    {
        return Results.Ok(new
        {
            rhinoObjectIds = Array.Empty<string>(),
            grasshopperObjects = Array.Empty<object>(),
            activeLayer = (string?)null,
        });
    }
    return Results.Ok(new
    {
        rhinoObjectIds = selection.RhinoObjectIds.Select(id => id.ToString("D")).ToArray(),
        grasshopperObjects = (selection.GrasshopperObjects ?? [])
            .Select(item => new
            {
                id = item.ObjectId.ToString("D"),
                label = string.IsNullOrWhiteSpace(item.NickName) ? item.Name : item.NickName,
            })
            .ToArray(),
        activeLayer = selection.ActiveLayerName,
    });
});

// Which language GPTino writes its prose in. A project-level preference (not per session):
// the panel toggles it, and the next thread start/resume composes it into instructions.
api.MapGet("/language", (ProjectContextStore context) =>
    Results.Ok(new LanguageSetting(context.ReadLanguage())));

api.MapPost("/language", (LanguageSetting request, ProjectContextStore context) =>
{
    context.WriteLanguage(request.Language);
    return Results.Ok(new LanguageSetting(context.ReadLanguage()));
});

// Goal cards travel as camelCase JSON in one column and one SSE field; the panel and the agent
// tool both read this exact shape.
var GoalCardJson = new JsonSerializerOptions(JsonSerializerDefaults.Web);
// The user's answer to a proposed approval card. Approving mints ONE grant bound to exactly the
// (objectId, fingerprint) pairs of the items they ticked — not the whole proposal — and stores it
// on the card so the agent's next turn can carry it in a ChangeSet. A human pressed this.
api.MapPut("/sessions/{id:guid}/approval", async (
    Guid id,
    AnswerApprovalRequest request,
    SessionStore sessionStore,
    LiveDocumentBackend liveBackend,
    CancellationToken cancellationToken) =>
{
    var session = await sessionStore.FindSessionAsync(id, cancellationToken);
    if (session?.ApprovalCard is null)
    {
        return Results.NotFound(new ApiError("approval_card_absent", "This session has no approval card to answer."));
    }
    var card = JsonSerializer.Deserialize<ApprovalCard>(session.ApprovalCard, GoalCardJson);
    if (card is null)
    {
        return Results.NotFound(new ApiError("approval_card_unreadable", "The stored approval card could not be read."));
    }
    if (!string.Equals(request.Status, "granted", StringComparison.OrdinalIgnoreCase))
    {
        await sessionStore.SetApprovalCardAsync(
            id,
            JsonSerializer.Serialize(card with { Status = "rejected" }, GoalCardJson),
            cancellationToken);
        events.Publish();
        return Results.NoContent();
    }
    var approvedIds = request.ApprovedItemIds ?? [];
    var targets = card.Items
        .Where(item => approvedIds.Contains(item.Id))
        .SelectMany(item => item.Targets)
        .Select(target => (target.ObjectId, target.Fingerprint))
        .ToArray();
    if (targets.Length == 0)
    {
        return Results.BadRequest(new ApiError("nothing_approved", "Approving requires at least one item."));
    }
    var grantJson = JsonSerializer.SerializeToElement(liveBackend.MintApprovalGrant(targets), GoalCardJson);
    // Choices are kept only for items that were actually granted: a choice attached to a refused
    // item is not a decision the user made about anything that will happen, and injecting it next
    // turn would read as permission to act on the item they declined.
    var choices = request.Choices?
        .Where(entry => approvedIds.Contains(entry.Key))
        .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
    var updated = card with
    {
        Status = "granted",
        GrantId = grantJson.GetProperty("grantId").GetString(),
        ApprovedItemIds = approvedIds.ToArray(),
        Choices = choices is { Count: > 0 } ? choices : null,
    };
    await sessionStore.SetApprovalCardAsync(id, JsonSerializer.Serialize(updated, GoalCardJson), cancellationToken);
    events.Publish();
    return Results.NoContent();
});

api.MapPost("/sessions", async (
    CreateSessionRequest request,
    SessionStore sessionStore,
    ILiveDocumentQueueControl queue,
    CancellationToken cancellationToken) =>
{
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

api.MapPut("/sessions/{id:guid}/title", async (
    Guid id,
    RenameSessionRequest request,
    SessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    await sessionStore.SetSessionTitleAsync(id, request.Name, cancellationToken);
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


// The user's verdict on a proposed goal card. Confirming (optionally with edits) is what the
// agent is held to afterwards: the confirmed card rides every following turn's input, and the
// self-score at the end must answer these criteria. A human pressed this — never an agent step.
api.MapPut("/sessions/{id:guid}/goal", async (
    Guid id,
    SetGoalRequest request,
    SessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    var session = await sessionStore.FindSessionAsync(id, cancellationToken);
    if (session?.GoalCard is null)
    {
        return Results.NotFound(new ApiError("goal_card_absent", "This session has no goal card to answer."));
    }
    var card = JsonSerializer.Deserialize<GoalCard>(session.GoalCard, GoalCardJson);
    if (card is null)
    {
        return Results.NotFound(new ApiError("goal_card_unreadable", "The stored goal card could not be read."));
    }
    var status = string.Equals(request.Status, "confirmed", StringComparison.OrdinalIgnoreCase)
        ? "confirmed"
        : "rejected";
    var updated = card with
    {
        Status = status,
        // Edits win: the user is held to what they approved, not to what was proposed.
        Objective = string.IsNullOrWhiteSpace(request.Objective) ? card.Objective : request.Objective!,
        Criteria = request.Criteria is { Count: > 0 } ? request.Criteria : card.Criteria,
        ChosenOption = request.ChosenOption ?? card.ChosenOption,
        ConfirmedAt = DateTimeOffset.UtcNow,
    };
    await sessionStore.SetGoalCardAsync(id, JsonSerializer.Serialize(updated, GoalCardJson), cancellationToken);
    events.Publish();
    return Results.NoContent();
});

// Soft-delete: hide from the active list but keep everything, so it can be restored.
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
    // Same rationale as /dev/audit: the structural-extract live gate needs the extraction result
    // with no model in the loop. Product surface is the structural_extract tool.
    api.MapGet("/dev/structural-extract", async (
        string? layerFilter,
        double? prototypeHeight,
        LiveDocumentBackend liveBackend,
        CancellationToken cancellationToken) =>
    {
        // Omitted, not null: StructuralExtractRequest's threshold properties are non-nullable
        // (they carry validated defaults), so a null in the JSON fails deserialization outright.
        var query = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(layerFilter))
        {
            query["layerFilter"] = layerFilter;
        }
        if (prototypeHeight is > 0)
        {
            query["prototypeHeight"] = prototypeHeight.Value;
        }
        var arguments = JsonSerializer.SerializeToElement(query);
        return Results.Ok(await liveBackend.ReadStructuralExtractAsync(arguments, cancellationToken));
    });
    // Server-computed document audit, for harnesses that must check a claim WITHOUT asking the
    // agent whether it is true. The product surface for this is the rhino_audit tool; this is the
    // same backend call with no model in the loop, which is what a live gate needs to grade one.
    api.MapGet("/dev/audit", async (
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
