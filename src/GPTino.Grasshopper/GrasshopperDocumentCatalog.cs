using System.Collections.Concurrent;
using GPTino.BridgeContract;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;

namespace GPTino.Grasshopper;

/// <summary>
/// Discovers documents, but never selects one for an operation. Callers must resolve by DocumentID.
/// </summary>
public static class GrasshopperDocumentCatalog
{
    private static readonly ConcurrentDictionary<Guid, WeakReference<GH_Document>> Documents = new();
    private static readonly ConcurrentQueue<MutationWork> MutationQueue = new();
    private static readonly HashSet<GH_Canvas> AttachedCanvases = new();
    private static readonly HashSet<GH_Document> AttachedDocuments =
        new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<Guid, string> ObservedPaths = new();
    private static GH_DocumentServer? _documentServer;
    private static bool _documentAddedSubscribed;
    private static bool _documentRemovedSubscribed;
    private static bool _canvasCreatedSubscribed;
    private static bool _canvasDestroyedSubscribed;
    private static bool _rhinoClosingSubscribed;
    private static int _isDrainingMutations;
    private static int _mutationThreadId;
    private static int _acceptingResolutions;
    private static CatalogState _state;

    public static void Initialize() => ExecuteMutation(InitializeCore);

    internal static void Teardown() => ExecuteMutation(TeardownAndReport);

    public static bool TryResolve(Guid documentId, out GH_Document document)
    {
        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("Document ID is required.", nameof(documentId));
        }
        if (Volatile.Read(ref _acceptingResolutions) == 0)
        {
            document = null!;
            return false;
        }

        if (Documents.TryGetValue(documentId, out var reference) &&
            reference.TryGetTarget(out document!) &&
            document.DocumentID == documentId &&
            Documents.TryGetValue(documentId, out var currentReference) &&
            ReferenceEquals(reference, currentReference) &&
            Volatile.Read(ref _acceptingResolutions) != 0)
        {
            return true;
        }

