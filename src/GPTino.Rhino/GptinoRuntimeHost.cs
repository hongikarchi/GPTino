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
    private static readonly TimeSpan AgentHostReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BootstrapMonitorInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SelectionDebounceInterval = TimeSpan.FromMilliseconds(250);
    private const int MaximumSelectionIds = 512;

    private readonly object _gate = new();
    private readonly object _observationGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, DocumentTarget> _targets = new(StringComparer.Ordinal);
    private readonly AutomaticRestartPolicy _automaticRestartPolicy = new();
    private readonly Dictionary<uint, string> _observedRhinoDocuments = [];
    private readonly Dictionary<Guid, string> _observedGrasshopperDocuments = [];
    private readonly ConcurrentDictionary<BridgeAdapterOwner, IBridgeOperationHandler> _handlers = new();
    private AgentHostBootstrapper? _bootstrapper;
    private Timer? _selectionDebounceTimer;
    private uint _pendingSelectionSerial;
    private string? _plugInAssemblyPath;
    private Guid? _bootstrapProjectId;
    private DocumentPipeConnection? _connection;
    private Task? _bootstrapMonitorTask;
    private Task? _connectionTask;
    private CancellationTokenSource? _connectionLifetime;
    private string _bridgeStatus = "Document bridge has not started.";
    private long _runtimeGeneration;
    private int _connectionStarted;
    private bool _automaticRestartPending;
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
            DocumentPipeConnection? connection;
            AgentHostBootstrapper? bootstrapper;
            string bridgeStatus;
            lock (_gate)
            {
                connection = _connection;
                bootstrapper = _bootstrapper;
                bridgeStatus = _bridgeStatus;
            }

            try
            {
                if (connection is { IsConnected: true })
                {
                    return bridgeStatus;
                }
            }
            catch (ObjectDisposedException)
            {
                // The connection task owns disposal and may finish after this snapshot.
            }

            return bootstrapper?.Status ?? bridgeStatus;
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

        DocumentTarget registered;
        DetachedRuntime? detached = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var boundToDifferentProject =
                _targets.Values.Any(existing => existing.ProjectId != target.ProjectId) ||
                _bootstrapProjectId is { } bootstrapProjectId && bootstrapProjectId != target.ProjectId;
            if (boundToDifferentProject)
            {
                // The observed file pair changed identity (Save As, rename, or close+reopen). Rebind to
                // the new pair instead of staying bound to a pair that no longer exists: drop the stale
                // targets and tear down the old AgentHost so the new pair can bootstrap cleanly. Without
                // this a single Save As permanently strands the session on a dead AgentHost.
                foreach (var staleKey in _targets.Keys.ToArray())
                {
                    _targets.TryRemove(staleKey, out _);
                }
                detached = DetachRuntimeLocked("Rebinding to the current Rhino/Grasshopper file pair.");
                ResetAutomaticRestartPolicyLocked();
                DevelopmentDiagnosticTrace.TryWrite(
                    "Rhino",
                    "runtime-rebind",
                    $"project={target.ProjectId:D}");
            }
            registered = _targets.AddOrUpdate(
                target.StableTargetKey(),
                target,
                (_, current) => target.Generation >= current.Generation ? target : current);
        }
        StopDetachedRuntime(detached);
        EnsureBootstrap(registered);
        QueueRegistration(registered);
    }

    /// <summary>
    /// Records a panel's exact Rhino serial. Automatic pairing occurs only when exactly one saved
    /// Rhino document and one saved GH document have been observed; otherwise explicit registration
    /// remains required.
    /// </summary>
    public void ObserveRhinoDocument(uint documentSerial) =>
        ObserveRhinoDocument(documentSerial, explicitPath: null);

    /// <param name="explicitPath">
    /// The authoritative file path from a save event (DocumentSaveEventArgs.FileName). Preferred over
    /// RhinoDoc.Path because at EndSaveDocument the document's Path can still report the pre-Save-As value,
    /// which would register (and display) a stale path even though the live binding — keyed on the runtime
    /// serial — is correct. Null for open/panel observations, which read the current Path.
    /// </param>
    public void ObserveRhinoDocument(uint documentSerial, string? explicitPath)
    {
        if (documentSerial == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(documentSerial));
        }

        var document = global::Rhino.RhinoDoc.FromRuntimeSerialNumber(documentSerial);
        if (document is null)
        {
            return;
        }

        var rawPath = !string.IsNullOrWhiteSpace(explicitPath) && Path.IsPathFullyQualified(explicitPath)
            ? explicitPath
            : document.Path;
        if (string.IsNullOrWhiteSpace(rawPath) || !Path.IsPathFullyQualified(rawPath))
        {
            return;
        }

        var normalizedPath = Path.GetFullPath(rawPath);
        if (RhinoAutoSavePaths.IsAutoSavePath(normalizedPath))
        {
            // A document living at an autosave path (crash recovery, or the user opening the
            // autosave copy directly) must not become the registered identity; the waiting page
            // tells the user to Save As so the real path binds instead.
            DevelopmentDiagnosticTrace.TryWrite(
                "Rhino",
                "rhino-document-autosave-ignored",
                $"serial={documentSerial}");
            return;
        }
        bool changed;
        int observedCount;
        lock (_observationGate)
        {
            changed = !_observedRhinoDocuments.TryGetValue(documentSerial, out var previousPath) ||
                !string.Equals(previousPath, normalizedPath, StringComparison.OrdinalIgnoreCase);
            _observedRhinoDocuments[documentSerial] = normalizedPath;
            observedCount = _observedRhinoDocuments.Count;
        }
        DevelopmentDiagnosticTrace.TryWrite(
            "Rhino",
            "rhino-document-observed",
            $"serial={documentSerial};count={observedCount}");
        // A Save As / rename changes document.Path but NOT the runtime serial, so re-registering in place
        // refreshes the path metadata while keeping the AgentHost/session/codex state alive. Only do it when
        // the pair genuinely changed (a new serial or a new path): a repeated observation of the same
        // serial+path — e.g. the panel re-observing on show — is a true no-op, skipped to avoid redundant
        // re-registration / schedule / event churn.
        if (changed)
        {
            TryRegisterUnambiguousPair();
        }
    }

    public void ObserveGrasshopperDocument(Guid documentId, string filePath)
    {
        if (documentId == Guid.Empty || string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath))
        {
            return;
        }

        var normalizedPath = Path.GetFullPath(filePath);
        bool changed;
        int observedCount;
        lock (_observationGate)
        {
            changed = !_observedGrasshopperDocuments.TryGetValue(documentId, out var previousPath) ||
                !string.Equals(previousPath, normalizedPath, StringComparison.OrdinalIgnoreCase);
            _observedGrasshopperDocuments[documentId] = normalizedPath;
            observedCount = _observedGrasshopperDocuments.Count;
        }
        DevelopmentDiagnosticTrace.TryWrite(
            "Rhino",
            "grasshopper-document-observed",
            $"id={documentId:D};count={observedCount}");
        // Save As of the .gh changes its path but keeps the same GH DocumentID; re-register in place to refresh
        // it. Skip a true no-op (same id + same path) to avoid redundant re-registration churn.
        if (changed)
        {
            TryRegisterUnambiguousPair();
        }
    }

    public void ForgetGrasshopperDocument(Guid documentId)
    {
        if (documentId != Guid.Empty)
        {
            lock (_observationGate)
            {
                _observedGrasshopperDocuments.Remove(documentId);
            }
            RemoveTargets(
                target => target.GrasshopperDocumentId == documentId,
                "Grasshopper document closed.");
            TryRegisterUnambiguousPair();
        }
    }

    public void ForgetRhinoDocument(uint documentSerial)
    {
        if (documentSerial != 0)
        {
            lock (_observationGate)
            {
                _observedRhinoDocuments.Remove(documentSerial);
            }
            RemoveTargets(
                target => target.RhinoDocumentSerial == documentSerial,
                "Rhino document closed.");
            TryRegisterUnambiguousPair();
        }
    }

    public bool TryGetPanelUri(uint documentSerial, out Uri uri)
    {
        if (documentSerial == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(documentSerial));
        }

        AgentHostBootstrapper? bootstrapper;
        DocumentTarget? target;
        lock (_gate)
        {
            target = _targets.Values.FirstOrDefault(candidate =>
                candidate.ProjectId == _bootstrapProjectId &&
                candidate.RhinoDocumentSerial == documentSerial);
            bootstrapper = _bootstrapper;
        }
        if (bootstrapper?.UiBaseUri is not { } baseUri || target is null)
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

        if (!bootstrapper.TryTakePanelBootstrapNonce(documentSerial, out var panelBootstrapNonce))
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

    /// <summary>
    /// Reports the current AgentHost UI base URI for a panel's document serial without consuming a
    /// bootstrap nonce. The panel polls this to detect when the live endpoint changes (a rebind spawns
    /// a fresh AgentHost on a new port) so it can re-navigate instead of staying on the dead old port.
    /// </summary>
    public bool TryGetActivePanelBaseUri(uint documentSerial, out Uri baseUri)
    {
        baseUri = null!;
        if (documentSerial == 0)
        {
            return false;
        }

        lock (_gate)
        {
            var target = _targets.Values.FirstOrDefault(candidate =>
                candidate.ProjectId == _bootstrapProjectId &&
                candidate.RhinoDocumentSerial == documentSerial);
            if (target is null || _bootstrapper?.UiBaseUri is not { } uri)
            {
                return false;
            }
            baseUri = uri;
            return true;
        }
    }

    public DocumentPipeClient CreateBridgeClient()
    {
        AgentHostBootstrapper? bootstrapper;
        lock (_gate)
        {
            bootstrapper = _bootstrapper;
        }

        return bootstrapper?.CreateBridgeClient()
            ?? throw new InvalidOperationException("AgentHost has not started.");
    }

    public void Dispose()
    {
        DetachedRuntime? detached;
        bool hubAttached;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            detached = DetachRuntimeLocked("Document bridge stopped.");
            _plugInAssemblyPath = null;
            _targets.Clear();
            hubAttached = _hubAttached;
            _hubAttached = false;
        }

        lock (_observationGate)
        {
            _observedRhinoDocuments.Clear();
            _observedGrasshopperDocuments.Clear();
        }

        try
        {
            CancelSafely(_lifetime, "runtime-cancellation-failed");
            StopDetachedRuntime(detached);
            _selectionDebounceTimer?.Dispose();
        }
        finally
        {
            if (hubAttached)
            {
                BridgeProcessHub.GrasshopperDocumentObserved -= OnHubGrasshopperDocumentObserved;
                BridgeProcessHub.GrasshopperDocumentForgotten -= OnHubGrasshopperDocumentForgotten;
                BridgeProcessHub.OperationHandlerRegistered -= OnHubOperationHandlerRegistered;
                BridgeProcessHub.GrasshopperSelectionChanged -= OnHubGrasshopperSelectionChanged;
            }

            _lifetime.Dispose();
        }
    }

    private void AttachProcessHub()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_hubAttached)
            {
                return;
            }

            BridgeProcessHub.GrasshopperDocumentObserved += OnHubGrasshopperDocumentObserved;
            BridgeProcessHub.GrasshopperDocumentForgotten += OnHubGrasshopperDocumentForgotten;
            BridgeProcessHub.OperationHandlerRegistered += OnHubOperationHandlerRegistered;
            BridgeProcessHub.GrasshopperSelectionChanged += OnHubGrasshopperSelectionChanged;
            _hubAttached = true;
        }

        foreach (var pair in BridgeProcessHub.GetGrasshopperDocuments())
        {
            OnHubGrasshopperDocumentObserved(pair.Key, pair.Value);
        }

        foreach (var handler in BridgeProcessHub.GetOperationHandlers())
        {
            OnHubOperationHandlerRegistered(handler);
        }
    }

    private void OnHubGrasshopperDocumentObserved(Guid documentId, string filePath)
    {
        if (IsDisposed())
        {
            return;
        }
        try
        {
            ObserveGrasshopperDocument(documentId, filePath);
        }
        catch (ObjectDisposedException) when (IsDisposed())
        {
        }
    }

    private void OnHubGrasshopperDocumentForgotten(Guid documentId)
    {
        if (IsDisposed())
        {
            return;
        }
        try
        {
            ForgetGrasshopperDocument(documentId);
        }
        catch (ObjectDisposedException) when (IsDisposed())
        {
        }
    }

    private void OnHubOperationHandlerRegistered(IBridgeOperationHandler handler)
    {
        if (!IsDisposed())
        {
            RegisterOperationHandler(handler);
        }
    }

    private void OnHubGrasshopperSelectionChanged(
        Guid documentId,
        IReadOnlyList<GrasshopperSelectedObject> selection)
    {
        _ = selection; // The latest selection is read back from the hub at capture time.
        uint serial;
        lock (_gate)
        {
            if (_disposed || _connection is null)
            {
                return;
            }
            var target = _targets.Values.FirstOrDefault(
                candidate => candidate.GrasshopperDocumentId == documentId);
            if (target is null)
            {
                return;
            }
            serial = target.RhinoDocumentSerial;
        }
        // Reuse the Rhino selection debounce path: canvas clicks coalesce with viewport
        // selection into one settled SelectionChangedEvent per burst.
        NotifySelectionChanged(serial);
    }

    private bool IsDisposed()
    {
        lock (_gate)
        {
            return _disposed;
        }
    }

    private void OnAgentHostReady(object? sender, EventArgs args)
    {
        if (sender is AgentHostBootstrapper bootstrapper)
        {
            BeginConnection(bootstrapper);
        }
    }

    private void EnsureBootstrap(DocumentTarget target, bool reservedRestart = false)
    {
        AgentHostBootstrapper? bootstrapper;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_targets.TryGetValue(target.StableTargetKey(), out var currentTarget) ||
                !ReferenceEquals(currentTarget, target))
            {
                return;
            }
            if (reservedRestart && !_automaticRestartPending)
            {
                return;
            }
            if (_bootstrapper is not null)
            {
                if (_bootstrapProjectId != target.ProjectId)
                {
                    throw new InvalidOperationException(
                        "This GPTino runtime is already bound to another Rhino/Grasshopper file pair.");
                }
                bootstrapper = _bootstrapper;
            }
            else if (_plugInAssemblyPath is null ||
                _automaticRestartPending && !reservedRestart ||
                _automaticRestartPolicy.IsSuppressed)
            {
                return;
            }
            else
            {
                DevelopmentDiagnosticTrace.TryWrite(
                    "Rhino",
                    "agent-bootstrap-starting",
                    $"project={target.ProjectId:D}");
                bootstrapper = AgentHostBootstrapper.Start(_plugInAssemblyPath, target);
                bootstrapper.Ready += OnAgentHostReady;
                _bootstrapper = bootstrapper;
                _bootstrapProjectId = target.ProjectId;
                _connectionLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                _bridgeStatus = "Starting the file-pair AgentHost.";
                _runtimeGeneration++;
                _connectionStarted = 0;
                if (reservedRestart)
                {
                    _automaticRestartPending = false;
                }
                var generation = _runtimeGeneration;
                var cancellationToken = _connectionLifetime.Token;
                _bootstrapMonitorTask = Task.Run(
                    () => MonitorBootstrapAsync(bootstrapper, generation, cancellationToken));
            }
        }

        if (bootstrapper.UiBaseUri is not null)
        {
            BeginConnection(bootstrapper);
        }
    }

    private async Task MonitorBootstrapAsync(
        AgentHostBootstrapper bootstrapper,
        long generation,
        CancellationToken cancellationToken)
    {
        var readyDeadline = Stopwatch.StartNew();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                lock (_gate)
                {
                    if (!IsCurrentRuntimeLocked(bootstrapper, generation))
                    {
                        return;
                    }
                }

                if (bootstrapper.HasExited)
                {
                    RecoverFailedRuntime(
                        bootstrapper,
                        generation,
                        "AgentHost exited before its document bridge became ready.");
                    return;
                }

                if (bootstrapper.UiBaseUri is not null)
                {
                    // This also repairs a READY notification that arrived before the host subscribed.
                    BeginConnection(bootstrapper);
                    return;
                }

                if (readyDeadline.Elapsed >= AgentHostReadyTimeout)
                {
                    RecoverFailedRuntime(
                        bootstrapper,
                        generation,
                        "AgentHost did not become ready within 30 seconds.");
                    return;
                }

                await Task.Delay(BootstrapMonitorInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void BeginConnection(AgentHostBootstrapper bootstrapper)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_bootstrapper, bootstrapper))
            {
                return;
            }
            if (_connectionStarted != 0)
            {
                return;
            }
            _connectionStarted = 1;
            _connectionLifetime ??= CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var generation = _runtimeGeneration;
            var cancellationToken = _connectionLifetime.Token;
            _bridgeStatus = "Connecting the authenticated document bridge.";
            _connectionTask = Task.Run(
                () => ConnectAndReceiveAsync(bootstrapper, generation, cancellationToken),
                cancellationToken);
        }
    }

    private async Task ConnectAndReceiveAsync(
        AgentHostBootstrapper bootstrapper,
        long generation,
        CancellationToken cancellationToken)
    {
        var restartExitedRuntime = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (!IsCurrentRuntimeLocked(bootstrapper, generation))
                {
                    return;
                }
            }
            if (bootstrapper.HasExited)
            {
                restartExitedRuntime = true;
                break;
            }

            DocumentPipeConnection? connection = null;
            var staleGeneration = false;
            try
            {
                var client = bootstrapper.CreateBridgeClient();
                connection = await client.ConnectAsync(
                    $"rhino-{Environment.ProcessId}",
                    TimeSpan.FromSeconds(15),
                    cancellationToken).ConfigureAwait(false);

                lock (_gate)
                {
                    if (!IsCurrentRuntimeLocked(bootstrapper, generation))
                    {
                        staleGeneration = true;
                    }
                    else
                    {
                        _connection = connection;
                        _bridgeStatus = "AgentHost and document bridge are connected.";
                    }
                }
                if (staleGeneration)
                {
                    return;
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
                restartExitedRuntime = bootstrapper.HasExited;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or UnauthorizedAccessException or
                BridgeProtocolException or ObjectDisposedException)
            {
                lock (_gate)
                {
                    if (IsCurrentRuntimeLocked(bootstrapper, generation))
                    {
                        _bridgeStatus = "Document bridge disconnected; retrying.";
                    }
                }
                DevelopmentDiagnosticTrace.TryWriteException(
                    "Rhino",
                    "document-bridge-disconnected",
                    exception);
                restartExitedRuntime = bootstrapper.HasExited;
            }
            finally
            {
                lock (_gate)
                {
                    if (IsCurrentRuntimeLocked(bootstrapper, generation) &&
                        ReferenceEquals(_connection, connection))
                    {
                        _connection = null;
                    }
                }
                if (connection is not null)
                {
                    await DisposeConnectionSafelyAsync(connection).ConfigureAwait(false);
                }
            }

            if (restartExitedRuntime)
            {
                break;
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

        if (restartExitedRuntime && !cancellationToken.IsCancellationRequested)
        {
            RecoverFailedRuntime(bootstrapper, generation, "AgentHost exited unexpectedly.");
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
            // Reflection-backed adapters can surface TargetInvocationException whose outer
            // message ("Exception has been thrown by the target of an invocation") hides the
            // actionable cause; unwrap so sessions and operators see the real error.
            var effective = exception;
            while (effective is System.Reflection.TargetInvocationException { InnerException: { } inner })
            {
                effective = inner;
            }
            var failureDetail =
                $"operationId={TryReadOperationId(frame)};payloadType={frame.PayloadType};" +
                $"exceptionType={effective.GetType().FullName};message={effective.Message};" +
                $"trace={effective}";
            DevelopmentDiagnosticTrace.TryWrite(
                "Rhino",
                "bridge-op-failure-detail",
                failureDetail.Length > 1500 ? failureDetail[..1500] : failureDetail);
            var failure = new BridgeFailure(
                effective is BridgeProtocolException protocolException
                    ? protocolException.Code
                    : effective is DocumentTargetMismatchException or DocumentTargetUnavailableException
                        ? "document_target_mismatch"
                        : "bridge_operation_failed",
                effective.Message,
                Retryable: effective is IOException,
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
        CancellationToken cancellationToken;
        long generation;
        lock (_gate)
        {
            if (_disposed ||
                !_targets.TryGetValue(target.StableTargetKey(), out var currentTarget) ||
                !ReferenceEquals(currentTarget, target) ||
                _connectionLifetime is null)
            {
                return;
            }
            connection = _connection;
            cancellationToken = _connectionLifetime.Token;
            generation = _runtimeGeneration;
        }

        try
        {
            if (connection is not { IsConnected: true })
            {
                return;
            }
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        _ = SendRegistrationSafelyAsync(
            connection,
            target,
            generation,
            cancellationToken);
    }

    private void RemoveTargets(Func<DocumentTarget, bool> predicate, string reason)
    {
        List<DocumentTarget> removedTargets = [];
        DocumentPipeConnection? connection;
        CancellationToken cancellationToken;
        long generation;
        DetachedRuntime? detached = null;
        lock (_gate)
        {
            foreach (var pair in _targets.Where(pair => predicate(pair.Value)).ToArray())
            {
                if (_targets.TryRemove(pair.Key, out var removed))
                {
                    removedTargets.Add(removed);
                }
            }

            connection = _connection;
            cancellationToken = _connectionLifetime?.Token ?? _lifetime.Token;
            generation = _runtimeGeneration;
            if (_targets.IsEmpty)
            {
                ResetAutomaticRestartPolicyLocked();
                detached = DetachRuntimeLocked(
                    "Waiting for one saved Rhino and Grasshopper file pair.");
            }
        }

        if (detached is null && connection is not null)
        {
            foreach (var removed in removedTargets)
            {
                _ = SendDocumentClosedSafelyAsync(
                    connection,
                    removed,
                    reason,
                    generation,
                    cancellationToken);
            }
        }
        StopDetachedRuntime(detached);
    }

    private DetachedRuntime? DetachRuntimeLocked(string status)
    {
        var detached = new DetachedRuntime(
            _bootstrapper,
            _connectionLifetime,
            _bootstrapMonitorTask,
            _connectionTask);
        _bootstrapper = null;
        _bootstrapProjectId = null;
        _connectionLifetime = null;
        _bootstrapMonitorTask = null;
        _connectionTask = null;
        _connection = null;
        _bridgeStatus = status;
        _runtimeGeneration++;
        _connectionStarted = 0;
        return detached.Bootstrapper is null &&
            detached.ConnectionLifetime is null &&
            detached.BootstrapMonitorTask is null &&
            detached.ConnectionTask is null
                ? null
                : detached;
    }

    private static void CancelSafely(
        CancellationTokenSource cancellation,
        string diagnosticEvent)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (Exception exception) when (
            exception is AggregateException or ObjectDisposedException)
        {
            DevelopmentDiagnosticTrace.TryWriteException(
                "Rhino",
                diagnosticEvent,
                exception);
        }
    }

    private void StopDetachedRuntime(DetachedRuntime? detached)
    {
        if (detached is null)
        {
            return;
        }

        if (detached.ConnectionLifetime is { } connectionLifetime)
        {
            CancelSafely(connectionLifetime, "connection-cancellation-failed");
        }

        if (detached.Bootstrapper is { } bootstrapper)
        {
            try
            {
                bootstrapper.Ready -= OnAgentHostReady;
                bootstrapper.Dispose();
            }
            catch (Exception exception)
            {
                DevelopmentDiagnosticTrace.TryWriteException(
                    "Rhino",
                    "agent-bootstrap-dispose-failed",
                    exception);
            }
        }

        var runtimeTasks = new[]
        {
            detached.BootstrapMonitorTask,
            detached.ConnectionTask,
        }.OfType<Task>().Distinct().ToArray();
        if (runtimeTasks.Length != 0)
        {
            _ = ObserveRuntimeTasksAsync(runtimeTasks, detached.ConnectionLifetime);
        }
        else
        {
            detached.ConnectionLifetime?.Dispose();
        }
    }

    private static async Task ObserveRuntimeTasksAsync(
        IReadOnlyCollection<Task> runtimeTasks,
        CancellationTokenSource? connectionLifetime)
    {
        try
        {
            await Task.WhenAll(runtimeTasks).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            DevelopmentDiagnosticTrace.TryWriteException(
                "Rhino",
                "runtime-task-failed",
                exception);
        }
        finally
        {
            connectionLifetime?.Dispose();
        }
    }

    private void RecoverFailedRuntime(
        AgentHostBootstrapper bootstrapper,
        long generation,
        string failureStatus)
    {
        DetachedRuntime? detached;
        TimeSpan restartDelay;
        long detachedGeneration;
        CancellationToken lifetimeToken;
        lock (_gate)
        {
            if (_disposed || !IsCurrentRuntimeLocked(bootstrapper, generation))
            {
                return;
            }

            detached = DetachRuntimeLocked(failureStatus);
            detachedGeneration = _runtimeGeneration;
            lifetimeToken = _lifetime.Token;
            if (_targets.IsEmpty)
            {
                ResetAutomaticRestartPolicyLocked();
                _bridgeStatus = "Waiting for one saved Rhino and Grasshopper file pair.";
                restartDelay = Timeout.InfiniteTimeSpan;
            }
            else if (!TryScheduleAutomaticRestartLocked(failureStatus, out restartDelay))
            {
                restartDelay = Timeout.InfiniteTimeSpan;
            }
        }

        StopDetachedRuntime(detached);
        if (restartDelay == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        _ = RestartRuntimeAfterDelayAsync(detachedGeneration, restartDelay, lifetimeToken);
    }

    private bool TryScheduleAutomaticRestartLocked(string failureStatus, out TimeSpan restartDelay)
    {
        if (!_automaticRestartPolicy.TryReserve(out restartDelay))
        {
            _automaticRestartPending = false;
            _bridgeStatus = $"{failureStatus} Automatic restart stopped after " +
                $"{AutomaticRestartPolicy.MaximumAttempts} retries; " +
                "close and reopen either project file to retry.";
            return false;
        }

        _automaticRestartPending = true;
        _bridgeStatus = $"{failureStatus} Restarting in {restartDelay.TotalSeconds:0} second(s) " +
            $"({_automaticRestartPolicy.AttemptCount}/{AutomaticRestartPolicy.MaximumAttempts}).";
        return true;
    }

    private async Task RestartRuntimeAfterDelayAsync(
        long detachedGeneration,
        TimeSpan restartDelay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(restartDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        DocumentTarget? replacement;
        try
        {
            lock (_gate)
            {
                if (_disposed ||
                    _runtimeGeneration != detachedGeneration ||
                    !_automaticRestartPending ||
                    _automaticRestartPolicy.IsSuppressed ||
                    _bootstrapper is not null)
                {
                    return;
                }

                replacement = _targets.Values
                    .OrderBy(target => target.StableTargetKey(), StringComparer.Ordinal)
                    .FirstOrDefault();
                if (replacement is null)
                {
                    ResetAutomaticRestartPolicyLocked();
                    _bridgeStatus = "Waiting for one saved Rhino and Grasshopper file pair.";
                    return;
                }
                // Keep the restart lease and target selection under the same re-entrant gate.
                // Concurrent document replacement therefore cannot consume the reservation or
                // leave a stable replacement target without a bootstrap attempt.
                EnsureBootstrap(replacement, reservedRestart: true);
                if (_bootstrapper is null)
                {
                    _automaticRestartPending = false;
                    _bridgeStatus = "AgentHost restart was deferred while documents were changing.";
                    return;
                }
            }
            QueueRegistration(replacement);
        }
        catch (ObjectDisposedException) when (IsDisposed())
        {
            return;
        }
        catch (Exception exception)
        {
            TimeSpan nextRestartDelay = Timeout.InfiniteTimeSpan;
            lock (_gate)
            {
                if (!_disposed &&
                    _runtimeGeneration == detachedGeneration &&
                    _bootstrapper is null &&
                    !_targets.IsEmpty)
                {
                    _ = TryScheduleAutomaticRestartLocked(
                        "Could not start AgentHost for this file pair.",
                        out nextRestartDelay);
                }
            }
            DevelopmentDiagnosticTrace.TryWriteException(
                "Rhino",
                "agent-bootstrap-restart-failed",
                exception);
            if (nextRestartDelay != Timeout.InfiniteTimeSpan)
            {
                _ = RestartRuntimeAfterDelayAsync(
                    detachedGeneration,
                    nextRestartDelay,
                    cancellationToken);
            }
        }
    }

    private void ResetAutomaticRestartPolicyLocked()
    {
        _automaticRestartPolicy.Reset();
        _automaticRestartPending = false;
    }

    private bool IsCurrentRuntimeLocked(AgentHostBootstrapper bootstrapper, long generation) =>
        !_disposed &&
        ReferenceEquals(_bootstrapper, bootstrapper) &&
        _runtimeGeneration == generation;

    private static async Task DisposeConnectionSafelyAsync(DocumentPipeConnection connection)
    {
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            DevelopmentDiagnosticTrace.TryWriteException(
                "Rhino",
                "document-bridge-dispose-failed",
                exception);
        }
    }

    /// <summary>
    /// Debounced Rhino selection push. Rubber-band selection fires many events per second, so
    /// only the settled selection is captured (on the UI thread) and sent over the pipe.
    /// Selection ids are a discovery hint for agent sessions, never concurrency control.
    /// </summary>
    public void NotifySelectionChanged(uint documentSerial)
    {
        if (documentSerial == 0)
        {
            return;
        }
        lock (_gate)
        {
            if (_disposed ||
                _connection is null ||
                !_targets.Values.Any(target => target.RhinoDocumentSerial == documentSerial))
            {
                return;
            }
            _pendingSelectionSerial = documentSerial;
            _selectionDebounceTimer ??= new Timer(
                _ => OnSelectionDebounceElapsed(),
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _selectionDebounceTimer.Change(SelectionDebounceInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnSelectionDebounceElapsed()
    {
        DocumentPipeConnection? connection;
        DocumentTarget? target;
        long generation;
        CancellationToken cancellationToken;
        lock (_gate)
        {
            if (_disposed || _connection is null)
            {
                return;
            }
            var serial = _pendingSelectionSerial;
            connection = _connection;
            generation = _runtimeGeneration;
            target = _targets.Values.FirstOrDefault(candidate => candidate.RhinoDocumentSerial == serial);
            cancellationToken = _lifetime.Token;
        }
        if (target is null)
        {
            return;
        }
        _ = SendSelectionChangedSafelyAsync(connection, target, generation, cancellationToken);
    }

    private async Task SendSelectionChangedSafelyAsync(
        DocumentPipeConnection connection,
        DocumentTarget target,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = await RhinoUiThreadDispatcher.InvokeAsync(
                () => Task.FromResult(CaptureSelection(target)),
                cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                return;
            }
            lock (_gate)
            {
                if (_disposed ||
                    _runtimeGeneration != generation ||
                    !ReferenceEquals(_connection, connection))
                {
                    return;
                }
            }
            await connection.SendAsync(
                BridgeFrame.Create(
                    BridgeMessageKind.Event,
                    BridgeMessageTypes.SelectionChanged,
                    payload,
                    target),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException
                or OperationCanceledException
                or ObjectDisposedException
                or InvalidOperationException)
        {
            // Selection context is best-effort; it must never disturb the bridge.
        }
    }

    private static SelectionChangedEvent? CaptureSelection(DocumentTarget target)
    {
        var document = global::Rhino.RhinoDoc.FromRuntimeSerialNumber(target.RhinoDocumentSerial);
        if (document is null)
        {
            return null;
        }
        var ids = new List<Guid>();
        foreach (var rhinoObject in document.Objects.GetSelectedObjects(
            includeLights: false,
            includeGrips: false))
        {
            ids.Add(rhinoObject.Id);
            if (ids.Count >= MaximumSelectionIds)
            {
                break;
            }
        }
        // Canvas selection is captured by the .gha watcher and read back from the hub here, so
        // one event carries the settled selection of BOTH documents in the bound pair.
        var grasshopperObjects = BridgeProcessHub.GetGrasshopperSelection(target.GrasshopperDocumentId);
        return new SelectionChangedEvent(
            ids,
            document.Layers.CurrentLayer?.FullPath,
            DateTimeOffset.UtcNow,
            grasshopperObjects.Count > 0 ? grasshopperObjects : null);
    }

    private async Task SendDocumentClosedSafelyAsync(
        DocumentPipeConnection connection,
        DocumentTarget target,
        string reason,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed ||
                    _runtimeGeneration != generation ||
                    !ReferenceEquals(_connection, connection) ||
                    _targets.TryGetValue(target.StableTargetKey(), out var currentTarget) &&
                    currentTarget.Generation >= target.Generation)
                {
                    return;
                }
            }
            await connection.SendAsync(
                BridgeFrame.Create(
                    BridgeMessageKind.Event,
                    BridgeMessageTypes.DocumentClosed,
                    new DocumentClosedEvent(reason, target.Generation),
                    target),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private async Task SendRegistrationSafelyAsync(
        DocumentPipeConnection connection,
        DocumentTarget target,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed ||
                    _runtimeGeneration != generation ||
                    !ReferenceEquals(_connection, connection) ||
                    !_targets.TryGetValue(target.StableTargetKey(), out var currentTarget) ||
                    !ReferenceEquals(currentTarget, target))
                {
                    return;
                }
            }
            await SendRegistrationAsync(connection, target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            lock (_gate)
            {
                if (!_disposed &&
                    _runtimeGeneration == generation &&
                    ReferenceEquals(_connection, connection))
                {
                    _bridgeStatus = "Could not register the document target; retrying on reconnect.";
                }
            }
            DevelopmentDiagnosticTrace.TryWriteException(
                "Rhino",
                "document-registration-failed",
                exception);
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
        DocumentTarget target;
        lock (_observationGate)
        {
            DevelopmentDiagnosticTrace.TryWrite(
                "Rhino",
                "pair-evaluated",
                $"rhino={_observedRhinoDocuments.Count};grasshopper={_observedGrasshopperDocuments.Count}");
            if (_observedRhinoDocuments.Count != 1 || _observedGrasshopperDocuments.Count != 1)
            {
                return;
            }

            var rhinoPair = _observedRhinoDocuments.Single();
            var grasshopperPair = _observedGrasshopperDocuments.Single();
            using var process = Process.GetCurrentProcess();
            var startedAt = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            target = DocumentRuntimeTarget.Create(
                CreateProjectId(process.Id, startedAt.UtcTicks, rhinoPair.Key, grasshopperPair.Key),
                process.Id,
                startedAt,
                rhinoPair.Key,
                grasshopperPair.Key,
                rhinoPair.Value,
                grasshopperPair.Value);
        }

        // Register outside _observationGate: rebinding may tear down the previous AgentHost
        // (up to a 2s process wait), which must not stall concurrent document observation.
        RegisterDocument(target);
    }

    // ProjectId identifies the LIVE document pair by its stable runtime identity — the Rhino process,
    // the RhinoDoc runtime serial, and the Grasshopper DocumentID — NOT by file paths. This makes it
    // invariant across a Save As / rename, so the AgentHost binding is not torn down when a path changes.
    // (It is a per-Rhino-session token; the persistent context folder is keyed separately by path.)
    private static Guid CreateProjectId(
        int rhinoProcessId,
        long rhinoProcessStartTicks,
        uint rhinoDocumentSerial,
        Guid grasshopperDocumentId)
    {
        var canonical = string.Join(
            '\n',
            rhinoProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            rhinoProcessStartTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            rhinoDocumentSerial.ToString(System.Globalization.CultureInfo.InvariantCulture),
            grasshopperDocumentId.ToString("D"));
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

    private sealed record DetachedRuntime(
        AgentHostBootstrapper? Bootstrapper,
        CancellationTokenSource? ConnectionLifetime,
        Task? BootstrapMonitorTask,
        Task? ConnectionTask);
}
