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
var identity = new RuntimeIdentity(
    options.ProjectId,
    options.RhinoPath,
    options.GrasshopperPath,
    options.ProjectDirectory,
    DateTimeOffset.UtcNow);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(identity);
builder.Services.AddSingleton(new SessionStore(Path.Combine(options.ResolveDataDirectory(), "runtime.db")));
builder.Services.AddSingleton<RuntimeControl>();
builder.Services.AddSingleton<EventHub>();
builder.Services.AddSingleton<EndpointRegistry>();
builder.Services.AddSingleton<PanelBootstrapNonceStore>();
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
builder.Services.AddSingleton<MessageRoutingPolicy>();
builder.Services.AddSingleton<EffectiveModelState>();
builder.Services.AddSingleton<ModelSelector>();
builder.Services.AddSingleton<DynamicToolDispatcher>();
builder.Services.AddSingleton<SessionOrchestrator>();
builder.Services.AddSingleton<RuntimeStateProjector>();
builder.Services.AddSingleton<TerminalLauncher>();
builder.Services.AddHostedService<ReadySignalService>();
builder.Services.AddHostedService<ParentProcessMonitor>();

var app = builder.Build();
var store = app.Services.GetRequiredService<SessionStore>();
await store.InitializeAsync();
var events = app.Services.GetRequiredService<EventHub>();
var control = app.Services.GetRequiredService<RuntimeControl>();
var backend = app.Services.GetRequiredService<ILiveDocumentBackend>();
var codex = app.Services.GetRequiredService<CodexAppServerClient>();
var dispatcher = app.Services.GetRequiredService<DynamicToolDispatcher>();
var queueControl = app.Services.GetRequiredService<ILiveDocumentQueueControl>();
_ = app.Services.GetRequiredService<SessionOrchestrator>();
codex.DynamicToolHandler = dispatcher.DispatchAsync;
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

api.MapPost("/sessions", async (
    CreateSessionRequest request,
    SessionStore sessionStore,
    ILiveDocumentQueueControl queue,
    CancellationToken cancellationToken) =>
{
    var session = await sessionStore.CreateSessionAsync(request, cancellationToken);
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

api.MapPut("/sessions/{id:guid}/mode", async (
    Guid id,
    SetModeRequest request,
    SessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    var role = request.Mode.Trim().ToLowerInvariant() switch
    {
        "plan" => "planner",
        "auto" => "modeler",
        _ => throw new ArgumentException("Mode must be 'plan' or 'auto'.")
    };
    await sessionStore.UpdatePreferencesAsync(id, role, null, null, false, cancellationToken);
    events.Publish();
    return Results.NoContent();
});

api.MapPut("/sessions/{id:guid}/model", async (
    Guid id,
    SetModelRequest request,
    SessionStore sessionStore,
    CancellationToken cancellationToken) =>
{
    var profile = request.ModelProfile.Trim().ToLowerInvariant() switch
    {
        "auto" => "auto",
        "fast" => "fast-safe",
        "standard" => "standard",
        "deep" => "high-assurance",
        _ => throw new ArgumentException("Model profile must be auto, fast, standard, or deep.")
    };
    await sessionStore.UpdatePreferencesAsync(
        id,
        null,
        profile,
        request.Model,
        true,
        cancellationToken);
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

api.MapGet("/models", async (ModelSelector selector, CancellationToken cancellationToken) =>
    Results.Ok(await selector.ReadModelsAsync(cancellationToken)));

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
        var arguments = JsonSerializer.SerializeToElement(new { limit = 1000 });
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
