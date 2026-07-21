using System.Diagnostics;
using System.Net;
using System.Text.Json;
using GPTino.BridgeContract;

namespace GPTino.Rhino;

internal sealed class AgentHostBootstrapper : IDisposable
{
    private const string ReadyPrefix = "GPTINO_READY ";

    private readonly object _gate = new();
    private readonly Process _process;
    private readonly PipeEndpoint _endpoint;
    private readonly BridgeSecret _secret;
    private readonly CancellationTokenSource _lifetime = new();
    private string _status = "Waiting for AgentHost.";
    private Uri? _uiBaseUri;
    private string? _panelParentCredential;
    private string? _panelBootstrapNonce;
    private uint? _panelBootstrapDocumentSerial;
    private Task? _panelNonceRequest;
    private DateTimeOffset _nextPanelNonceAttempt;
    private int _activeReadyCallbacks;
    private bool _disposed;

    public event EventHandler? Ready;

    private AgentHostBootstrapper(
        Process process,
        PipeEndpoint endpoint,
        BridgeSecret secret)
    {
        _process = process;
        _endpoint = endpoint;
        _secret = secret;
    }

    public Uri? UiBaseUri
    {
        get
        {
            lock (_gate)
            {
                return _disposed ? null : _uiBaseUri;
            }
        }
    }

