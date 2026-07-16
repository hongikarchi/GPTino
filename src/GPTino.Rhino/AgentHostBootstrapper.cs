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
                return _uiBaseUri;
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

    public bool TryTakePanelBootstrapNonce(uint documentSerial, out string nonce)
    {
        if (documentSerial == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(documentSerial));
        }

        lock (_gate)
        {
            if (_panelBootstrapNonce is not null &&
                _panelBootstrapDocumentSerial == documentSerial)
            {
                nonce = _panelBootstrapNonce;
                _panelBootstrapNonce = null;
                _panelBootstrapDocumentSerial = null;
                return true;
            }

            if (!_disposed &&
                _uiBaseUri is not null &&
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
        startInfo.Environment["GPTINO_BRIDGE_PIPE"] = endpoint.Name;
        startInfo.Environment["GPTINO_BRIDGE_SECRET"] = secret.ExportBase64();

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var bootstrapper = new AgentHostBootstrapper(process, endpoint, secret);
        process.OutputDataReceived += bootstrapper.OnOutput;
        process.ErrorDataReceived += bootstrapper.OnError;
        process.Exited += bootstrapper.OnExited;

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("AgentHost did not start.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return bootstrapper;
    }

    public DocumentPipeClient CreateBridgeClient() =>
        new(_endpoint, _secret);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _lifetime.Cancel();

        _process.OutputDataReceived -= OnOutput;
        _process.ErrorDataReceived -= OnError;
        _process.Exited -= OnExited;
        Ready = null;

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2_000);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and termination.
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

    private void OnOutput(object sender, DataReceivedEventArgs args)
    {
        if (args.Data is not { } line || !line.StartsWith(ReadyPrefix, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var ready = JsonSerializer.Deserialize<AgentHostReady>(
                line[ReadyPrefix.Length..],
                BridgeProtocol.JsonOptions)
                ?? throw new JsonException("AgentHost readiness payload was null.");
            var uri = new Uri(ready.UiBaseUrl, UriKind.Absolute);
            if (!IsLoopbackHttp(uri))
            {
                throw new InvalidDataException("AgentHost UI URL must use HTTP on loopback.");
            }
            if (string.IsNullOrWhiteSpace(ready.PanelParentCredential) || ready.PanelParentCredential.Length != 64)
            {
                throw new InvalidDataException("AgentHost panel parent credential is invalid.");
            }

            lock (_gate)
            {
                _uiBaseUri = uri;
                _panelParentCredential = ready.PanelParentCredential;
                _status = "AgentHost UI is ready; connecting the document bridge.";
            }

            Ready?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is JsonException or UriFormatException or InvalidDataException)
        {
            lock (_gate)
            {
                _status = $"AgentHost announced an invalid UI endpoint: {exception.Message}";
            }
        }
    }

    private void OnError(object sender, DataReceivedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Data))
        {
            return;
        }

        lock (_gate)
        {
            _status = "AgentHost reported an error. See the local GPTino runtime log.";
        }
    }

    private void OnExited(object? sender, EventArgs args)
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _status = $"AgentHost exited with code {_process.ExitCode}.";
                _uiBaseUri = null;
                _panelParentCredential = null;
                _panelBootstrapNonce = null;
                _panelBootstrapDocumentSerial = null;
            }
        }

        _lifetime.Cancel();
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
                    _status = $"Could not authorize the Rhino panel; retrying: {exception.Message}";
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