        if (reference is not null)
        {
            ((ICollection<KeyValuePair<Guid, WeakReference<GH_Document>>>)Documents)
                .Remove(new KeyValuePair<Guid, WeakReference<GH_Document>>(documentId, reference));
        }
        document = null!;
        return false;
    }

    /// <summary>
    /// Live (id, document) pairs for read-only inspection such as selection polling. Returns
    /// empty once the catalog stops accepting resolutions, so pollers quiesce on teardown.
    /// </summary>
    internal static IReadOnlyList<KeyValuePair<Guid, GH_Document>> SnapshotLiveDocuments()
    {
        if (Volatile.Read(ref _acceptingResolutions) == 0)
        {
            return Array.Empty<KeyValuePair<Guid, GH_Document>>();
        }

        var live = new List<KeyValuePair<Guid, GH_Document>>();
        foreach (var entry in Documents)
        {
            if (entry.Value.TryGetTarget(out var document) && document.DocumentID == entry.Key)
            {
                live.Add(new KeyValuePair<Guid, GH_Document>(entry.Key, document));
            }
        }
        return live;
    }

    internal static void Register(GH_Document? document) =>
        EnqueueMutation(() =>
        {
            if (IsAcceptingEvents())
            {
                RegisterCore(document);
            }
        });

    private static void InitializeCore()
    {
        if (_state == CatalogState.Running)
        {
            return;
        }
        if (_state is CatalogState.Starting or CatalogState.Stopping)
        {
            throw new InvalidOperationException(
                "The Grasshopper document catalog is already changing lifecycle state.");
        }

        _state = CatalogState.Starting;
        try
        {
            SubscribeCore();
            var documentServer = _documentServer
                ?? throw new InvalidOperationException(
                    "The Grasshopper document server is unavailable.");
            var documents = documentServer.Cast<GH_Document>().ToArray();
            var activeCanvas = global::Grasshopper.Instances.ActiveCanvas;

            foreach (var document in documents)
            {
                if (documentServer.Contains(document))
                {
                    RegisterCore(document);
                }
            }
            if (activeCanvas is not null)
            {
                AttachCanvasCore(activeCanvas);
            }

            _state = CatalogState.Running;
            Volatile.Write(ref _acceptingResolutions, 1);
            DevelopmentDiagnosticTrace.TryWrite(
                "Grasshopper",
                "document-catalog-ready",
                $"documents={documentServer.DocumentCount};activeCanvas={activeCanvas is not null}");
        }
        catch
        {
            List<Exception> cleanupFailures = [];
            TeardownCore(cleanupFailures);
            WriteCleanupFailures(cleanupFailures);
            throw;
        }
    }

    private static void SubscribeCore()
    {
        _documentServer ??= global::Grasshopper.Instances.DocumentServer;
        if (!_documentAddedSubscribed)
        {
            _documentServer.DocumentAdded += OnDocumentAdded;
            _documentAddedSubscribed = true;
        }
        if (!_documentRemovedSubscribed)
        {
            _documentServer.DocumentRemoved += OnDocumentRemoved;
            _documentRemovedSubscribed = true;
        }
        if (!_canvasCreatedSubscribed)
        {
            global::Grasshopper.Instances.CanvasCreated += OnCanvasCreated;
            _canvasCreatedSubscribed = true;
        }
        if (!_canvasDestroyedSubscribed)
        {
            global::Grasshopper.Instances.CanvasDestroyed += OnCanvasDestroyed;
            _canvasDestroyedSubscribed = true;
        }
        if (!_rhinoClosingSubscribed)
        {
            global::Rhino.RhinoApp.Closing += OnRhinoClosing;
            _rhinoClosingSubscribed = true;
        }
    }

    private static void TeardownAndReport()
    {
        List<Exception> cleanupFailures = [];
        TeardownCore(cleanupFailures);
        WriteCleanupFailures(cleanupFailures);
    }

    private static void TeardownCore(List<Exception> cleanupFailures)
    {
        Volatile.Write(ref _acceptingResolutions, 0);
        _state = CatalogState.Stopping;

        TryCleanup(
            () =>
            {
                if (_documentServer is not null)
                {
                    _documentServer.DocumentAdded -= OnDocumentAdded;
                }
            },
            () => _documentAddedSubscribed = false,
            _documentAddedSubscribed,
            cleanupFailures);
        TryCleanup(
            () =>
            {
                if (_documentServer is not null)
                {
                    _documentServer.DocumentRemoved -= OnDocumentRemoved;
                }
            },
            () => _documentRemovedSubscribed = false,
            _documentRemovedSubscribed,
            cleanupFailures);
        TryCleanup(
            () => global::Grasshopper.Instances.CanvasCreated -= OnCanvasCreated,
            () => _canvasCreatedSubscribed = false,
            _canvasCreatedSubscribed,
            cleanupFailures);
        TryCleanup(
            () => global::Grasshopper.Instances.CanvasDestroyed -= OnCanvasDestroyed,
            () => _canvasDestroyedSubscribed = false,
            _canvasDestroyedSubscribed,
            cleanupFailures);
        TryCleanup(
            () => global::Rhino.RhinoApp.Closing -= OnRhinoClosing,
            () => _rhinoClosingSubscribed = false,
            _rhinoClosingSubscribed,
            cleanupFailures);

        foreach (var canvas in AttachedCanvases.ToArray())
        {
            TryCleanup(
                () => canvas.DocumentChanged -= OnDocumentChanged,
                () => AttachedCanvases.Remove(canvas),
                enabled: true,
                cleanupFailures);
        }
        foreach (var document in AttachedDocuments.ToArray())
        {
            TryCleanup(
                () => document.FilePathChanged -= OnFilePathChanged,
                () => AttachedDocuments.Remove(document),
                enabled: true,
                cleanupFailures);
        }

        var observedDocumentIds = ObservedPaths.Keys.ToArray();
        ObservedPaths.Clear();
        Documents.Clear();
        foreach (var documentId in observedDocumentIds)
        {
            TryCleanup(
                () => BridgeProcessHub.ForgetGrasshopperDocument(documentId),
                onSuccess: null,
                enabled: true,
                cleanupFailures);
        }

        if (!_documentAddedSubscribed &&
            !_documentRemovedSubscribed &&
            !_canvasCreatedSubscribed &&
            !_canvasDestroyedSubscribed &&
            !_rhinoClosingSubscribed)
        {
            _documentServer = null;
        }
        _state = CatalogState.Stopped;
    }

    private static void TryCleanup(
        Action action,
        Action? onSuccess,
        bool enabled,
        List<Exception> failures)
    {
        if (!enabled)
        {
            return;
        }

        try
        {
            action();
            onSuccess?.Invoke();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void WriteCleanupFailures(IEnumerable<Exception> failures)
    {
        foreach (var exception in failures)
        {
            DevelopmentDiagnosticTrace.TryWriteException(
                "Grasshopper",
                "document-catalog-teardown-failed",
                exception);
        }
    }

    private static void RegisterCore(GH_Document? document)
    {
        if (document is null ||
            document.DocumentID == Guid.Empty ||
            !IsAcceptingEvents() ||
            !IsDocumentLiveCore(document))
        {
            return;
        }

        var documentId = document.DocumentID;
        var path = ResolveDocumentPath(document);
        if (Documents.TryGetValue(documentId, out var existingReference) &&
            existingReference.TryGetTarget(out var existingDocument) &&
            !ReferenceEquals(existingDocument, document) &&
            AttachedDocuments.Contains(existingDocument))
        {
            existingDocument.FilePathChanged -= OnFilePathChanged;
            AttachedDocuments.Remove(existingDocument);
        }

        if (AttachedDocuments.Add(document))
        {
            try
            {
                document.FilePathChanged += OnFilePathChanged;
            }
            catch
            {
                AttachedDocuments.Remove(document);
                throw;
            }
        }
        Documents[documentId] = new WeakReference<GH_Document>(document);

        if (path is null)
        {
            if (ObservedPaths.Remove(documentId))
            {
                BridgeProcessHub.ForgetGrasshopperDocument(documentId);
            }
            return;
        }
        if (ObservedPaths.TryGetValue(documentId, out var observedPath) &&
            string.Equals(observedPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ObservedPaths[documentId] = path;
        BridgeProcessHub.ObserveGrasshopperDocument(documentId, path);
    }

    private static string? ResolveDocumentPath(GH_Document document)
    {
        var path = document.FilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : null;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static void OnCanvasCreated(GH_Canvas canvas) =>
        EnqueueMutation(() =>
        {
            if (IsAcceptingEvents())
            {
                AttachCanvasCore(canvas);
            }
        });

    private static void OnRhinoClosing(object? sender, EventArgs args)
    {
        if (Volatile.Read(ref _mutationThreadId) == Environment.CurrentManagedThreadId)
        {
            TeardownAndReport();
            return;
        }

        ExecuteMutation(TeardownAndReport);
    }

    private static void OnDocumentAdded(GH_DocumentServer sender, GH_Document document) =>
        EnqueueMutation(() =>
        {
            if (!IsAcceptingEvents())
            {
                return;
            }
            RegisterCore(document);
            DevelopmentDiagnosticTrace.TryWrite(
                "Grasshopper",
                "document-added",
                $"id={document.DocumentID:D};saved={!string.IsNullOrWhiteSpace(document.FilePath)}");
        });

    private static void OnDocumentRemoved(GH_DocumentServer sender, GH_Document document) =>
        EnqueueMutation(() =>
        {
            if (!IsAcceptingEvents())
            {
                return;
            }
            UnregisterIfUnusedCore(document);
            DevelopmentDiagnosticTrace.TryWrite(
                "Grasshopper",
                "document-removed",
                $"id={document.DocumentID:D}");
        });

    private static void OnCanvasDestroyed(GH_Canvas canvas) =>
        EnqueueMutation(() =>
        {
            if (!IsAcceptingEvents() || !AttachedCanvases.Remove(canvas))
            {
                return;
            }

            try
            {
                canvas.DocumentChanged -= OnDocumentChanged;
            }
            catch
            {
                AttachedCanvases.Add(canvas);
                throw;
            }
            UnregisterIfUnusedCore(canvas.Document);
        });

    private static void AttachCanvasCore(GH_Canvas canvas)
    {
        if (!AttachedCanvases.Add(canvas))
        {
            return;
        }

        try
        {
            canvas.DocumentChanged += OnDocumentChanged;
        }
        catch
        {
            AttachedCanvases.Remove(canvas);
            throw;
        }
        RegisterCore(canvas.Document);
    }

    private static void OnDocumentChanged(
        GH_Canvas sender,
        GH_CanvasDocumentChangedEventArgs args) =>
        EnqueueMutation(() =>
        {
            if (!IsAcceptingEvents())
            {
                return;
            }
            UnregisterIfUnusedCore(args.OldDocument, sender);
            RegisterCore(args.NewDocument);
        });

    private static void OnFilePathChanged(object sender, GH_DocFilePathEventArgs args) =>
        EnqueueMutation(() =>
        {
            if (IsAcceptingEvents())
            {
                RegisterCore(args.Document ?? sender as GH_Document);
            }
        });

    private static void UnregisterIfUnusedCore(
        GH_Document? document,
        GH_Canvas? changingCanvas = null)
    {
        if (document is null || document.DocumentID == Guid.Empty)
        {
            return;
        }
        if (AttachedCanvases.Any(canvas =>
                !ReferenceEquals(canvas, changingCanvas) &&
                ReferenceEquals(canvas.Document, document)))
        {
            return;
        }
        if (_documentServer?.Contains(document) == true)
        {
            return;
        }

        UnregisterCore(document);
    }

    private static bool IsDocumentLiveCore(GH_Document document) =>
        _documentServer?.Contains(document) == true ||
        AttachedCanvases.Any(canvas => ReferenceEquals(canvas.Document, document));

    private static void UnregisterCore(GH_Document document)
    {
        var documentId = document.DocumentID;
        var removed = false;
        if (Documents.TryGetValue(documentId, out var reference) &&
            reference.TryGetTarget(out var registeredDocument) &&
            ReferenceEquals(registeredDocument, document))
        {
            removed = Documents.TryRemove(documentId, out _);
        }
        if (AttachedDocuments.Remove(document))
        {
            try
            {
                document.FilePathChanged -= OnFilePathChanged;
            }
            catch
            {
                AttachedDocuments.Add(document);
                throw;
            }
            removed = true;
        }
        if (removed && ObservedPaths.Remove(documentId))
        {
            BridgeProcessHub.ForgetGrasshopperDocument(documentId);
        }
    }

    private static void ExecuteMutation(Action action)
    {
        if (Volatile.Read(ref _mutationThreadId) == Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException(
                "A synchronous Grasshopper catalog lifecycle call cannot be re-entered.");
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        MutationQueue.Enqueue(new MutationWork(action, completion));
        DrainMutations();
        completion.Task.GetAwaiter().GetResult();
    }

    private static void EnqueueMutation(Action action)
    {
        MutationQueue.Enqueue(new MutationWork(action, Completion: null));
        DrainMutations();
    }

    private static void DrainMutations()
    {
        if (Interlocked.CompareExchange(ref _isDrainingMutations, 1, 0) != 0)
        {
            return;
        }

        while (true)
        {
            Volatile.Write(ref _mutationThreadId, Environment.CurrentManagedThreadId);
            while (MutationQueue.TryDequeue(out var work))
            {
                try
                {
                    work.Action();
                    work.Completion?.SetResult();
                }
                catch (Exception exception)
                {
                    if (work.Completion is not null)
                    {
                        work.Completion.SetException(exception);
                    }
                    else
                    {
                        DevelopmentDiagnosticTrace.TryWriteException(
                            "Grasshopper",
                            "document-catalog-mutation-failed",
                            exception);
                    }
                }
            }
            Volatile.Write(ref _mutationThreadId, 0);
            Interlocked.Exchange(ref _isDrainingMutations, 0);
            if (MutationQueue.IsEmpty ||
                Interlocked.CompareExchange(ref _isDrainingMutations, 1, 0) != 0)
            {
                return;
            }
        }
    }

    private static bool IsAcceptingEvents() =>
        _state is CatalogState.Starting or CatalogState.Running;

    private sealed record MutationWork(Action Action, TaskCompletionSource? Completion);

    private enum CatalogState
    {
        Stopped,
        Starting,
        Running,
        Stopping,
    }
}
