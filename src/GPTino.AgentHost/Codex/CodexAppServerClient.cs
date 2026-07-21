using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using GPTino.AgentHost.Api;
using GPTino.AgentHost.Hosting;

namespace GPTino.AgentHost.Codex;

public sealed class CodexAppServerClient : ICodexSessionClient, IModelCatalog, IAsyncDisposable
{
    private static readonly TimeSpan DynamicToolCallTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NotificationDrainTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan McpDiscoveryTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan McpDiscoveryCleanupTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StartupFailureCooldown = TimeSpan.FromSeconds(30);
    private const int StandardErrorReadBufferCharacters = 1_024;
    private const int MaximumLoggedStandardErrorRecords = 32;
    private static readonly string[] IsolatedFeatureOverrides =
    [
        "features.plugins=false",
        "features.apps=false",
        "features.remote_plugin=false",
        "features.enable_mcp_apps=false",
        "features.plugin_sharing=false"
    ];
    private static readonly string[] DisabledDirectMcpNames =
    [
        "cordyceps",
        "wireify",
        "rhino"
    ];
    private readonly AgentHostOptions _options;
    private readonly ILogger<CodexAppServerClient> _logger;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<long, PendingRequest> _pending = new();
    private Process? _process;
    private ProcessGeneration? _processGeneration;
    private Task? _readLoop;
    private long _nextId;
    private long _nextProcessGeneration;
    private bool _initialized;
    private bool _disposed;
    private DateTimeOffset _startupRetryNotBeforeUtc;

    public CodexAppServerClient(
        AgentHostOptions options,
        ILogger<CodexAppServerClient> logger,
        IThreadInstructionComposer? instructionComposer = null)
    {
        _options = options;
        _logger = logger;
        _instructionComposer = instructionComposer;
    }

    private readonly IThreadInstructionComposer? _instructionComposer;

    private string ComposeBaseInstructions() =>
        _instructionComposer?.Compose(ThreadInstructions) ?? ThreadInstructions;

    public event Func<string, JsonElement, Task>? NotificationReceived;

    public Func<DynamicToolCall, CancellationToken, Task<DynamicToolResult>>? DynamicToolHandler { get; set; }

    public bool IsRunning => TryGetRunningGeneration(out _);

    public CodexProcessIdentity? ReadProcessIdentity()
    {
        var process = Volatile.Read(ref _process);
        if (process is null)
        {
            return null;
        }
        try
        {
            process.Refresh();
            if (process.HasExited)
            {
                return null;
            }
            return new CodexProcessIdentity(
                process.Id,
                process.StartTime.ToUniversalTime());
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

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
        var parameters = CreateThreadStartParameters(cwd, model);
        parameters["baseInstructions"] = ComposeBaseInstructions();

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
            ["baseInstructions"] = ComposeBaseInstructions(),
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

    public async Task<CodexTurnReadResult?> ReadTurnAsync(
        string threadId,
        string turnId,
        CancellationToken cancellationToken = default)
    {
        var result = await CallAsync(
            "thread/read",
            CreateThreadReadParameters(threadId),
            cancellationToken).ConfigureAwait(false);
        return ParseThreadReadResult(result, turnId);
    }

    public async Task StopAsync()
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopProcessAsync().ConfigureAwait(false);
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
        var generation = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        return await CallCoreAsync(method, parameters, cancellationToken, generation).ConfigureAwait(false);
    }

    private async Task<ProcessGeneration> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (TryGetRunningGeneration(out var runningGeneration))
        {
            return runningGeneration;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetRunningGeneration(out runningGeneration))
            {
                return runningGeneration;
            }
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_startupRetryNotBeforeUtc > DateTimeOffset.UtcNow)
            {
                throw new CodexProtocolException(
                    "Codex startup is temporarily paused after a recent failure.");
            }
            await StopProcessAsync().ConfigureAwait(false);

