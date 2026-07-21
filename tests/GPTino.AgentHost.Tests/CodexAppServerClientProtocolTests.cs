using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GPTino.AgentHost.Codex;
using GPTino.AgentHost.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace GPTino.AgentHost.Tests;

public sealed class CodexAppServerClientProtocolTests
{
    [Fact]
    public void McpPreflightAndAppServerShareContextAndDisableEveryEffectiveMcp()
    {
        var mcpListStartInfo = InvokeCreateMcpListStartInfo("codex.exe", Directory.GetCurrentDirectory());
        Assert.Equal(
            [
                "-c", "features.plugins=false",
                "-c", "features.apps=false",
                "-c", "features.remote_plugin=false",
                "-c", "features.enable_mcp_apps=false",
                "-c", "features.plugin_sharing=false",
                "mcp", "list", "--json"
            ],
            mcpListStartInfo.ArgumentList);
        Assert.True(mcpListStartInfo.RedirectStandardInput);
        Assert.Equal(Encoding.UTF8.CodePage, mcpListStartInfo.StandardOutputEncoding?.CodePage);
        Assert.Equal(Encoding.UTF8.CodePage, mcpListStartInfo.StandardErrorEncoding?.CodePage);

        var startInfo = InvokeCreateAppServerStartInfo(
            mcpListStartInfo,
            ["alias", "project.plugin", "wire-alias", "quote\"name", "한글-서버", "rhino"]);

        Assert.Equal(mcpListStartInfo.FileName, startInfo.FileName);
        Assert.Equal(mcpListStartInfo.WorkingDirectory, startInfo.WorkingDirectory);
        Assert.Equal(
            mcpListStartInfo.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase),
            startInfo.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase));
        Assert.True(startInfo.RedirectStandardInput);
        Assert.Equal(Encoding.UTF8.CodePage, startInfo.StandardOutputEncoding?.CodePage);
        Assert.Equal(Encoding.UTF8.CodePage, startInfo.StandardErrorEncoding?.CodePage);
        Assert.Equal(
            [
                "-c", "features.plugins=false",
                "-c", "features.apps=false",
                "-c", "features.remote_plugin=false",
                "-c", "features.enable_mcp_apps=false",
                "-c", "features.plugin_sharing=false",
                "-c",
                "mcp_servers={\"cordyceps\"={enabled=false,command=\"gptino-disabled\"},\"wireify\"={enabled=false,command=\"gptino-disabled\"},\"rhino\"={enabled=false,command=\"gptino-disabled\"},\"alias\"={enabled=false,command=\"gptino-disabled\"},\"project.plugin\"={enabled=false,command=\"gptino-disabled\"},\"wire-alias\"={enabled=false,command=\"gptino-disabled\"},\"quote\\\"name\"={enabled=false,command=\"gptino-disabled\"},\"한글-서버\"={enabled=false,command=\"gptino-disabled\"}}",
                "app-server",
                "--stdio"
            ],
            startInfo.ArgumentList);
        Assert.DoesNotContain(startInfo.ArgumentList, value => value == "mcp_servers={}");
        Assert.Equal(1, startInfo.ArgumentList.Count(value => value.Contains("\"rhino\"", StringComparison.Ordinal)));

        mcpListStartInfo.Environment["GPTINO_TEST_SECRET"] = "must-not-propagate";
        mcpListStartInfo.Environment["GPTino:TestSecret"] = "must-not-propagate";
        var sanitizedAppServer = InvokeCreateAppServerStartInfo(mcpListStartInfo, []);
        Assert.DoesNotContain(
            sanitizedAppServer.Environment.Keys,
            key =>
                key.StartsWith("GPTINO_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("GPTINO:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ConfiguredExecutableMustBeNamedCodexExe()
    {
        var resolve = typeof(CodexAppServerClient).GetMethod(
            "ResolveConfiguredCodex",
            BindingFlags.NonPublic | BindingFlags.Static);
        var processPath = Environment.ProcessPath;

        Assert.NotNull(resolve);
        Assert.False(string.IsNullOrWhiteSpace(processPath));
        Assert.False(string.Equals(
            "codex.exe",
            Path.GetFileName(processPath),
            StringComparison.OrdinalIgnoreCase));
        var exception = Assert.Throws<TargetInvocationException>(
            () => resolve.Invoke(null, [processPath]));
        Assert.IsType<FileNotFoundException>(exception.InnerException);
    }

    [Fact]
    public void McpListParserSupportsAliasesPunctuationQuotesAndUtf8()
    {
        var names = InvokeParseMcpListNames("""
            [
              { "name": "wire-alias", "transport": { "type": "stdio" } },
              { "name": "project.plugin", "enabled": true },
              { "name": "quote\"name" },
              { "name": "한글-서버" },
              { "name": "wire-alias" }
            ]
            """);

        Assert.Equal(["project.plugin", "quote\"name", "wire-alias", "한글-서버"], names);
    }

    [Fact]
    public void McpListParserAcceptsAnEmptyEffectiveList()
    {
        Assert.Empty(InvokeParseMcpListNames("[]"));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("[{}]")]
    [InlineData("[{\"name\":null}]")]
    [InlineData("[{\"name\":\"\"}]")]
    public void McpListParserRejectsMalformedOrUnexpectedJsonWithoutEchoingIt(string payload)
    {
        var exception = Assert.Throws<CodexProtocolException>(() => InvokeParseMcpListNames(payload));

        Assert.DoesNotContain(payload, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledMcpOverrideEscapesTomlBasicStringCharacters()
    {
        Assert.Equal(
            "mcp_servers={\"quote\\\"slash\\\\tab\\tline\\n\"={enabled=false,command=\"gptino-disabled\"}}",
            InvokeCreateDisabledMcpTableOverride(["quote\"slash\\tab\tline\n"]));
    }

    [Fact]
    public async Task McpPreflightRejectsMalformedOutputWithoutEchoingConfig()
    {
        var startInfo = CreateShellProcessStartInfo(
            OperatingSystem.IsWindows() ? "echo {secret-broken" : "printf '{secret-broken'");

        var exception = await Assert.ThrowsAsync<CodexProtocolException>(
            () => InvokeEnumerateEffectiveMcpNamesAsync(startInfo, TimeSpan.FromSeconds(3)));

        Assert.Equal("Codex MCP isolation preflight returned malformed JSON.", exception.Message);
        Assert.DoesNotContain("secret-broken", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpPreflightFailsClosedOnNonzeroExitWithoutEchoingStderr()
    {
        var command = OperatingSystem.IsWindows()
            ? "echo super-secret 1>&2 & exit /b 7"
            : "printf 'super-secret' >&2; exit 7";
        var startInfo = CreateShellProcessStartInfo(command);

        var exception = await Assert.ThrowsAsync<CodexProtocolException>(
            () => InvokeEnumerateEffectiveMcpNamesAsync(startInfo, TimeSpan.FromSeconds(3)));

        Assert.Equal("Codex MCP isolation preflight failed.", exception.Message);
        Assert.DoesNotContain("super-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpPreflightTimeoutKillsTheChildAndReturnsWithinItsCleanupBound()
    {
        var command = OperatingSystem.IsWindows()
            ? "ping 127.0.0.1 -n 30 > nul"
            : "sleep 30";
        var startInfo = CreateShellProcessStartInfo(command);
        var stopwatch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<CodexProtocolException>(
            () => InvokeEnumerateEffectiveMcpNamesAsync(startInfo, TimeSpan.FromMilliseconds(100)));

        stopwatch.Stop();
        Assert.Equal("Codex MCP isolation preflight timed out.", exception.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Cleanup took {stopwatch.Elapsed}.");
    }

    [Fact]
    public async Task McpPreflightClosesStandardInputImmediately()
    {
        var command = OperatingSystem.IsWindows()
            ? "more > nul & echo []"
            : "cat > /dev/null; printf '[]'";
        var startInfo = CreateShellProcessStartInfo(command);

        var names = await InvokeEnumerateEffectiveMcpNamesAsync(
            startInfo,
            TimeSpan.FromSeconds(3));

        Assert.Empty(names);
    }

    [Fact]
    public async Task McpPreflightReadsUtf8Json()
    {
        using var artifacts = new TestDirectory();
        var path = artifacts.GetPath($"gptino-mcp-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "[{\"name\":\"한글-서버\"}]", new UTF8Encoding(false));
        var startInfo = CreateFileOutputProcessStartInfo(path);

        var names = await InvokeEnumerateEffectiveMcpNamesAsync(startInfo, TimeSpan.FromSeconds(3));

        Assert.Equal(["한글-서버"], names);
    }

    [Fact(Skip = "Executed by the explicit DevLoop live-codex stage; it must not be reported as a unit-test pass when credentials are absent.")]
    public async Task LiveCodexMcpIsolationSmoke_WhenExplicitlyEnabled()
    {
        var executable = Environment.GetEnvironmentVariable("GPTINO_LIVE_CODEX_EXECUTABLE");
        if (string.IsNullOrWhiteSpace(executable))
        {
            return;
        }

        await using var client = new CodexAppServerClient(
            new AgentHostOptions
            {
                ProjectDirectory = Directory.GetCurrentDirectory(),
                CodexExecutable = executable
            },
            NullLogger<CodexAppServerClient>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var models = await client.ListModelsAsync(timeout.Token);

        Assert.NotEmpty(models);
    }

    [Fact]
    public async Task StartupFailureOpensCircuitBeforeAnotherPreflightCanStart()
    {
        await using var client = new CodexAppServerClient(
            new AgentHostOptions
            {
                ProjectDirectory = Directory.GetCurrentDirectory(),
                CodexExecutable = Path.Combine(
                    Directory.GetCurrentDirectory(),
                    $"missing-codex-{Guid.NewGuid():N}.exe")
            },
            NullLogger<CodexAppServerClient>.Instance);

        await Assert.ThrowsAsync<FileNotFoundException>(() => client.ListModelsAsync());
        var stopwatch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<CodexProtocolException>(
            () => client.ListModelsAsync());

        stopwatch.Stop();
        Assert.Equal(
            "Codex startup is temporarily paused after a recent failure.",
            exception.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void DynamicToolDeadlineIsFiniteAndShorterThanTheTurnLifetime()
    {
        var timeout = typeof(CodexAppServerClient).GetField(
            "DynamicToolCallTimeout",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(timeout);
        Assert.Equal(TimeSpan.FromSeconds(30), timeout.GetValue(null));
    }

    [Fact]
    public void ThreadStartUsesStableLegacyHistoryDefault()
    {
        var createParameters = typeof(CodexAppServerClient).GetMethod(
            "CreateThreadStartParameters",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(createParameters);
        var parameters = Assert.IsAssignableFrom<IDictionary<string, object?>>(
            createParameters.Invoke(null, [Directory.GetCurrentDirectory(), null]));

        Assert.DoesNotContain("historyMode", parameters.Keys);
        Assert.Equal("never", parameters["approvalPolicy"]);
        Assert.Equal("read-only", parameters["sandbox"]);
        Assert.Equal("pragmatic", parameters["personality"]);
        Assert.True(parameters.ContainsKey("dynamicTools"));
        Assert.False(parameters.ContainsKey("model"));
    }

    [Fact]
    public void ThreadStartIncludesExplicitModelWithoutExperimentalHistoryMode()
    {
        var createParameters = typeof(CodexAppServerClient).GetMethod(
            "CreateThreadStartParameters",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(createParameters);
        var parameters = Assert.IsAssignableFrom<IDictionary<string, object?>>(
            createParameters.Invoke(null, [Directory.GetCurrentDirectory(), "gpt-test"]));

        Assert.Equal("gpt-test", parameters["model"]);
        Assert.DoesNotContain("historyMode", parameters.Keys);
    }

    [Fact]
    public void ThreadReadAlwaysIncludesTurns()
    {
        var createParameters = typeof(CodexAppServerClient).GetMethod(
            "CreateThreadReadParameters",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(createParameters);
        var parameters = Assert.IsAssignableFrom<IDictionary<string, object>>(
            createParameters.Invoke(null, ["thread-1"]));

        Assert.Equal("thread-1", parameters["threadId"]);
        Assert.Equal(true, parameters["includeTurns"]);
    }

    [Fact]
    public void ThreadReadParsesTurnErrorAndAgentMessagesFromCodex0144Shape()
    {
        using var document = JsonDocument.Parse("""
            {
              "thread": {
                "turns": [
                  {
                    "id": "turn-other",
                    "status": "completed",
                    "error": null,
                    "items": []
                  },
                  {
                    "id": "turn-target",
                    "status": "failed",
                    "error": {
                      "message": "provider failed",
                      "additionalDetails": "request-id-7",
                      "codexErrorInfo": "serverOverloaded"
                    },
                    "items": [
                      { "id": "user-1", "type": "userMessage", "content": [] },
                      { "id": "agent-1", "type": "agentMessage", "text": "working", "phase": "commentary" },
                      { "id": "tool-1", "type": "dynamicToolCall", "tool": "snapshot_read", "status": "completed" },
                      { "id": "agent-2", "type": "agentMessage", "text": "done", "phase": "final_answer" },
                      { "id": "agent-3", "type": "agentMessage", "text": "legacy phase", "phase": null }
                    ]
                  }
                ]
              }
            }
            """);

        var result = InvokeThreadReadParser(document.RootElement, "turn-target");

        Assert.NotNull(result);
        Assert.Equal("turn-target", result.TurnId);
        Assert.Equal("failed", result.Status);
        Assert.NotNull(result.Error);
        Assert.Equal("provider failed", result.Error.Message);
        Assert.Equal("request-id-7", result.Error.AdditionalDetails);
        Assert.Equal("serverOverloaded", result.Error.CodexErrorInfo?.GetString());
        Assert.Collection(
            result.AgentMessages,
            message => Assert.Equal(new CodexAgentMessage("agent-1", "working", "commentary"), message),
            message => Assert.Equal(new CodexAgentMessage("agent-2", "done", "final_answer"), message),
            message => Assert.Equal(new CodexAgentMessage("agent-3", "legacy phase", null), message));
    }

    [Fact]
    public void ThreadReadReturnsNullWhenRequestedTurnIsAbsent()
    {
        using var document = JsonDocument.Parse("""
            { "thread": { "turns": [{ "id": "other", "status": "completed", "error": null, "items": [] }] } }
            """);

        Assert.Null(InvokeThreadReadParser(document.RootElement, "missing"));
    }

    [Fact]
    public void NdjsonParserIdentifiesMalformedAndValidLines()
    {
        var parse = typeof(CodexAppServerClient).GetMethod(
            "TryParseOutputLine",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(parse);

        object?[] malformed = ["{broken", default(JsonElement), null];
        Assert.Equal(false, parse.Invoke(null, malformed));
        Assert.IsAssignableFrom<JsonException>(malformed[2]);

        object?[] valid = ["{\"id\":17,\"result\":{\"ok\":true}}", default(JsonElement), null];
        Assert.Equal(true, parse.Invoke(null, valid));
        Assert.Null(valid[2]);
        var payload = Assert.IsType<JsonElement>(valid[1]);
        Assert.Equal(17, payload.GetProperty("id").GetInt32());
        Assert.True(payload.GetProperty("result").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task MalformedNdjsonFailsPendingCallAndStopsItsProcessGeneration()
    {
        var client = CreateClient();
        var generation = CreateProcessGeneration(client, 13);
        using var process = StartExitedProcess();
        SetField(client, "_process", process);
        SetField(client, "_processGeneration", generation.Value);
        SetField(client, "_initialized", true);
        var call = InvokeCallCoreAsync(client, "probe/read", new { }, generation.Value);
        await WaitForBytesAsync(generation.Output);
        using var output = new MemoryStream(Encoding.UTF8.GetBytes(
            "{broken\n{\"id\":1,\"result\":{\"mustNotBeRead\":true}}\n"));
        using var reader = new StreamReader(output, Encoding.UTF8);

        await InvokeReadLoopAsync(client, process, reader, generation.Value)
            .WaitAsync(TimeSpan.FromSeconds(1));
        var exception = await Assert.ThrowsAsync<CodexProtocolException>(
            () => call.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.Contains("malformed NDJSON", exception.Message);
        Assert.True(GetGenerationToken(generation.Value).IsCancellationRequested);
        Assert.False(client.IsRunning);
        AssertPendingCount(client, 0);
        await client.DisposeAsync();
        generation.Dispose();
    }

    [Fact]
    public async Task ExitedProcessStillDrainsBufferedStdoutBeforeFailingPendingCalls()
    {
        var client = CreateClient();
        var generation = CreateProcessGeneration(client, 17);
        using var process = StartExitedProcess();
        SetField(client, "_process", process);
        SetField(client, "_processGeneration", generation.Value);
        SetField(client, "_initialized", true);

        var call = InvokeCallCoreAsync(client, "probe/read", new { }, generation.Value);
        using var output = new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"id\":1,\"result\":{\"ok\":true}}\n"));
        using var reader = new StreamReader(output, Encoding.UTF8);

        await InvokeReadLoopAsync(client, process, reader, generation.Value)
            .WaitAsync(TimeSpan.FromSeconds(1));
        var result = await call.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(result.GetProperty("ok").GetBoolean());
        await client.DisposeAsync();
        generation.Dispose();
    }

    [Fact]
    public async Task PendingCallStopsWhenItsProcessGenerationIsCanceled()
    {
        var client = CreateClient();
        var generation = CreateProcessGeneration(client, 21);
        SetField(client, "_processGeneration", generation.Value);

        var call = InvokeCallCoreAsync(client, "probe/read", new { }, generation.Value);
        await WaitForBytesAsync(generation.Output);
        CancelGeneration(generation.Value);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => call.WaitAsync(TimeSpan.FromSeconds(1)));
        AssertPendingCount(client, 0);
        await client.DisposeAsync();
        generation.Dispose();
    }

    [Fact]
    public async Task PendingCallCannotWriteIntoReplacementProcessGeneration()
    {
        var client = CreateClient();
        var generationA = CreateProcessGeneration(client, 31);
        var generationB = CreateProcessGeneration(client, 32);
        var writeGate = GetField<SemaphoreSlim>(client, "_writeGate");
        await writeGate.WaitAsync();
        try
        {
            SetField(client, "_processGeneration", generationA.Value);
            var call = InvokeCallCoreAsync(client, "probe/read", new { }, generationA.Value);

            SetField(client, "_processGeneration", generationB.Value);
            CancelGeneration(generationA.Value);
            writeGate.Release();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => call.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(0, generationA.Output.Length);
            Assert.Equal(0, generationB.Output.Length);
            AssertPendingCount(client, 0);
        }
        finally
        {
            if (writeGate.CurrentCount == 0)
            {
                writeGate.Release();
            }
            await client.DisposeAsync();
            generationA.Dispose();
            generationB.Dispose();
        }
    }

    [Fact]
    public async Task ResponseCannotCompletePendingCallFromAnotherProcessGeneration()
    {
        var client = CreateClient();
        var generationA = CreateProcessGeneration(client, 41);
        var generationB = CreateProcessGeneration(client, 42);
        SetField(client, "_processGeneration", generationB.Value);
        var call = InvokeCallCoreAsync(client, "probe/read", new { }, generationB.Value);
        await WaitForBytesAsync(generationB.Output);
        using var response = JsonDocument.Parse("{\"id\":1,\"result\":{\"ok\":true}}");

        await InvokeProcessOutputAsync(client, response.RootElement, generationA.Value);
        Assert.False(call.IsCompleted);

        await InvokeProcessOutputAsync(client, response.RootElement, generationB.Value);
        var result = await call.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(result.GetProperty("ok").GetBoolean());

        await client.DisposeAsync();
        generationA.Dispose();
        generationB.Dispose();
    }

    [Fact]
    public async Task OrderedNotificationHandlerCannotBlockRpcResponsePump()
    {
        var client = CreateClient();
        var generation = CreateProcessGeneration(client, 45);
        using var process = StartExitedProcess();
        SetField(client, "_process", process);
        SetField(client, "_processGeneration", generation.Value);
        SetField(client, "_initialized", true);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handled = new List<int>();
        client.NotificationReceived += async (_, parameters) =>
        {
            var sequence = parameters.GetProperty("sequence").GetInt32();
            if (sequence == 1)
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task;
            }
            handled.Add(sequence);
        };
        var call = InvokeCallCoreAsync(client, "probe/read", new { }, generation.Value);
        await WaitForBytesAsync(generation.Output);
        using var output = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"method":"test/notification","params":{"sequence":1}}
            {"method":"test/notification","params":{"sequence":2}}
            {"id":1,"result":{"ok":true}}
            """ + "\n"));
        using var reader = new StreamReader(output, Encoding.UTF8);

        var readLoop = InvokeReadLoopAsync(client, process, reader, generation.Value);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var result = await call.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Empty(handled);
        releaseFirst.TrySetResult();
        await readLoop.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal([1, 2], handled);
        await client.DisposeAsync();
        generation.Dispose();
    }

    [Fact]
    public async Task StopUsesBoundedNotificationDrainWhenHandlerDoesNotReturn()
    {
        var client = CreateClient();
        var generation = CreateProcessGeneration(client, 46);
        SetField(client, "_processGeneration", generation.Value);
        SetField(client, "_initialized", true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.NotificationReceived += async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        using var notification = JsonDocument.Parse(
            "{\"method\":\"test/notification\",\"params\":{}}");
        await InvokeProcessOutputAsync(client, notification.RootElement, generation.Value);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var stopwatch = Stopwatch.StartNew();
        await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.True(GetGenerationToken(generation.Value).IsCancellationRequested);
        release.TrySetResult();
        await client.DisposeAsync();
        generation.Dispose();
    }

    [Fact]
    public async Task DynamicToolSuccessAndDeclaredFailureProduceJsonRpcResults()
    {
        var client = CreateClient();
        client.DynamicToolHandler = (_, _) => Task.FromResult(DynamicToolResult.Ok(new { value = 7 }));

        var success = await InvokeServerResponseAsync(client, TimeSpan.FromSeconds(1));

        Assert.Equal(41, success["id"]?.GetValue<int>());
        Assert.True(success["result"]?["success"]?.GetValue<bool>());
        client.DynamicToolHandler = (_, _) => Task.FromResult(DynamicToolResult.Fail("not available"));

        var failure = await InvokeServerResponseAsync(client, TimeSpan.FromSeconds(1));

        Assert.False(failure["result"]?["success"]?.GetValue<bool>());
        Assert.Equal("not available", failure["result"]?["contentItems"]?[0]?["text"]?.GetValue<string>());
    }

    [Fact]
    public async Task DynamicToolExceptionProducesJsonRpcError()
    {
        var client = CreateClient();
        client.DynamicToolHandler = (_, _) => throw new InvalidOperationException("secret path C:\\private\\tool exploded");

        var response = await InvokeServerResponseAsync(client, TimeSpan.FromSeconds(1));

        Assert.Equal(-32000, response["error"]?["code"]?.GetValue<int>());
        Assert.Equal("Dynamic tool call failed unexpectedly.", response["error"]?["message"]?.GetValue<string>());
        Assert.DoesNotContain("private", response.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DynamicToolTimeoutCancelsHandlerAndProducesJsonRpcError()
    {
        var client = CreateClient();
        var handlerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DynamicToolHandler = async (_, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return DynamicToolResult.Ok(new { unreachable = true });
            }
            catch (OperationCanceledException)
            {
                handlerCancelled.TrySetResult();
                throw;
            }
        };

        var response = await InvokeServerResponseAsync(client, TimeSpan.FromMilliseconds(50));

        Assert.Equal(-32001, response["error"]?["code"]?.GetValue<int>());
        Assert.Contains("timed out", response["error"]?["message"]?.GetValue<string>());
        await handlerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SynchronouslyBlockingDynamicToolCannotDefeatDeadline()
    {
        var client = CreateClient();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DynamicToolHandler = (_, _) =>
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(5));
            return Task.FromResult(DynamicToolResult.Ok(new { late = true }));
        };

        var responseTask = Task.Run(
            () => InvokeServerResponseAsync(client, TimeSpan.FromMilliseconds(50)));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(-32001, response["error"]?["code"]?.GetValue<int>());
        }
        finally
        {
            release.Set();
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task SynchronouslyBlockingServerRequestDoesNotBlockStdoutDispatcher()
    {
        var client = CreateClient();
        var generation = CreateProcessGeneration(client, 51);
        SetField(client, "_processGeneration", generation.Value);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.DynamicToolHandler = (_, _) =>
        {
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(5));
            return Task.FromResult(DynamicToolResult.Ok(new { late = true }));
        };
        using var request = JsonDocument.Parse("""
            {
              "id": "server-call-1",
              "method": "item/tool/call",
              "params": {
                "callId": "call-1",
                "threadId": "thread-1",
                "turnId": "turn-1",
                "namespace": "gptino_v1",
                "tool": "snapshot_read",
                "arguments": {}
              }
            }
            """);

        var dispatch = Task.Run(
            () => InvokeProcessOutputAsync(client, request.RootElement, generation.Value));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await dispatch.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            CancelGeneration(generation.Value);
            release.Set();
            await client.DisposeAsync();
            generation.Dispose();
        }
    }

    [Fact]
    public async Task DynamicToolStopsWithoutResponseConstructionDuringProcessShutdown()
    {
        var client = CreateClient();
        client.DynamicToolHandler = async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return DynamicToolResult.Ok(new { unreachable = true });
        };
        using var processLifetime = new CancellationTokenSource();

        var responseTask = InvokeServerResponseAsync(
            client,
            TimeSpan.FromMinutes(1),
            processLifetime.Token);
        processLifetime.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => responseTask);
    }

    private static ProcessStartInfo InvokeCreateMcpListStartInfo(string executable, string projectDirectory)
    {
        var create = typeof(CodexAppServerClient).GetMethod(
            "CreateMcpListProcessStartInfo",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(create);
        return Assert.IsType<ProcessStartInfo>(create.Invoke(null, [executable, projectDirectory]));
    }

    private static ProcessStartInfo InvokeCreateAppServerStartInfo(
        ProcessStartInfo mcpListStartInfo,
        IReadOnlyCollection<string> effectiveMcpNames)
    {
        var create = typeof(CodexAppServerClient).GetMethod(
            "CreateProcessStartInfo",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(create);
        return Assert.IsType<ProcessStartInfo>(create.Invoke(null, [mcpListStartInfo, effectiveMcpNames]));
    }

    private static IReadOnlyList<string> InvokeParseMcpListNames(string json)
    {
        var parse = typeof(CodexAppServerClient).GetMethod(
            "ParseMcpListNames",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(parse);
        try
        {
            return Assert.IsAssignableFrom<IReadOnlyList<string>>(parse.Invoke(null, [json]));
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static string InvokeCreateDisabledMcpTableOverride(IReadOnlyCollection<string> names)
    {
        var create = typeof(CodexAppServerClient).GetMethod(
            "CreateDisabledMcpTableOverride",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(create);
        return Assert.IsType<string>(create.Invoke(null, [names]));
    }

    private static Task<IReadOnlyList<string>> InvokeEnumerateEffectiveMcpNamesAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var enumerate = typeof(CodexAppServerClient).GetMethod(
            "EnumerateEffectiveMcpNamesAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(enumerate);
        return Assert.IsAssignableFrom<Task<IReadOnlyList<string>>>(enumerate.Invoke(
            null,
            [startInfo, timeout, cancellationToken]));
    }

    private static ProcessStartInfo CreateShellProcessStartInfo(string command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows()
                ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
                : "/bin/sh",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
        }
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }

    private static ProcessStartInfo CreateFileOutputProcessStartInfo(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var windowsStartInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            windowsStartInfo.ArgumentList.Add("/d");
            windowsStartInfo.ArgumentList.Add("/c");
            windowsStartInfo.ArgumentList.Add("type");
            windowsStartInfo.ArgumentList.Add(path);
            return windowsStartInfo;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/cat",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(path);
        return startInfo;
    }

    private static CodexAppServerClient CreateClient() =>
        new(
            new AgentHostOptions { ProjectDirectory = Directory.GetCurrentDirectory() },
            NullLogger<CodexAppServerClient>.Instance);

    private static ProcessGenerationFixture CreateProcessGeneration(
        CodexAppServerClient client,
        long id)
    {
        var output = new MemoryStream();
        var input = new StreamWriter(output, new UTF8Encoding(false), 1024, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        var generationType = typeof(CodexAppServerClient).GetNestedType(
            "ProcessGeneration",
            BindingFlags.NonPublic);
        Assert.NotNull(generationType);
        var generation = Activator.CreateInstance(
            generationType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                id,
                input,
                new Func<string, JsonElement, Task>(
                    (method, parameters) => InvokeNotificationHandlerAsync(client, method, parameters)),
                NullLogger<CodexAppServerClient>.Instance
            ],
            culture: null);
        Assert.NotNull(generation);
        return new ProcessGenerationFixture(generation, output, input);
    }

    private static void CancelGeneration(object generation)
    {
        var cancel = generation.GetType().GetMethod(
            "Cancel",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(cancel);
        cancel.Invoke(generation, null);
    }

    private static CancellationToken GetGenerationToken(object generation)
    {
        var token = generation.GetType().GetProperty(
            "Token",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(token);
        return Assert.IsType<CancellationToken>(token.GetValue(generation));
    }

    private static async Task WaitForBytesAsync(MemoryStream stream)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        while (stream.Length == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.True(stream.Length > 0, "The test RPC was not written before its deadline.");
    }

    private static Process StartExitedProcess()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows()
                ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
                : "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("exit 0");
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("exit 0");
        }
        var process = Process.Start(startInfo);
        Assert.NotNull(process);
        Assert.True(process.WaitForExit(3000));
        Assert.True(process.HasExited);
        return process;
    }

    private static Task<JsonElement> InvokeCallCoreAsync(
        CodexAppServerClient client,
        string method,
        object parameters,
        object generation)
    {
        var call = typeof(CodexAppServerClient).GetMethod(
            "CallCoreAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(call);
        return Assert.IsAssignableFrom<Task<JsonElement>>(call.Invoke(
            client,
            [method, parameters, CancellationToken.None, generation]));
    }

    private static Task InvokeReadLoopAsync(
        CodexAppServerClient client,
        Process process,
        StreamReader output,
        object generation)
    {
        var read = typeof(CodexAppServerClient).GetMethod(
            "ReadLoopAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(read);
        return Assert.IsAssignableFrom<Task>(read.Invoke(client, [process, output, generation]));
    }

    private static Task InvokeProcessOutputAsync(
        CodexAppServerClient client,
        JsonElement payload,
        object generation)
    {
        var process = typeof(CodexAppServerClient).GetMethod(
            "ProcessOutputPayloadAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(process);
        return Assert.IsAssignableFrom<Task>(process.Invoke(
            client,
            [payload.Clone(), payload.GetRawText(), generation]));
    }

    private static Task InvokeNotificationHandlerAsync(
        CodexAppServerClient client,
        string method,
        JsonElement parameters)
    {
        var raise = typeof(CodexAppServerClient).GetMethod(
            "RaiseNotificationAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(raise);
        return Assert.IsAssignableFrom<Task>(raise.Invoke(client, [method, parameters.Clone()]));
    }

    private static void SetField(CodexAppServerClient client, string name, object? value)
    {
        var field = typeof(CodexAppServerClient).GetField(
            name,
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field.SetValue(client, value);
    }

    private static T GetField<T>(CodexAppServerClient client, string name)
    {
        var field = typeof(CodexAppServerClient).GetField(
            name,
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<T>(field.GetValue(client));
    }

    private static void AssertPendingCount(CodexAppServerClient client, int expected)
    {
        var pending = GetField<object>(client, "_pending");
        var count = pending.GetType().GetProperty("Count");
        Assert.NotNull(count);
        Assert.Equal(expected, Assert.IsType<int>(count.GetValue(pending)));
    }

    private static CodexTurnReadResult? InvokeThreadReadParser(JsonElement payload, string turnId)
    {
        var parse = typeof(CodexAppServerClient).GetMethod(
            "ParseThreadReadResult",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(parse);
        return (CodexTurnReadResult?)parse.Invoke(null, [payload, turnId]);
    }

    private static async Task<JsonObject> InvokeServerResponseAsync(
        CodexAppServerClient client,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var createResponse = typeof(CodexAppServerClient).GetMethod(
            "CreateServerResponseAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(createResponse);
        using var id = JsonDocument.Parse("41");
        using var parameters = JsonDocument.Parse("""
            {
              "callId": "call-1",
              "threadId": "thread-1",
              "turnId": "turn-1",
              "namespace": "gptino_v1",
              "tool": "snapshot_read",
              "arguments": {}
            }
            """);
        var task = Assert.IsAssignableFrom<Task<JsonObject>>(createResponse.Invoke(
            client,
            [id.RootElement, "item/tool/call", parameters.RootElement, timeout, cancellationToken]));
        return await task;
    }

    private sealed record ProcessGenerationFixture(
        object Value,
        MemoryStream Output,
        StreamWriter Input) : IDisposable
    {
        public void Dispose()
        {
            Input.Dispose();
            Output.Dispose();
        }
    }
}
