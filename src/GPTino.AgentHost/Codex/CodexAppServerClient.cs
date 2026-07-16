using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Hosting;

namespace GPTino.AgentHost.Codex;

public sealed class CodexAppServerClient : IModelCatalog, IAsyncDisposable
{
    private readonly AgentHostOptions _options;
    private readonly ILogger<CodexAppServerClient> _logger;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private Process? _process;
    private StreamWriter? _input;
    private Task? _readLoop;
    private long _nextId;
    private bool _initialized;
    private bool _disposed;

    public CodexAppServerClient(
        AgentHostOptions options,
        ILogger<CodexAppServerClient> logger)
    {
        _options = options;
        _logger = logger;
    }

    public event Func<string, JsonElement, Task>? NotificationReceived;

    public Func<DynamicToolCall, CancellationToken, Task<DynamicToolResult>>? DynamicToolHandler { get; set; }

    public bool IsRunning => _process is { HasExited: false } && _initialized;

    public async Task<IReadOnlyList<ModelView>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var result = await CallAsync("model/list", new { limit = 100 }, cancellationToken).ConfigureAwait(false);
        var models = new List<ModelView>();
        if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return models;
        }

        foreach (var item in data.EnumerateArray())
        {
            var id = ReadString(item, "id") ?? ReadString(item, "model") ?? "unknown";
            var model = ReadString(item, "model") ?? id;
            var efforts = item.TryGetProperty("supportedReasoningEfforts", out var effortItems)
                ? effortItems.EnumerateArray()
                    .Select(value => ReadString(value, "reasoningEffort"))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Cast<string>()
                    .ToArray()
                : [];
            models.Add(new ModelView(
                id,
                model,
                ReadString(item, "displayName") ?? model,
                ReadString(item, "description") ?? string.Empty,
                item.TryGetProperty("isDefault", out var isDefault) && isDefault.GetBoolean(),
                efforts));
        }
        return models;
    }

    public async Task<string> StartThreadAsync(
        string cwd,
        string? model,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["cwd"] = Path.GetFullPath(cwd),
            ["approvalPolicy"] = "never",
            ["sandbox"] = "read-only",
            ["personality"] = "pragmatic",
            ["historyMode"] = "paginated",
            ["baseInstructions"] = ThreadInstructions,
            ["dynamicTools"] = DynamicToolSpecs.Create()
        };
        if (!string.IsNullOrWhiteSpace(model))
        {
            parameters["model"] = model;
        }

        var result = await CallAsync("thread/start", parameters, cancellationToken).ConfigureAwait(false);
        return result.GetProperty("thread").GetProperty("id").GetString()
            ?? throw new CodexProtocolException("thread/start did not return a thread id.");
    }

    public async Task ResumeThreadAsync(
        string threadId,
        string cwd,
        string? model,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["threadId"] = threadId,
            ["cwd"] = Path.GetFullPath(cwd),
            ["approvalPolicy"] = "never",
            ["sandbox"] = "read-only",
            ["baseInstructions"] = ThreadInstructions,
            ["excludeTurns"] = true
        };
        if (!string.IsNullOrWhiteSpace(model))
        {
            parameters["model"] = model;
        }
        _ = await CallAsync("thread/resume", parameters, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> StartTurnAsync(
        string threadId,
        string message,
        string? model,
        string? effort,
        CancellationToken cancellationToken = default)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["threadId"] = threadId,
            ["input"] = new[] { new { type = "text", text = message } },
            ["approvalPolicy"] = "never"
        };
        if (!string.IsNullOrWhiteSpace(model))
        {
            parameters["model"] = model;
        }
        if (!string.IsNullOrWhiteSpace(effort))
        {
            parameters["effort"] = effort;
        }

        var result = await CallAsync("turn/start", parameters, cancellationToken).ConfigureAwait(false);
        return result.GetProperty("turn").GetProperty("id").GetString()
            ?? throw new CodexProtocolException("turn/start did not return a turn id.");
    }

    public async Task InterruptTurnAsync(string threadId, string turnId, CancellationToken cancellationToken = default)
    {
        _ = await CallAsync("turn/interrupt", new { threadId, turnId }, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            StopProcess();
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _startGate.Dispose();
        _writeGate.Dispose();
    }

    private async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        return await CallCoreAsync(method, parameters, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (IsRunning)
        {
            return;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return;
            }
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopProcess();

            var executable = ResolveCodexExecutable();
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetFullPath(_options.ProjectDirectory)
            };
            startInfo.ArgumentList.Add("app-server");
            startInfo.ArgumentList.Add("--stdio");
            RemoveGptinoEnvironment(startInfo);
            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Codex App Server process could not be started.");
            _input = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false))
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            _readLoop = ReadLoopAsync(_process, _process.StandardOutput, CancellationToken.None);
            _ = ReadErrorLoopAsync(_process.StandardError, CancellationToken.None);

            _ = await CallCoreAsync(
                "initialize",
                new
                {
                    clientInfo = new { name = "gptino-agent-host", title = "GPTino", version = "0.1.0" },
                    capabilities = new { experimentalApi = true }
                },
                cancellationToken).ConfigureAwait(false);
            await WriteAsync(new { method = "initialized", @params = new { } }, cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        catch
        {
            StopProcess();
            throw;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<JsonElement> CallCoreAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion))
        {
            throw new InvalidOperationException("Duplicate JSON-RPC request id.");
        }

        using var registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        try
        {
            await WriteAsync(new { id, method, @params = parameters }, cancellationToken).ConfigureAwait(false);
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync(Process process, StreamReader output, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested && !process.HasExited)
            {
                var line = await output.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("method", out var methodElement))
                {
                    var method = methodElement.GetString() ?? string.Empty;
                    var parameters = root.TryGetProperty("params", out var paramsElement)
                        ? paramsElement.Clone()
                        : EmptyObject();
                    if (root.TryGetProperty("id", out var serverRequestId))
                    {
                        _ = HandleServerRequestAsync(serverRequestId.Clone(), method, parameters);
                    }
                    else
                    {
                        await RaiseNotificationAsync(method, parameters).ConfigureAwait(false);
                    }
                    continue;
                }

                if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id) || !_pending.TryGetValue(id, out var pending))
                {
                    _logger.LogWarning("Ignoring unmatched Codex JSON-RPC payload: {Payload}", line);
                    continue;
                }
                if (root.TryGetProperty("error", out var error))
                {
                    pending.TrySetException(new CodexProtocolException(error.GetRawText()));
                }
                else if (root.TryGetProperty("result", out var result))
                {
                    pending.TrySetResult(result.Clone());
                }
                else
                {
                    pending.TrySetException(new CodexProtocolException("JSON-RPC response had neither result nor error."));
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            _logger.LogError(exception, "Codex App Server output loop stopped unexpectedly.");
        }
        finally
        {
            _initialized = false;
            var exception = failure ?? new EndOfStreamException("Codex App Server closed its output stream.");
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(exception);
            }
        }
    }

    private async Task HandleServerRequestAsync(JsonElement id, string method, JsonElement parameters)
    {
        try
        {
            if (method == "item/tool/call" && DynamicToolHandler is not null)
            {
                var call = DynamicToolCall.FromJson(parameters);
                var result = await DynamicToolHandler(call, CancellationToken.None).ConfigureAwait(false);
                await WriteRpcResultAsync(id, result.ToProtocolResult(), CancellationToken.None).ConfigureAwait(false);
                return;
            }

            await WriteRpcErrorAsync(id, -32601, $"Unsupported server request: {method}", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Codex server request {Method} failed.", method);
            await WriteRpcErrorAsync(id, -32000, exception.Message, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RaiseNotificationAsync(string method, JsonElement parameters)
    {
        var handlers = NotificationReceived;
        if (handlers is null)
        {
            return;
        }
        foreach (var handler in handlers.GetInvocationList().Cast<Func<string, JsonElement, Task>>())
        {
            try
            {
                await handler(method, parameters).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Codex notification handler failed for {Method}.", method);
            }
        }
    }

    private async Task ReadErrorLoopAsync(StreamReader error, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await error.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(line))
            {
                _logger.LogDebug("Codex App Server: {Message}", line);
            }
        }
    }

    private Task WriteRpcResultAsync(JsonElement id, object result, CancellationToken cancellationToken)
    {
        var node = new JsonObject
        {
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["result"] = JsonSerializer.SerializeToNode(result)
        };
        return WriteAsync(node, cancellationToken);
    }

    private Task WriteRpcErrorAsync(JsonElement id, int code, string message, CancellationToken cancellationToken)
    {
        var node = new JsonObject
        {
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        };
        return WriteAsync(node, cancellationToken);
    }

    private async Task WriteAsync(object payload, CancellationToken cancellationToken)
    {
        var input = _input ?? throw new InvalidOperationException("Codex App Server is not connected.");
        var json = JsonSerializer.Serialize(payload, JsonDefaults.Options);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await input.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private string ResolveCodexExecutable()
    {
        if (!string.IsNullOrWhiteSpace(_options.CodexExecutable))
        {
            return ResolveConfiguredCodex(_options.CodexExecutable);
        }
        var configured = Environment.GetEnvironmentVariable("CODEX_EXECUTABLE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return ResolveConfiguredCodex(configured);
        }
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var bundled = Path.Combine(userProfile, ".codex", ".sandbox-bin", "codex.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var npmDirectory = Path.Combine(roaming, "npm");
        var npmNative = ResolveNpmNativeExecutable(npmDirectory);
        if (npmNative is not null)
        {
            return npmNative;
        }

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                     .Select(value => value.Trim().Trim('"'))
                     .Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            string fullDirectory;
            try
            {
                fullDirectory = Path.GetFullPath(directory);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                continue;
            }
            var executable = Path.Combine(fullDirectory, "codex.exe");
            if (File.Exists(executable))
            {
                return executable;
            }
            npmNative = ResolveNpmNativeExecutable(fullDirectory);
            if (npmNative is not null)
            {
                return npmNative;
            }
        }

        throw new FileNotFoundException(
            "Codex CLI was not found. Install it with npm, complete 'codex login', or set CODEX_EXECUTABLE to the native codex.exe (an npm codex.cmd shim is also accepted)." );
    }

    private static void RemoveGptinoEnvironment(ProcessStartInfo startInfo)
    {
        foreach (var key in startInfo.Environment.Keys
                     .Where(key => key.StartsWith("GPTINO_", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            startInfo.Environment.Remove(key);
        }
    }

    private static string ResolveConfiguredCodex(string configured)
    {
        var path = Path.GetFullPath(configured);
        if (File.Exists(path) && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }
        if (File.Exists(path) && Path.GetFileName(path).StartsWith("codex.", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveNpmNativeExecutable(Path.GetDirectoryName(path)!)
                ?? throw new FileNotFoundException(
                    "The Codex npm shim exists, but its platform-native codex.exe was not found.",
                    path);
        }
        throw new FileNotFoundException("The configured Codex executable was not found or is not codex.exe.", path);
    }

    private static string? ResolveNpmNativeExecutable(string npmDirectory)
    {
        if (string.IsNullOrWhiteSpace(npmDirectory) || !Directory.Exists(npmDirectory))
        {
            return null;
        }
        var (packageSuffix, target) = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => ("x64", "x86_64-pc-windows-msvc"),
            Architecture.Arm64 => ("arm64", "aarch64-pc-windows-msvc"),
            _ => (string.Empty, string.Empty)
        };
        if (packageSuffix.Length == 0)
        {
            return null;
        }
        var packageRoot = Path.Combine(npmDirectory, "node_modules", "@openai", "codex");
        var candidates = new[]
        {
            Path.Combine(
                packageRoot,
                "node_modules",
                "@openai",
                $"codex-win32-{packageSuffix}",
                "vendor",
                target,
                "bin",
                "codex.exe"),
            Path.Combine(packageRoot, "vendor", target, "bin", "codex.exe"),
            Path.Combine(packageRoot, "vendor", target, "codex.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private void StopProcess()
    {
        _initialized = false;
        var process = Interlocked.Exchange(ref _process, null);
        _input = null;
        if (process is null)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private const string ThreadInstructions = """
        You are a GPTino modeling session attached to one explicit Rhino/Grasshopper document pair.
        You may inspect immutable state in parallel with other sessions. Never mutate Rhino or Grasshopper through shell,
        files, or an active-document fallback. Use only gptino_v1 tools for document state and change submission.
        Start modeling work with snapshot_read; its sessionId and target.projectId are the exact IDs required by ChangeSet.
        Use component_catalog before creating Grasshopper components and rhino_list before broad Rhino scene edits.
        Draft and validate code before calling change_submit. The central broker owns ordering, conflict checks, the writer
        lease, live execution, verification, and history. A submitted change is not successful until job_status reports a
        verified terminal result. Preserve document units, tolerances, data trees, and existing wiring unless requested.
        For complex work, iterate in session artifacts, inspect runtime messages, and correct deterministic pre-write failures
        instead of guessing. Re-read a fresh snapshot before resubmitting and use a new idempotency key for changed content.
        If a job reports recoveryRequired, stop automatic mutation and explain the uncertain live state to the user.
        """;
}

public sealed record DynamicToolCall(
    string CallId,
    string ThreadId,
    string TurnId,
    string? Namespace,
    string Tool,
    JsonElement Arguments)
{
    public static DynamicToolCall FromJson(JsonElement value) =>
        new(
            value.GetProperty("callId").GetString() ?? throw new CodexProtocolException("Missing callId."),
            value.GetProperty("threadId").GetString() ?? throw new CodexProtocolException("Missing threadId."),
            value.GetProperty("turnId").GetString() ?? throw new CodexProtocolException("Missing turnId."),
            value.TryGetProperty("namespace", out var toolNamespace) ? toolNamespace.GetString() : null,
            value.GetProperty("tool").GetString() ?? throw new CodexProtocolException("Missing tool name."),
            value.GetProperty("arguments").Clone());
}

public sealed record DynamicToolResult(bool Success, string Text)
{
    public object ToProtocolResult() => new
    {
        success = Success,
        contentItems = new[] { new { type = "inputText", text = Text } }
    };

    public static DynamicToolResult Ok(object value) =>
        new(true, JsonSerializer.Serialize(value, JsonDefaults.Options));

    public static DynamicToolResult Fail(string message) => new(false, message);
}

public sealed class CodexProtocolException(string message) : InvalidOperationException(message);

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
}
