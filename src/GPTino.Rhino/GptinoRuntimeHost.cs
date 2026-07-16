using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GPTino.BridgeContract;
using GPTino.CordycepsAdapter;

namespace GPTino.Rhino;

/// <summary>
/// Process-wide bridge lifecycle. Documents are registered explicitly and every incoming operation
/// is rechecked against that registration before it reaches a UI-thread adapter.
/// </summary>
public sealed class GptinoRuntimeHost : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, DocumentTarget> _targets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<uint, string> _observedRhinoDocuments = new();
    private readonly ConcurrentDictionary<Guid, string> _observedGrasshopperDocuments = new();
    private readonly ConcurrentDictionary<BridgeAdapterOwner, IBridgeOperationHandler> _handlers = new();
    private AgentHostBootstrapper? _bootstrapper;
    private string? _plugInAssemblyPath;
    private Guid? _bootstrapProjectId;
    private DocumentPipeConnection? _connection;
    private Task? _connectionTask;
    private CancellationTokenSource? _connectionLifetime;
    private string _bridgeStatus = "Document bridge has not started.";
    private int _connectionStarted;
    private bool _hubAttached;
    private bool _disposed;

    private GptinoRuntimeHost()
    {
    }

    public static GptinoRuntimeHost Instance { get; } = new();

    public string Status
    {
        get
        {
            lock (_gate)
            {
                return _connection is { IsConnected: true }
                    ? _bridgeStatus
                    : _bootstrapper?.Status ?? _bridgeStatus;
            }
        }
    }

    public void Start(string plugInAssemblyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plugInAssemblyPath);

        AttachProcessHub();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_plugInAssemblyPath is not null)
            {
                return;
            }
            _plugInAssemblyPath = Path.GetFullPath(plugInAssemblyPath);
            _bridgeStatus = "Waiting for one saved Rhino and Grasshopper file pair.";
        }

        foreach (var target in _targets.Values)
        {
            EnsureBootstrap(target);
        }
    }

    public void RegisterOperationHandler(IBridgeOperationHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[handler.Owner] = handler;

        foreach (var target in _targets.Values)
        {
            QueueRegistration(target);
        }
    }

    public void RegisterRhinoSceneAdapter(ICordycepsRhinoSceneAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        RegisterOperationHandler(new CordycepsRhinoBridgeOperationHandler(adapter));
    }

    /// <summary>Registers a fully specified pair. This method never infers an active document.</summary>
    public void RegisterDocument(DocumentTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Validate();
        _ = new ExplicitRhinoDocumentResolver().Resolve(target);

        var registered = _targets.AddOrUpdate(
            target.StableTargetKey(),
            target,
            (_, current) => target.Generation >= current.Generation ? target : current);
        EnsureBootstrap(registered);
        QueueRegistration(registered);
    }

    /// <summary>
    /// Records a panel's exact Rhino serial. Automatic pairing occurs only when exactly one saved
    /// Rhino document and one saved GH document have been observed; otherwise explicit registration
    /// remains required.
    /// </summary>
    public void ObserveRhinoDocument(uint documentSerial)
    {
        if (documentSerial == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(documentSerial));
        }

        var document = global::Rhino.RhinoDoc.FromRuntimeSerialNumber(documentSerial);
        if (document is null || string.IsNullOrWhiteSpace(document.Path))
        {
            return;
        }

        _observedRhinoDocuments[documentSerial] = Path.GetFullPath(document.Path);
        TryRegisterUnambiguousPair();
    }

    public void ObserveGrasshopperDocument(Guid documentId, string filePath)
    {
        if (documentId == Guid.Empty || string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath))
        {
            return;
        }

        _observedGrasshopperDocuments[documentId] = Path.GetFullPath(filePath);
        TryRegisterUnambiguousPair();
    }

    public void ForgetGrasshopperDocument(Guid documentId)
    {
        if (documentId != Guid.Empty)
        {
            _observedGrasshopperDocuments.TryRemove(documentId, out _);
            RemoveTargets(
                target => target.GrasshopperDocumentId == documentId,
                "Grasshopper document closed.");
        }
    }

    public void ForgetRhinoDocument(uint documentSerial)
    {
        if (documentSerial != 0)
        {
            _observedRhinoDocuments.TryRemove(documentSerial, out _);
            RemoveTargets(
                target => target.RhinoDocumentSerial == documentSerial,
                "Rhino document closed.");
        }
    }

    public bool TryGetPanelUri(uint documentSerial, out Uri uri)
    {
        if (documentSerial == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(documentSerial));
        }

        lock (_gate)
        {
            var target = _targets.Values.FirstOrDefault(candidate =>
                candidate.ProjectId == _bootstrapProjectId &&
                candidate.RhinoDocumentSerial == documentSerial);
            if (_bootstrapper?.UiBaseUri is not { } baseUri ||
                target is null)
            {
                uri = null!;
                return false;
            }

            try
            {
                _ = new ExplicitRhinoDocumentResolver().Resolve(target);
            }
            catch (DocumentTargetUnavailableException)
            {
                uri = null!;
                return false;
            }

            if (!_bootstrapper.TryTakePanelBootstrapNonce(documentSerial, out var panelBootstrapNonce))
            {
                uri = null!;
                return false;
            }

            var builder = new UriBuilder(new Uri(baseUri, "panel"))
            {
                Query = $"documentSerial={documentSerial}&bootstrap={Uri.EscapeDataString(panelBootstrapNonce)}",
            };
            uri = builder.Uri;
            return true;
        }
    }

    public DocumentPipeClient CreateBridgeClient()
    {
        lock (_gate)
        {
            return _bootstrapper?.CreateBridgeClient()
                ?? throw new InvalidOperationException("AgentHost has not started.");
        }
    }

    public void Dispose()
    {
        AgentHostBootstrapper? bootstrapper;
        DocumentPipeConnection? connection;
        Task? connectionTask;
        CancellationTokenSource? connectionLifetime;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            bootstrapper = _bootstrapper;
            connection = _connection;
            connectionTask = _connectionTask;
            connectionLifetime = _connectionLifetime;
            _bootstrapper = null;
            _plugInAssemblyPath = null;
            _bootstrapProjectId = null;
            _connection = null;
            _connectionLifetime = null;
        }

        _lifetime.Cancel();
        connectionLifetime?.Cancel();
        if (connection is not null)
        {
            connection.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (connectionTask is not null)
        {
            try
            {
                connectionTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException exception) when (
                exception.InnerExceptions.All(inner => inner is OperationCanceledException or IOException))
            {
            }
        }

        if (bootstrapper is not null)
        {
            bootstrapper.Ready -= OnAgentHostReady;
            bootstrapper.Dispose();
        }
        connectionLifetime?.Dispose();

        if (_hubAttached)
        {
            BridgeProcessHub.GrasshopperDocumentObserved -= ObserveGrasshopperDocument;
            BridgeProcessHub.GrasshopperDocumentForgotten -= ForgetGrasshopperDocument;
            BridgeProcessHub.OperationHandlerRegistered -= RegisterOperationHandler;
            _hubAttached = false;
        }

        _lifetime.Dispose();
    }

    private void AttachProcessHub()
    {
        lock (_gate)
        {
            if (_hubAttached)
            {
                return;
            }

            BridgeProcessHub.GrasshopperDocumentObserved += ObserveGrasshopperDocument;
            BridgeProcessHub.GrasshopperDocumentForgotten += ForgetGrasshopperDocument;
            BridgeProcessHub.OperationHandlerRegistered += RegisterOperationHandler;
            _hubAttached = true;
        }

        foreach (var pair in BridgeProcessHub.GetGrasshopperDocuments())
        {
            ObserveGrasshopperDocument(pair.Key, pair.Value);
        }

        foreach (var handler in BridgeProcessHub.GetOperationHandlers())
        {
            RegisterOperationHandler(handler);
        }
    }

    private void OnAgentHostReady(object? sender, EventArgs args)
    {
        if (sender is AgentHostBootstrapper bootstrapper)
        {
            BeginConnection(bootstrapper);
        }
    }

    private void EnsureBootstrap(DocumentTarget target)
    {
        AgentHostBootstrapper? bootstrapper;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_bootstrapper is not null)
            {
                if (_bootstrapProjectId != target.ProjectId)
                {
                    throw new InvalidOperationException(
                        "This GPTino runtime is already bound to another Rhino/Grasshopper file pair.");
                }
                return;
            }
            if (_plugInAssemblyPath is null)
            {
                return;
            }

            bootstrapper = AgentHostBootstrapper.Start(_plugInAssemblyPath, target);
            bootstrapper.Ready += OnAgentHostReady;
            _bootstrapper = bootstrapper;
            _bootstrapProjectId = target.ProjectId;
            _connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _bridgeStatus = "Starting the file-pair AgentHost.";
        }

        if (bootstrapper.UiBaseUri is not null)
        {
            BeginConnection(bootstrapper);
        }
    }

    private void BeginConnection(AgentHostBootstrapper bootstrapper)
    {
        if (Interlocked.Exchange(ref _connectionStarted, 1) != 0)
        {
            return;
        }

        lock (_gate)
        {
            _connectionLifetime ??= CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _bridgeStatus = "Connecting the authenticated document bridge.";
            _connectionTask = Task.Run(
                () => ConnectAndReceiveAsync(bootstrapper, _connectionLifetime.Token),
                _connectionLifetime.Token);
        }
    }

    private async Task ConnectAndReceiveAsync(
        AgentHostBootstrapper bootstrapper,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            DocumentPipeConnection? connection = null;
            try
            {
                var client = bootstrapper.CreateBridgeClient();
                connection = await client.ConnectAsync(
                    $"rhino-{Environment.ProcessId}",
                    TimeSpan.FromSeconds(15),
                    cancellationToken).ConfigureAwait(false);

                lock (_gate)
                {
                    _connection = connection;
                    _bridgeStatus = "AgentHost and document bridge are connected.";
                }

                foreach (var target in _targets.Values)
                {
                    await SendRegistrationAsync(connection, target, cancellationToken).ConfigureAwait(false);
                }

                while (!cancellationToken.IsCancellationRequested && connection.IsConnected)
                {
                    var frame = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                    await ProcessIncomingFrameAsync(connection, frame, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or UnauthorizedAccessException or BridgeProtocolException)
            {
                lock (_gate)
                {
                    _bridgeStatus = $"Document bridge disconnected; retrying: {exception.Message}";
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_connection, connection))
                    {
                        _connection = null;
                    }
                }
                if (connection is not null)
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ProcessIncomingFrameAsync(
        DocumentPipeConnection connection,
        BridgeFrame frame,
        CancellationToken cancellationToken)
    {
        if (frame.Kind != BridgeMessageKind.Request)
        {
            return;
        }

        try
        {
            var target = RequireRegisteredTarget(frame.Target);
            BridgeFrame response;

            if (string.Equals(frame.PayloadType, BridgeMessageTypes.HealthRequest, StringComparison.Ordinal))
            {
                var request = frame.DeserializePayload<BridgeHealthRequest>();
                var health = await RhinoUiThreadDispatcher.InvokeAsync(
                    () =>
                    {
                        _ = new ExplicitRhinoDocumentResolver().Resolve(target);
                        return Task.FromResult(new BridgeHealthResponse(
                            request.ProbeId,
                            Healthy: true,
                            $"rhino-{Environment.ProcessId}",
                            target.StableTargetKey(),
                            target.Generation,
                            DateTimeOffset.UtcNow));
                    },
                    cancellationToken).ConfigureAwait(false);
                response = BridgeFrame.Create(
                    BridgeMessageKind.Response,
                    BridgeMessageTypes.HealthResponse,
                    health,
                    target,
                    frame.MessageId);
            }
            else if (string.Equals(frame.PayloadType, BridgeMessageTypes.OperationRequest, StringComparison.Ordinal))
            {
                var request = frame.DeserializePayload<BridgeOperationRequest>();
                request.Validate();
                if (!_handlers.TryGetValue(request.Owner, out var handler))
                {
                    throw new BridgeProtocolException(
                        "adapter_unavailable",
                        $"Adapter '{request.Owner}' is not registered for this Rhino process.");
                }

                var result = await RhinoUiThreadDispatcher.InvokeAsync(
                    () => handler.HandleAsync(target, request, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                response = BridgeFrame.Create(
                    BridgeMessageKind.Response,
                    BridgeMessageTypes.OperationResponse,
                    result,
                    target,
                    frame.MessageId);
            }
            else
            {
                throw new BridgeProtocolException(
                    "unknown_request",
                    $"Unknown bridge request payload '{frame.PayloadType}'.");
            }

            await connection.SendAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var failure = new BridgeFailure(
                exception is BridgeProtocolException protocolException
                    ? protocolException.Code
                    : exception is DocumentTargetMismatchException or DocumentTargetUnavailableException
                        ? "document_target_mismatch"
                        : "bridge_operation_failed",
                exception.Message,
                Retryable: exception is IOException,
                TryReadOperationId(frame));
            var error = BridgeFrame.Create(
                BridgeMessageKind.Error,
                "bridge.failure",
                failure,
                frame.Target,
                frame.MessageId) with
            {
                ErrorCode = failure.Code,
            };
            await connection.SendAsync(error, cancellationToken).ConfigureAwait(false);
        }
    }

    private DocumentTarget RequireRegisteredTarget(DocumentTarget? requested)
    {
        if (requested is null)
        {
            throw new BridgeProtocolException("target_required", "Bridge request has no document target.");
        }

        requested.Validate();
        if (!_targets.TryGetValue(requested.StableTargetKey(), out var registered))
        {
            throw new DocumentTargetUnavailableException(
                $"Target {requested.StableTargetKey()} is not registered in this Rhino process.");
        }

        DocumentTargetGuard.RequireCurrent(registered, requested);
        return registered;
    }

    private void QueueRegistration(DocumentTarget target)
    {
        DocumentPipeConnection? connection;
        lock (_gate)
        {
            connection = _connection;
        }

        if (connection is not { IsConnected: true })
        {
            return;
        }

        _ = SendRegistrationSafelyAsync(connection, target, _lifetime.Token);
    }

    private void RemoveTargets(Func<DocumentTarget, bool> predicate, string reason)
    {
        foreach (var pair in _targets.Where(pair => predicate(pair.Value)).ToArray())
        {
            if (!_targets.TryRemove(pair.Key, out var removed))
            {
                continue;
            }

            DocumentPipeConnection? connection;
            lock (_gate)
            {
                connection = _connection;
            }

            if (connection is { IsConnected: true })
            {
                _ = SendDocumentClosedSafelyAsync(connection, removed, reason, _lifetime.Token);
            }
        }

        if (_targets.IsEmpty)
        {
            ResetFilePairRuntime();
        }
    }

    private void ResetFilePairRuntime()
    {
        AgentHostBootstrapper? bootstrapper;
        CancellationTokenSource? connectionLifetime;
        Task? connectionTask;
        lock (_gate)
        {
            if (_targets.Count != 0 || _bootstrapper is null)
            {
                return;
            }

            bootstrapper = _bootstrapper;
            connectionLifetime = _connectionLifetime;
            connectionTask = _connectionTask;
            _bootstrapper = null;
            _bootstrapProjectId = null;
            _connectionLifetime = null;
            _connectionTask = null;
            _connection = null;
            _bridgeStatus = "Waiting for one saved Rhino and Grasshopper file pair.";
            Interlocked.Exchange(ref _connectionStarted, 0);
        }

        connectionLifetime?.Cancel();
        bootstrapper.Ready -= OnAgentHostReady;
        bootstrapper.Dispose();
        if (connectionTask is not null)
        {
            try
            {
                connectionTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException exception) when (
                exception.InnerExceptions.All(inner => inner is OperationCanceledException or IOException))
            {
            }
        }
        connectionLifetime?.Dispose();
    }

    private static async Task SendDocumentClosedSafelyAsync(
        DocumentPipeConnection connection,
        DocumentTarget target,
        string reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await connection.SendAsync(
                BridgeFrame.Create(
                    BridgeMessageKind.Event,
                    BridgeMessageTypes.DocumentClosed,
                    new DocumentClosedEvent(reason, target.Generation),
                    target),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
        }
    }

    private async Task SendRegistrationSafelyAsync(
        DocumentPipeConnection connection,
        DocumentTarget target,
        CancellationToken cancellationToken)
    {
        try
        {
            await SendRegistrationAsync(connection, target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        {
            lock (_gate)
            {
                _bridgeStatus = $"Could not register document target: {exception.Message}";
            }
        }
    }

    private Task SendRegistrationAsync(
        DocumentPipeConnection connection,
        DocumentTarget target,
        CancellationToken cancellationToken)
    {
        var request = new RegisterDocumentRequest(
            $"rhino-{Environment.ProcessId}",
            GetType().Assembly.GetName().Version?.ToString() ?? "0.0.0",
            _handlers.Keys.OrderBy(owner => owner).ToArray());
        var frame = BridgeFrame.Create(
            BridgeMessageKind.Event,
            BridgeMessageTypes.RegisterDocument,
            request,
            target);
        return connection.SendAsync(frame, cancellationToken).AsTask();
    }

    private void TryRegisterUnambiguousPair()
    {
        if (_observedRhinoDocuments.Count != 1 || _observedGrasshopperDocuments.Count != 1)
        {
            return;
        }

        var rhinoPair = _observedRhinoDocuments.Single();
        var grasshopperPair = _observedGrasshopperDocuments.Single();
        using var process = Process.GetCurrentProcess();
        var target = DocumentRuntimeTarget.Create(
            CreateProjectId(rhinoPair.Value, grasshopperPair.Value),
            process.Id,
            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero),
            rhinoPair.Key,
            grasshopperPair.Key,
            rhinoPair.Value,
            grasshopperPair.Value);
        RegisterDocument(target);
    }

    private static Guid CreateProjectId(string rhinoPath, string grasshopperPath)
    {
        var canonical = $"{Path.GetFullPath(rhinoPath).ToUpperInvariant()}\n" +
            Path.GetFullPath(grasshopperPath).ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string? TryReadOperationId(BridgeFrame frame)
    {
        try
        {
            return string.Equals(frame.PayloadType, BridgeMessageTypes.OperationRequest, StringComparison.Ordinal)
                ? frame.DeserializePayload<BridgeOperationRequest>().OperationId
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
