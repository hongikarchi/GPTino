using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace GPTino.AgentHost.Hosting;

public sealed class ReadySignalService : IHostedService
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IServer _server;
    private readonly EndpointRegistry _registry;
    private readonly AgentHostOptions _options;
    private readonly PanelBootstrapNonceStore _panelBootstrap;
    private readonly ILogger<ReadySignalService> _logger;

    public ReadySignalService(
        IHostApplicationLifetime lifetime,
        IServer server,
        EndpointRegistry registry,
        AgentHostOptions options,
        PanelBootstrapNonceStore panelBootstrap,
        ILogger<ReadySignalService> logger)
    {
        _lifetime = lifetime;
        _server = server;
        _registry = registry;
        _options = options;
        _panelBootstrap = panelBootstrap;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime.ApplicationStarted.Register(PublishEndpoint);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void PublishEndpoint()
    {
        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.Select(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null)
            .FirstOrDefault(uri => uri?.IsLoopback == true)
            ?? throw new InvalidOperationException("Kestrel did not publish a loopback address.");
        _registry.Set(address);

        var payload = JsonSerializer.Serialize(new
        {
            uiBaseUrl = address.ToString().TrimEnd('/'),
            panelParentCredential = _panelBootstrap.ParentCredential
        });
        Console.Out.WriteLine($"GPTINO_READY {payload}");
        Console.Out.Flush();

        try
        {
            var dataDirectory = _options.ResolveDataDirectory();
            Directory.CreateDirectory(dataDirectory);
            using var currentProcess = Process.GetCurrentProcess();
            var discovery = JsonSerializer.Serialize(
                new
                {
                    uiBaseUrl = address.ToString().TrimEnd('/'),
                    processId = Environment.ProcessId,
                    processStartTimeUtc = currentProcess.StartTime.ToUniversalTime(),
                    startedAt = DateTimeOffset.UtcNow
                },
                new JsonSerializerOptions { WriteIndented = true });
            WriteEndpointDiscoveryFile(dataDirectory, discovery);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not write the local endpoint discovery file.");
        }
    }

    private static void WriteEndpointDiscoveryFile(string dataDirectory, string discovery)
    {
        var endpointPath = Path.Combine(dataDirectory, "endpoint.json");
        var endpointInfo = new FileInfo(endpointPath);
        endpointInfo.Refresh();
        if (endpointInfo.LinkTarget is not null ||
            (endpointInfo.Exists && (endpointInfo.Attributes & FileAttributes.ReparsePoint) != 0))
        {
            throw new InvalidOperationException("The endpoint discovery file is a reparse point.");
        }

        var temporaryPath = Path.Combine(
            dataDirectory,
            $".endpoint-{Environment.ProcessId}-{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(discovery);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, endpointPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

/// <summary>
/// Authenticates the Rhino parent and issues short-lived, single-use panel nonces bound to the
/// document serial that launched this AgentHost. The API token is never exposed in this exchange.
/// </summary>
public sealed class PanelBootstrapNonceStore
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(2);
    private const int MaximumOutstandingNonces = 16;

    private readonly object _gate = new();
    private readonly uint? _documentSerial;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _lifetime;
    private readonly byte[] _parentCredentialHash;
    private readonly List<NonceEntry> _nonces = [];

    public PanelBootstrapNonceStore(AgentHostOptions options)
        : this(options.RhinoDocumentSerial, TimeProvider.System, DefaultLifetime)
    {
    }

    public PanelBootstrapNonceStore(
        uint? documentSerial,
        TimeProvider timeProvider,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        _documentSerial = documentSerial is > 0 ? documentSerial : null;
        _timeProvider = timeProvider;
        _lifetime = lifetime;
        ParentCredential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _parentCredentialHash = Hash(ParentCredential);
    }

    public string ParentCredential { get; }

    public bool IsBoundDocument(uint documentSerial) =>
        documentSerial != 0 && _documentSerial == documentSerial;

    public bool TryIssue(string? parentCredential, uint documentSerial, out string nonce)
    {
        nonce = string.Empty;
        if (!IsBoundDocument(documentSerial) ||
            parentCredential is null ||
            parentCredential.Length != 64)
        {
            return false;
        }

        var suppliedCredentialHash = Hash(parentCredential);
        bool authenticated;
        try
        {
            authenticated = CryptographicOperations.FixedTimeEquals(
                suppliedCredentialHash,
                _parentCredentialHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(suppliedCredentialHash);
        }
        if (!authenticated)
        {
            return false;
        }

        nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var entry = new NonceEntry(Hash(nonce), _timeProvider.GetUtcNow().Add(_lifetime));
        lock (_gate)
        {
            PruneExpiredNonces();
            if (_nonces.Count >= MaximumOutstandingNonces)
            {
                CryptographicOperations.ZeroMemory(_nonces[0].Hash);
                _nonces.RemoveAt(0);
            }
            _nonces.Add(entry);
        }

        return true;
    }

    public bool TryConsume(string? supplied, uint documentSerial)
    {
        if (documentSerial == 0 || supplied is null || supplied.Length != 64)
        {
            return false;
        }

        var suppliedHash = Hash(supplied);
        lock (_gate)
        {
            try
            {
                PruneExpiredNonces();
                if (_documentSerial != documentSerial)
                {
                    return false;
                }

                for (var index = 0; index < _nonces.Count; index++)
                {
                    if (!CryptographicOperations.FixedTimeEquals(suppliedHash, _nonces[index].Hash))
                    {
                        continue;
                    }

                    CryptographicOperations.ZeroMemory(_nonces[index].Hash);
                    _nonces.RemoveAt(index);
                    return true;
                }
                return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(suppliedHash);
            }
        }
    }

    private void PruneExpiredNonces()
    {
        var now = _timeProvider.GetUtcNow();
        for (var index = _nonces.Count - 1; index >= 0; index--)
        {
            if (now < _nonces[index].ExpiresAt)
            {
                continue;
            }

            CryptographicOperations.ZeroMemory(_nonces[index].Hash);
            _nonces.RemoveAt(index);
        }
    }

    private static byte[] Hash(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private sealed record NonceEntry(byte[] Hash, DateTimeOffset ExpiresAt);
}