            var executable = ResolveCodexExecutable();
            var mcpListStartInfo = CreateMcpListProcessStartInfo(executable, _options.ProjectDirectory);
            var effectiveMcpNames = await EnumerateEffectiveMcpNamesAsync(
                    mcpListStartInfo,
                    McpDiscoveryTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var startInfo = CreateProcessStartInfo(mcpListStartInfo, effectiveMcpNames);
            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Codex App Server process could not be started.");
            var input = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false))
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            var processGeneration = new ProcessGeneration(
                Interlocked.Increment(ref _nextProcessGeneration),
                input,
                RaiseNotificationAsync,
                _logger);
            _processGeneration = processGeneration;
            _readLoop = ReadLoopAsync(_process, _process.StandardOutput, processGeneration);
            _ = ReadErrorLoopAsync(_process.StandardError, processGeneration.Token);

            _ = await CallCoreAsync(
                "initialize",
                new
                {
                    clientInfo = new { name = "gptino-agent-host", title = "GPTino", version = "0.1.0" },
                    capabilities = new { experimentalApi = true }
                },
                cancellationToken,
                processGeneration).ConfigureAwait(false);
            using (var initializedLifetime = CancellationTokenSource.CreateLinkedTokenSource(
                       cancellationToken,
                       processGeneration.Token))
            {
                await WriteAsync(
                    new { method = "initialized", @params = new { } },
                    initializedLifetime.Token,
                    processGeneration).ConfigureAwait(false);
            }
            Volatile.Write(ref _initialized, true);
            _startupRetryNotBeforeUtc = default;
            return processGeneration;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopProcessAsync().ConfigureAwait(false);
            throw;
        }
        catch
        {
            _startupRetryNotBeforeUtc = DateTimeOffset.UtcNow + StartupFailureCooldown;
            await StopProcessAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<JsonElement> CallCoreAsync(
        string method,
        object parameters,
        CancellationToken cancellationToken,
        ProcessGeneration processGeneration)
    {
        using var callLifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            processGeneration.Token);
        var effectiveCancellationToken = callLifetime.Token;
        effectiveCancellationToken.ThrowIfCancellationRequested();

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, new PendingRequest(processGeneration.Id, completion)))
        {
            throw new InvalidOperationException("Duplicate JSON-RPC request id.");
        }

        using var registration = effectiveCancellationToken.Register(
            () => completion.TrySetCanceled(effectiveCancellationToken));
        try
        {
            await WriteAsync(
                new { id, method, @params = parameters },
                effectiveCancellationToken,
                processGeneration).ConfigureAwait(false);
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync(
        Process process,
        StreamReader output,
        ProcessGeneration processGeneration)
    {
        var cancellationToken = processGeneration.Token;
        Exception? failure = null;
        try
        {
            // The process may exit after writing its final response. The redirected pipe can still
            // contain buffered NDJSON at that point, so process.HasExited must not short-circuit the
            // read. Read until EOF (or explicit generation cancellation) instead.
            while (!cancellationToken.IsCancellationRequested)
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

                if (!TryParseOutputLine(line, out var root, out _))
                {
                    throw new CodexProtocolException(
                        $"Codex App Server emitted malformed NDJSON ({line.Length} characters). " +
                        "The process generation was stopped to avoid orphaning an RPC response.");
                }

                await ProcessOutputPayloadAsync(root, line, processGeneration).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
            _logger.LogError(exception, "Codex App Server output loop stopped unexpectedly.");
        }
        finally
        {
            if (ReferenceEquals(Volatile.Read(ref _processGeneration), processGeneration))
            {
                Volatile.Write(ref _initialized, false);
            }

            var wasCanceled = cancellationToken.IsCancellationRequested;
            if (wasCanceled)
            {
                CompletePendingForGeneration(
                    processGeneration.Id,
                    pending => pending.TrySetCanceled(cancellationToken));
            }
            else
            {
                var exception = failure ?? new EndOfStreamException("Codex App Server closed its output stream.");
                CompletePendingForGeneration(
                    processGeneration.Id,
                    pending => pending.TrySetException(exception));
            }
            processGeneration.Cancel();
            await processGeneration.StopNotificationsAsync(NotificationDrainTimeout).ConfigureAwait(false);
        }
    }

    private Task ProcessOutputPayloadAsync(
        JsonElement root,
        string originalLine,
        ProcessGeneration processGeneration)
    {
        if (root.TryGetProperty("method", out var methodElement))
        {
            var method = methodElement.GetString() ?? string.Empty;
            var parameters = root.TryGetProperty("params", out var paramsElement)
                ? paramsElement.Clone()
                : EmptyObject();
            if (root.TryGetProperty("id", out var serverRequestId))
            {
                // Do not run any part of a user-supplied dynamic-tool handler on the stdout pump.
                // A handler is allowed to do synchronous work before returning its Task; scheduling
                // the whole request keeps RPC responses and completion notifications flowing.
                _ = Task.Run(
                    () => HandleServerRequestAsync(
                        serverRequestId.Clone(),
                        method,
                        parameters,
                        processGeneration),
                    CancellationToken.None);
            }
            else
            {
                if (!processGeneration.EnqueueNotification(method, parameters))
                {
                    _logger.LogDebug(
                        "Dropping Codex notification {Method} because process generation {GenerationId} is stopping.",
                        method,
                        processGeneration.Id);
                }
            }
            return Task.CompletedTask;
        }

        if (!root.TryGetProperty("id", out var idElement) ||
            !idElement.TryGetInt64(out var id) ||
            !_pending.TryGetValue(id, out var pending) ||
            pending.GenerationId != processGeneration.Id)
        {
            _logger.LogWarning(
                "Ignoring unmatched Codex JSON-RPC payload ({CharacterCount} characters).",
                originalLine.Length);
            return Task.CompletedTask;
        }
        if (root.TryGetProperty("error", out var error))
        {
            pending.Completion.TrySetException(new CodexProtocolException(error.GetRawText()));
        }
        else if (root.TryGetProperty("result", out var result))
        {
            pending.Completion.TrySetResult(result.Clone());
        }
        else
        {
            pending.Completion.TrySetException(new CodexProtocolException("JSON-RPC response had neither result nor error."));
        }
        return Task.CompletedTask;
    }

    private async Task HandleServerRequestAsync(
        JsonElement id,
        string method,
        JsonElement parameters,
        ProcessGeneration processGeneration)
    {
        var processCancellationToken = processGeneration.Token;
        try
        {
            var response = await CreateServerResponseAsync(
                id,
                method,
                parameters,
                DynamicToolCallTimeout,
                processCancellationToken).ConfigureAwait(false);
            await WriteAsync(response, processCancellationToken, processGeneration).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (processCancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Codex server request {Method} stopped during App Server shutdown.", method);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not return the Codex server response for {Method}.", method);
        }
    }

    private async Task<JsonObject> CreateServerResponseAsync(
        JsonElement id,
        string method,
        JsonElement parameters,
        TimeSpan timeout,
        CancellationToken processCancellationToken)
    {
        if (method != "item/tool/call")
        {
            return CreateRpcError(id, -32601, $"Unsupported server request: {method}");
        }

        var handler = DynamicToolHandler;
        if (handler is null)
        {
            return CreateRpcError(id, -32601, "No dynamic tool handler is registered.");
        }

        DynamicToolCall? call = null;
        Task<DynamicToolResult>? handlerTask = null;
        CancellationTokenSource? timeoutSource = null;
        try
        {
            call = DynamicToolCall.FromJson(parameters);
            timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(processCancellationToken);
            timeoutSource.CancelAfter(timeout);
            processCancellationToken.ThrowIfCancellationRequested();
            // Invoke the delegate itself off-thread. Async delegates can perform arbitrary synchronous
            // work before returning their Task; without this boundary neither the timeout nor the
            // stdout pump can make progress while that work is blocked.
            handlerTask = Task.Run(
                () =>
                {
                    timeoutSource.Token.ThrowIfCancellationRequested();
                    return handler(call, timeoutSource.Token);
                },
                CancellationToken.None);
            var result = await handlerTask.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            return CreateRpcResult(id, result.ToProtocolResult());
        }
        catch (OperationCanceledException) when (processCancellationToken.IsCancellationRequested)
        {
            if (handlerTask is not null && call is not null)
            {
                ObserveAbandonedDynamicTool(handlerTask, call, "App Server shutdown");
            }
            throw;
        }
        catch (OperationCanceledException) when (timeoutSource?.IsCancellationRequested == true)
        {
            if (handlerTask is not null && call is not null)
            {
                ObserveAbandonedDynamicTool(handlerTask, call, "deadline expiry");
            }
            _logger.LogWarning(
                "Codex dynamic tool {Tool} exceeded its {TimeoutSeconds}-second deadline.",
                call?.Tool ?? "<unparsed>",
                timeout.TotalSeconds);
            return CreateRpcError(
                id,
                -32001,
                $"Dynamic tool call timed out after {timeout.TotalSeconds:0} seconds.");
        }
        catch (OperationCanceledException exception)
        {
            _logger.LogWarning(exception, "Codex dynamic tool {Tool} was canceled by its handler.", call?.Tool ?? "<unparsed>");
            return CreateRpcError(id, -32000, "Dynamic tool call was canceled before completion.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Codex server request {Method} failed.", method);
            return CreateRpcError(id, -32000, "Dynamic tool call failed unexpectedly.");
        }
        finally
        {
            timeoutSource?.Dispose();
        }
    }

    private void ObserveAbandonedDynamicTool(
        Task<DynamicToolResult> handlerTask,
        DynamicToolCall call,
        string reason)
    {
        _ = handlerTask.ContinueWith(
            task =>
            {
                if (task.IsFaulted)
                {
                    _logger.LogDebug(
                        task.Exception,
                        "Abandoned Codex dynamic tool {Tool} later faulted after {Reason}.",
                        call.Tool,
                        reason);
                }
                else if (task.IsCompletedSuccessfully)
                {
                    _logger.LogDebug(
                        "Abandoned Codex dynamic tool {Tool} later completed after {Reason}; its result was discarded.",
                        call.Tool,
                        reason);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
        try
        {
            var buffer = new char[StandardErrorReadBufferCharacters];
            long recordCharacters = 0;
            var recordHasNonWhitespace = false;
            var loggedRecords = 0;
            var suppressionLogged = false;
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await error.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                for (var index = 0; index < read; index++)
                {
                    var character = buffer[index];
                    if (character is '\r' or '\n')
                    {
                        LogStandardErrorRecord(
                            recordCharacters,
                            recordHasNonWhitespace,
                            ref loggedRecords,
                            ref suppressionLogged);
                        recordCharacters = 0;
                        recordHasNonWhitespace = false;
                        continue;
                    }

                    if (recordCharacters < long.MaxValue)
                    {
                        recordCharacters++;
                    }
                    recordHasNonWhitespace |= !char.IsWhiteSpace(character);
                }
            }
            LogStandardErrorRecord(
                recordCharacters,
                recordHasNonWhitespace,
                ref loggedRecords,
                ref suppressionLogged);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                "Codex App Server error stream stopped unexpectedly ({ExceptionType}).",
                exception.GetType().FullName);
        }
    }

    private void LogStandardErrorRecord(
        long characterCount,
        bool hasNonWhitespace,
        ref int loggedRecords,
        ref bool suppressionLogged)
    {
        if (!hasNonWhitespace)
        {
            return;
        }
        if (loggedRecords < MaximumLoggedStandardErrorRecords)
        {
            loggedRecords++;
            _logger.LogDebug(
                "Codex App Server wrote a non-empty stderr record ({CharacterCount} characters).",
                characterCount);
            return;
        }
        if (!suppressionLogged)
        {
            suppressionLogged = true;
            _logger.LogDebug("Additional Codex App Server stderr records were suppressed.");
        }
    }

    private static JsonObject CreateRpcResult(JsonElement id, object result)
    {
        return new JsonObject
        {
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["result"] = JsonSerializer.SerializeToNode(result)
        };
    }

    private static JsonObject CreateRpcError(JsonElement id, int code, string message)
    {
        return new JsonObject
        {
            ["id"] = JsonNode.Parse(id.GetRawText()),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        };
    }

    private async Task WriteAsync(
        object payload,
        CancellationToken cancellationToken,
        ProcessGeneration processGeneration)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureCurrentGeneration(processGeneration);
        var json = JsonSerializer.Serialize(payload, JsonDefaults.Options);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Stop/restart can happen while this request is waiting for the writer. Validate again
            // under the write lease and always use the writer owned by the captured generation;
            // an A-generation request must never be written to B-generation stdin.
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCurrentGeneration(processGeneration);
            await processGeneration.Input.WriteLineAsync(
                json.AsMemory(),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static ProcessStartInfo CreateMcpListProcessStartInfo(string executable, string projectDirectory)
    {
        var startInfo = CreateBaseProcessStartInfo(
            executable,
            projectDirectory,
            redirectStandardInput: true,
            environment: null);
        AddIsolationFeatureOverrides(startInfo);
        startInfo.ArgumentList.Add("mcp");
        startInfo.ArgumentList.Add("list");
        startInfo.ArgumentList.Add("--json");
        return startInfo;
    }

    private static ProcessStartInfo CreateProcessStartInfo(
        ProcessStartInfo mcpListStartInfo,
        IReadOnlyCollection<string> effectiveMcpNames)
    {
        var startInfo = CreateBaseProcessStartInfo(
            mcpListStartInfo.FileName,
            mcpListStartInfo.WorkingDirectory,
            redirectStandardInput: true,
            mcpListStartInfo.Environment);
        AddIsolationFeatureOverrides(startInfo);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var name in DisabledDirectMcpNames.Concat(effectiveMcpNames))
        {
            if (seen.Add(name))
            {
                names.Add(name);
            }
        }
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(CreateDisabledMcpTableOverride(names));
        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");
        return startInfo;
    }

    private static void AddIsolationFeatureOverrides(ProcessStartInfo startInfo)
    {
        foreach (var value in IsolatedFeatureOverrides)
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(value);
        }
    }

    private static ProcessStartInfo CreateBaseProcessStartInfo(
        string executable,
        string projectDirectory,
        bool redirectStandardInput,
        IEnumerable<KeyValuePair<string, string?>>? environment)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = redirectStandardInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetFullPath(projectDirectory)
        };

        if (environment is not null)
        {
            startInfo.Environment.Clear();
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }
        RemoveGptinoEnvironment(startInfo);
        return startInfo;
    }

    private static async Task<IReadOnlyList<string>> EnumerateEffectiveMcpNamesAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new CodexProtocolException("Codex MCP isolation preflight could not be started.");
            }
            process.StandardInput.Close();
        }
        catch (CodexProtocolException)
        {
            throw;
        }
        catch
        {
            // Do not forward Process.Start exception details. They may contain environment or
            // command-line context from the effective Codex configuration.
            throw new CodexProtocolException("Codex MCP isolation preflight could not be started.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardErrorDrain = process.StandardError.BaseStream.CopyToAsync(
            Stream.Null,
            cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await TerminateMcpListProcessAsync(process, standardOutput, standardErrorDrain)
                .ConfigureAwait(false);
            throw new CodexProtocolException("Codex MCP isolation preflight timed out.");
        }
        catch (OperationCanceledException)
        {
            await TerminateMcpListProcessAsync(process, standardOutput, standardErrorDrain)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }

        try
        {
            await Task.WhenAll(standardOutput, standardErrorDrain)
                .WaitAsync(McpDiscoveryCleanupTimeout)
                .ConfigureAwait(false);
        }
        catch
        {
            await TerminateMcpListProcessAsync(process, standardOutput, standardErrorDrain)
                .ConfigureAwait(false);
            throw new CodexProtocolException("Codex MCP isolation preflight output could not be read.");
        }

        if (process.ExitCode != 0)
        {
            throw new CodexProtocolException("Codex MCP isolation preflight failed.");
        }

        return ParseMcpListNames(await standardOutput.ConfigureAwait(false));
    }

    private static async Task TerminateMcpListProcessAsync(
        Process process,
        Task standardOutput,
        Task standardErrorDrain)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        try
        {
            await Task.WhenAll(
                    ObserveProcessExitAsync(process),
                    ObserveTaskAsync(standardOutput),
                    ObserveTaskAsync(standardErrorDrain))
                .WaitAsync(McpDiscoveryCleanupTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Cleanup is intentionally bounded. Both redirected streams have observers attached,
            // so a late completion or fault cannot become unobserved after this method returns.
        }
    }

    private static async Task ObserveProcessExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task ObserveTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static IReadOnlyList<string> ParseMcpListNames(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new CodexProtocolException("Codex MCP isolation preflight returned an invalid response.");
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("name", out var nameElement) ||
                    nameElement.ValueKind != JsonValueKind.String)
                {
                    throw new CodexProtocolException("Codex MCP isolation preflight returned an invalid response.");
                }

                var name = nameElement.GetString();
                if (string.IsNullOrEmpty(name) || !HasValidUtf16(name))
                {
                    throw new CodexProtocolException("Codex MCP isolation preflight returned an invalid response.");
                }
                names.Add(name);
            }
            return names.Order(StringComparer.Ordinal).ToArray();
        }
        catch (CodexProtocolException)
        {
            throw;
        }
        catch (JsonException)
        {
            // Never include the raw JSON or parser exception: mcp list includes transport config,
            // which can contain bearer tokens and environment secrets.
            throw new CodexProtocolException("Codex MCP isolation preflight returned malformed JSON.");
        }
    }

    private static string CreateDisabledMcpTableOverride(IReadOnlyCollection<string> names)
    {
        var result = new StringBuilder("mcp_servers={");
        var first = true;
        foreach (var name in names)
        {
            if (!first)
            {
                result.Append(',');
            }
            first = false;
            result.Append('"');
            result.Append(EscapeTomlBasicString(name));
            result.Append("\"={enabled=false,command=\"gptino-disabled\"}");
        }
        result.Append('}');
        return result.ToString();
    }

    private static string EscapeTomlBasicString(string value)
    {
        if (!HasValidUtf16(value))
        {
            throw new CodexProtocolException("Codex MCP isolation preflight returned an invalid server name.");
        }

        var escaped = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            switch (character)
            {
                case '\b':
                    escaped.Append("\\b");
                    break;
                case '\t':
                    escaped.Append("\\t");
                    break;
                case '\n':
                    escaped.Append("\\n");
                    break;
                case '\f':
                    escaped.Append("\\f");
                    break;
                case '\r':
                    escaped.Append("\\r");
                    break;
                case '"':
                    escaped.Append("\\\"");
                    break;
                case '\\':
                    escaped.Append("\\\\");
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        escaped.Append("\\u");
                        escaped.Append(((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        escaped.Append(character);
                    }
                    break;
            }
        }
        return escaped.ToString();
    }

    private static bool HasValidUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index]))
            {
                continue;
            }
            if (!char.IsHighSurrogate(value[index]) ||
                index + 1 >= value.Length ||
                !char.IsLowSurrogate(value[index + 1]))
            {
                return false;
            }
            index++;
        }
        return true;
    }

    private static bool TryParseOutputLine(
        string line,
        out JsonElement payload,
        out JsonException? failure)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            payload = document.RootElement.Clone();
            failure = null;
            return true;
        }
        catch (JsonException exception)
        {
            payload = default;
            failure = exception;
            return false;
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
            "Codex CLI was not found. Install it with npm, complete 'codex login', or set CODEX_EXECUTABLE to the native codex.exe (an npm codex.cmd shim is also accepted).");
    }

    private static void RemoveGptinoEnvironment(ProcessStartInfo startInfo)
    {
        foreach (var key in startInfo.Environment.Keys
                     .Where(IsGptinoEnvironmentKey)
                     .ToArray())
        {
            startInfo.Environment.Remove(key);
        }
    }

    private static bool IsGptinoEnvironmentKey(string key) =>
        key.StartsWith("GPTINO_", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("GPTINO:", StringComparison.OrdinalIgnoreCase);

    private static string ResolveConfiguredCodex(string configured)
    {
        var path = Path.GetFullPath(configured);
        if (File.Exists(path) &&
            string.Equals(Path.GetFileName(path), "codex.exe", StringComparison.OrdinalIgnoreCase))
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

    private async Task StopProcessAsync()
    {
        Volatile.Write(ref _initialized, false);
        var processGeneration = Interlocked.Exchange(ref _processGeneration, null);
        processGeneration?.Cancel();
        var process = Interlocked.Exchange(ref _process, null);
        if (processGeneration is not null)
        {
            CompletePendingForGeneration(
                processGeneration.Id,
                pending => pending.TrySetCanceled(processGeneration.Token));
        }
        try
        {
            if (process is not null && !process.HasExited)
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
            process?.Dispose();
            processGeneration?.DisposeInput();
            if (processGeneration is not null)
            {
                await processGeneration.StopNotificationsAsync(NotificationDrainTimeout).ConfigureAwait(false);
            }
        }
    }

    private bool TryGetRunningGeneration(out ProcessGeneration generation)
    {
        generation = null!;
        if (!Volatile.Read(ref _initialized))
        {
            return false;
        }

        var candidate = Volatile.Read(ref _processGeneration);
        var process = Volatile.Read(ref _process);
        if (candidate is null || candidate.Token.IsCancellationRequested || process is null)
        {
            return false;
        }
        try
        {
            if (process.HasExited)
            {
                return false;
            }
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (!ReferenceEquals(candidate, Volatile.Read(ref _processGeneration)) ||
            !Volatile.Read(ref _initialized))
        {
            return false;
        }
        generation = candidate;
        return true;
    }

    private void EnsureCurrentGeneration(ProcessGeneration processGeneration)
    {
        if (!ReferenceEquals(Volatile.Read(ref _processGeneration), processGeneration) ||
            processGeneration.Token.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "The Codex App Server process generation has stopped.",
                innerException: null,
                processGeneration.Token);
        }
    }

    private void CompletePendingForGeneration(
        long generationId,
        Action<TaskCompletionSource<JsonElement>> complete)
    {
        foreach (var pending in _pending.Values)
        {
            if (pending.GenerationId == generationId)
            {
                complete(pending.Completion);
            }
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static Dictionary<string, object?> CreateThreadStartParameters(string cwd, string? model)
    {
        var parameters = new Dictionary<string, object?>
        {
            ["cwd"] = Path.GetFullPath(cwd),
            ["approvalPolicy"] = "never",
            ["sandbox"] = "read-only",
            ["personality"] = "pragmatic",
            ["baseInstructions"] = ThreadInstructions,
            ["dynamicTools"] = DynamicToolSpecs.Create()
        };
        if (!string.IsNullOrWhiteSpace(model))
        {
            parameters["model"] = model;
        }
        return parameters;
    }

    private static Dictionary<string, object> CreateThreadReadParameters(string threadId) =>
        new()
        {
            ["threadId"] = threadId,
            ["includeTurns"] = true
        };

    private static CodexTurnReadResult? ParseThreadReadResult(JsonElement result, string turnId)
    {
        if (!result.TryGetProperty("thread", out var thread) ||
            !thread.TryGetProperty("turns", out var turns) ||
            turns.ValueKind != JsonValueKind.Array)
        {
            throw new CodexProtocolException("thread/read did not return thread.turns.");
        }

        foreach (var turn in turns.EnumerateArray())
        {
            if (!string.Equals(ReadString(turn, "id"), turnId, StringComparison.Ordinal))
            {
                continue;
            }

            var status = ReadString(turn, "status")
                ?? throw new CodexProtocolException("thread/read returned a turn without status.");
            CodexTurnError? error = null;
            if (turn.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object)
            {
                error = new CodexTurnError(
                    ReadString(errorElement, "message") ?? "Unknown Codex turn error.",
                    ReadString(errorElement, "additionalDetails"),
                    errorElement.TryGetProperty("codexErrorInfo", out var errorInfo) &&
                    errorInfo.ValueKind != JsonValueKind.Null
                        ? errorInfo.Clone()
                        : null);
            }

            var messages = new List<CodexAgentMessage>();
            if (!turn.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                throw new CodexProtocolException("thread/read returned a turn without items.");
            }
            foreach (var item in items.EnumerateArray())
            {
                if (!string.Equals(ReadString(item, "type"), "agentMessage", StringComparison.Ordinal))
                {
                    continue;
                }
                messages.Add(new CodexAgentMessage(
                    ReadString(item, "id")
                        ?? throw new CodexProtocolException("thread/read returned an agentMessage without id."),
                    ReadString(item, "text")
                        ?? throw new CodexProtocolException("thread/read returned an agentMessage without text."),
                    ReadString(item, "phase")));
            }

            return new CodexTurnReadResult(turnId, status, error, messages);
        }

        return null;
    }

    private sealed record PendingRequest(
        long GenerationId,
        TaskCompletionSource<JsonElement> Completion);

    private sealed class ProcessGeneration
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly CancellationTokenSource _notificationLifetime = new();
        private readonly Channel<CodexNotification> _notifications;
        private readonly Func<string, JsonElement, Task> _notificationHandler;
        private readonly ILogger<CodexAppServerClient> _logger;
        private readonly Task _notificationLoop;
        private readonly object _notificationStopGate = new();
        private Task? _notificationStop;
        private int _stopped;
        private int _inputDisposed;

        public ProcessGeneration(
            long id,
            StreamWriter input,
            Func<string, JsonElement, Task> notificationHandler,
            ILogger<CodexAppServerClient> logger)
        {
            Id = id;
            Input = input;
            _notificationHandler = notificationHandler;
            _logger = logger;
            _notifications = Channel.CreateUnbounded<CodexNotification>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false
                });
            _notificationLoop = RunNotificationLoopAsync();
        }

        public long Id { get; }

        public StreamWriter Input { get; }

        public CancellationToken Token => _lifetime.Token;

        public void Cancel()
        {
            if (Interlocked.Exchange(ref _stopped, 1) == 0)
            {
                _lifetime.Cancel();
            }
        }

        public bool EnqueueNotification(string method, JsonElement parameters) =>
            _notifications.Writer.TryWrite(new CodexNotification(method, parameters));

        public Task StopNotificationsAsync(TimeSpan drainTimeout)
        {
            lock (_notificationStopGate)
            {
                _notifications.Writer.TryComplete();
                return _notificationStop ??= StopNotificationsCoreAsync(drainTimeout);
            }
        }

        public void DisposeInput()
        {
            if (Interlocked.Exchange(ref _inputDisposed, 1) == 0)
            {
                Input.Dispose();
            }
        }

        private async Task RunNotificationLoopAsync()
        {
            try
            {
                await foreach (var notification in _notifications.Reader.ReadAllAsync(_notificationLifetime.Token)
                                   .ConfigureAwait(false))
                {
                    await _notificationHandler(notification.Method, notification.Parameters).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_notificationLifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Codex notification dispatcher for generation {GenerationId} stopped unexpectedly.",
                    Id);
            }
        }

        private async Task StopNotificationsCoreAsync(TimeSpan drainTimeout)
        {
            if (await Task.WhenAny(_notificationLoop, Task.Delay(drainTimeout)).ConfigureAwait(false) !=
                _notificationLoop)
            {
                _notificationLifetime.Cancel();
                _logger.LogWarning(
                    "Codex notification dispatcher for generation {GenerationId} exceeded its {TimeoutSeconds}-second drain deadline and was canceled.",
                    Id,
                    drainTimeout.TotalSeconds);
                if (await Task.WhenAny(
                        _notificationLoop,
                        Task.Delay(TimeSpan.FromMilliseconds(100))).ConfigureAwait(false) != _notificationLoop)
                {
                    _ = _notificationLoop.ContinueWith(
                        _ => _notificationLifetime.Dispose(),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    return;
                }
            }
            await _notificationLoop.ConfigureAwait(false);
            _notificationLifetime.Dispose();
        }
    }

    private sealed record CodexNotification(string Method, JsonElement Parameters);

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

public sealed record CodexProcessIdentity(int ProcessId, DateTime ProcessStartTimeUtc);

public sealed record CodexTurnReadResult(
    string TurnId,
    string Status,
    CodexTurnError? Error,
    IReadOnlyList<CodexAgentMessage> AgentMessages);

public sealed record CodexTurnError(
    string Message,
    string? AdditionalDetails,
    JsonElement? CodexErrorInfo);

public sealed record CodexAgentMessage(
    string Id,
    string Text,
    string? Phase);

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