    public string Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public bool HasExited
    {
        get
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return true;
                }
                try
                {
                    return _process.HasExited;
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or ObjectDisposedException)
                {
                    return true;
                }
            }
        }
    }

    public bool TryTakePanelBootstrapNonce(uint documentSerial, out string nonce)
    {
        if (documentSerial == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(documentSerial));
        }

        lock (_gate)
        {
            if (_disposed)
            {
                nonce = string.Empty;
                return false;
            }

            if (_panelBootstrapNonce is not null &&
                _panelBootstrapDocumentSerial == documentSerial)
            {
                nonce = _panelBootstrapNonce;
                _panelBootstrapNonce = null;
                _panelBootstrapDocumentSerial = null;
                return true;
            }

            if (_uiBaseUri is not null &&
                _panelParentCredential is not null &&
                _panelNonceRequest is null &&
                DateTimeOffset.UtcNow >= _nextPanelNonceAttempt)
            {
                var baseUri = _uiBaseUri;
                var parentCredential = _panelParentCredential;
                _panelNonceRequest = Task.Run(
                    () => RequestPanelBootstrapNonceAsync(
                        baseUri,
                        parentCredential,
                        documentSerial,
                        _lifetime.Token),
                    _lifetime.Token);
            }

            nonce = string.Empty;
            return false;
        }
    }

    public static AgentHostBootstrapper Start(string plugInAssemblyPath, DocumentTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Validate();
        var assemblyDirectory = Path.GetDirectoryName(Path.GetFullPath(plugInAssemblyPath))
            ?? throw new InvalidOperationException("Cannot resolve the GPTino plug-in directory.");
        var executablePath = ResolveAgentHostPath(assemblyDirectory);
        DevelopmentDiagnosticTrace.TryWrite(
            "Rhino",
            "agent-executable-resolved",
            $"file={Path.GetFileName(executablePath)};exists={File.Exists(executablePath)}");

        using var currentProcess = Process.GetCurrentProcess();
        var processIdentity = $"rhino-{currentProcess.Id}-{currentProcess.StartTime.ToUniversalTime().Ticks}";
        var endpoint = PipeEndpoint.ForProject(processIdentity, currentProcess.Id);
        var secret = BridgeSecret.Generate();

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = assemblyDirectory,
        };
        startInfo.ArgumentList.Add("--bridge-pipe");
        startInfo.ArgumentList.Add(endpoint.Name);
        startInfo.ArgumentList.Add("--parent-process-id");
        startInfo.ArgumentList.Add(currentProcess.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--project-id");
        startInfo.ArgumentList.Add(target.ProjectId.ToString("D"));
        startInfo.ArgumentList.Add("--rhino");
        startInfo.ArgumentList.Add(target.RhinoPath);
        startInfo.ArgumentList.Add("--rhino-document-serial");
        startInfo.ArgumentList.Add(
            target.RhinoDocumentSerial.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--grasshopper");
        startInfo.ArgumentList.Add(target.GrasshopperPath);
        startInfo.ArgumentList.Add("--project-directory");
        startInfo.ArgumentList.Add(
            Path.GetDirectoryName(target.GrasshopperPath)
            ?? throw new InvalidOperationException("Cannot resolve the Grasshopper project directory."));
        var developmentDataDirectory = DevelopmentDataDirectoryPolicy.ResolveFromEnvironment();
        if (developmentDataDirectory is not null)
        {
            startInfo.ArgumentList.Add("--data-directory");
            startInfo.ArgumentList.Add(developmentDataDirectory);
        }
        RemoveGptinoEnvironment(startInfo);
        if (developmentDataDirectory is not null)
        {
            var developmentApiToken = Environment.GetEnvironmentVariable("GPTINO_API_TOKEN");
            if (developmentApiToken is null ||
                developmentApiToken.Length != 64 ||
                developmentApiToken.Any(character => !Uri.IsHexDigit(character)))
            {
                throw new InvalidOperationException(
                    "A 256-bit hexadecimal API token is required for a development launch.");
            }
            startInfo.Environment[DevelopmentDataDirectoryPolicy.ModeEnvironmentVariable] = "1";
            startInfo.Environment[DevelopmentDataDirectoryPolicy.DataDirectoryEnvironmentVariable] =
                developmentDataDirectory;
            startInfo.Environment["GPTINO_API_TOKEN"] = developmentApiToken;
        }
        startInfo.Environment["GPTINO_BRIDGE_PIPE"] = endpoint.Name;
        startInfo.Environment["GPTINO_BRIDGE_SECRET"] = secret.ExportBase64();

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var bootstrapper = new AgentHostBootstrapper(process, endpoint, secret);
        process.OutputDataReceived += bootstrapper.OnOutput;
        process.ErrorDataReceived += bootstrapper.OnError;
        process.Exited += bootstrapper.OnExited;

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("AgentHost did not start.");
            }
            DevelopmentDiagnosticTrace.TryWrite(
                "Rhino",
                "agent-process-started",
                $"pid={process.Id}");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return bootstrapper;
        }
        catch
        {
            bootstrapper.Dispose();
            throw;
        }
    }

    public DocumentPipeClient CreateBridgeClient() =>
        new(_endpoint, _secret);

    public void Dispose()
    {
        int timedOutReadyCallbackCount;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Ready = null;
            _uiBaseUri = null;
            _panelParentCredential = null;
            _panelBootstrapNonce = null;
            _panelBootstrapDocumentSerial = null;
            var callbackDeadline = Stopwatch.StartNew();
            while (_activeReadyCallbacks != 0 &&
                   callbackDeadline.Elapsed < TimeSpan.FromSeconds(2))
            {
                var remaining = TimeSpan.FromSeconds(2) - callbackDeadline.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }
                Monitor.Wait(_gate, remaining);
            }
            timedOutReadyCallbackCount = _activeReadyCallbacks;
        }
        if (timedOutReadyCallbackCount != 0)
        {
            DevelopmentDiagnosticTrace.TryWrite(
                "Rhino",
                "agent-ready-callback-timeout",
                $"active={timedOutReadyCallbackCount}");
        }

        try
        {
            try
            {
                _lifetime.Cancel();
            }
            catch (Exception exception) when (
                exception is AggregateException or ObjectDisposedException)
            {
                DevelopmentDiagnosticTrace.TryWriteException(
                    "Rhino",
                    "agent-cancellation-failed",
                    exception);
            }

            _process.OutputDataReceived -= OnOutput;
            _process.ErrorDataReceived -= OnError;
            _process.Exited -= OnExited;

            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    if (!_process.WaitForExit(2_000) && !_process.HasExited)
                    {
                        DevelopmentDiagnosticTrace.TryWrite(
                            "Rhino",
                            "agent-termination-timeout",
                            $"pid={_process.Id}");
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited between the check and termination.
            }
            catch (Exception exception) when (
                exception is System.ComponentModel.Win32Exception or NotSupportedException)
            {
                DevelopmentDiagnosticTrace.TryWriteException(
                    "Rhino",
                    "agent-termination-failed",
                    exception);
            }
        }
        finally
        {
            _process.Dispose();
            _lifetime.Dispose();
        }
    }

    private static string ResolveAgentHostPath(string assemblyDirectory)
    {
        var candidates = new[]
        {
            Path.Combine(assemblyDirectory, "GPTino.AgentHost.exe"),
            Path.Combine(assemblyDirectory, "agent", "GPTino.AgentHost.exe"),
        };

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(
                "GPTino.AgentHost.exe was not found beside the Rhino plug-in.",
                candidates[0]);
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

    private void OnOutput(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is not { } line || !line.StartsWith(ReadyPrefix, StringComparison.Ordinal))
        {
            return;
        }

        AgentHostReady ready;
        Uri uri;
        try
        {
            ready = JsonSerializer.Deserialize<AgentHostReady>(
                line[ReadyPrefix.Length..],
                BridgeProtocol.JsonOptions)
                ?? throw new JsonException("AgentHost readiness payload was null.");
            uri = new Uri(ready.UiBaseUrl, UriKind.Absolute);
            if (!IsLoopbackHttp(uri))
            {
                throw new InvalidDataException("AgentHost UI URL must use HTTP on loopback.");
            }
            if (string.IsNullOrWhiteSpace(ready.PanelParentCredential) || ready.PanelParentCredential.Length != 64)
            {
                throw new InvalidDataException("AgentHost panel parent credential is invalid.");
            }
        }
        catch (Exception exception) when (exception is JsonException or UriFormatException or InvalidDataException)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                _status = "AgentHost announced an invalid UI endpoint.";
            }
            DevelopmentDiagnosticTrace.TryWriteException(
                "Rhino",
                "agent-ready-invalid",
                exception);
            return;
        }

        EventHandler? readyHandler;
        int processId;
        Exception? readyHandlerFailure = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _uiBaseUri = uri;
            _panelParentCredential = ready.PanelParentCredential;
            _status = "AgentHost UI is ready; connecting the document bridge.";
            processId = _process.Id;
            readyHandler = Ready;
            if (readyHandler is not null)
            {
                _activeReadyCallbacks++;
            }
        }

        try
        {
            readyHandler?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            readyHandlerFailure = exception;
        }
        finally
        {
            if (readyHandler is not null)
            {
                lock (_gate)
                {
                    if (readyHandlerFailure is not null && !_disposed)
                    {
                        _status = "AgentHost is ready, but the document bridge callback failed.";
                    }
                    _activeReadyCallbacks--;
                    Monitor.PulseAll(_gate);
                }
            }
        }
        if (readyHandlerFailure is not null)
        {
            DevelopmentDiagnosticTrace.TryWriteException(
                "Rhino",
                "agent-ready-handler-failed",
                readyHandlerFailure);
            return;
        }
        DevelopmentDiagnosticTrace.TryWrite(
            "Rhino",
            "agent-ready",
            $"pid={processId}");
    }

    private void OnError(object sender, DataReceivedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Data))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _status = "AgentHost reported an error. See the local GPTino runtime log.";
        }
        DevelopmentDiagnosticTrace.TryWriteStandardError("Rhino", args.Data);
    }

    private void OnExited(object? sender, EventArgs args)
    {
        int? exitCode = null;
        var shouldTrace = false;
        try
        {
            try
            {
                exitCode = (sender as Process ?? _process).ExitCode;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or ObjectDisposedException)
            {
                // Disposal can race an already queued Exited callback.
            }

            lock (_gate)
            {
                if (!_disposed)
                {
                    shouldTrace = true;
                    _status = exitCode is { } code
                        ? $"AgentHost exited with code {code}."
                        : "AgentHost exited.";
                    _uiBaseUri = null;
                    _panelParentCredential = null;
                    _panelBootstrapNonce = null;
                    _panelBootstrapDocumentSerial = null;
                }
            }

            if (shouldTrace)
            {
                DevelopmentDiagnosticTrace.TryWrite(
                    "Rhino",
                    "agent-exited",
                    exitCode is { } codeValue ? $"code={codeValue}" : "code=unavailable");
            }
        }
        finally
        {
            try
            {
                _lifetime.Cancel();
            }
            catch (Exception exception) when (
                exception is AggregateException or ObjectDisposedException)
            {
                // Dispose can complete while an already queued Exited callback is running.
                DevelopmentDiagnosticTrace.TryWriteException(
                    "Rhino",
                    "agent-exit-cancellation-failed",
                    exception);
            }
        }
    }

    private async Task RequestPanelBootstrapNonceAsync(
        Uri baseUri,
        string parentCredential,
        uint documentSerial,
        CancellationToken cancellationToken)
    {
        try
        {
            var builder = new UriBuilder(new Uri(baseUri, "panel/bootstrap"))
            {
                Query = $"documentSerial={documentSerial}",
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, builder.Uri);
            request.Headers.TryAddWithoutValidation("X-GPTino-Panel-Parent", parentCredential);
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<PanelBootstrapResponse>(json, BridgeProtocol.JsonOptions)
                ?? throw new InvalidDataException("Panel bootstrap response was empty.");
            if (result.Nonce.Length != 64)
            {
                throw new InvalidDataException("Panel bootstrap response contained an invalid nonce.");
            }

            lock (_gate)
            {
                if (!_disposed)
                {
                    _panelBootstrapNonce = result.Nonce;
                    _panelBootstrapDocumentSerial = documentSerial;
                    _status = "AgentHost UI is ready; connecting the document bridge.";
                }
            }
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException)
        {
            lock (_gate)
            {
                if (!_disposed)
                {
                    _nextPanelNonceAttempt = DateTimeOffset.UtcNow.AddSeconds(1);
                    _status = "Could not authorize the Rhino panel; retrying.";
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _panelNonceRequest = null;
            }
        }
    }

    private static bool IsLoopbackHttp(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp &&
        (IPAddress.TryParse(uri.Host, out var address)
            ? IPAddress.IsLoopback(address)
            : string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    private sealed record AgentHostReady(string UiBaseUrl, string PanelParentCredential);

    private sealed record PanelBootstrapResponse(string Nonce);
}
