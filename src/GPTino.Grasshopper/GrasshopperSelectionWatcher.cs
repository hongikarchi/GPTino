using GPTino.BridgeContract;

namespace GPTino.Grasshopper;

/// <summary>
/// Grasshopper exposes no document-level selection event, so canvas selection is POLLED on the
/// Rhino UI thread via RhinoApp.Idle (canvas mutations also run there, so enumeration is safe)
/// and throttled. Changes are pushed through BridgeProcessHub, the only .gha ↔ .rhp seam, so the
/// runtime host can relay them without a plug-in load-order dependency. Selection is a discovery
/// hint only — it never participates in canvas fingerprints, so clicking around cannot disturb
/// optimistic concurrency.
/// </summary>
internal static class GrasshopperSelectionWatcher
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private const int MaximumSelectedObjects = 64;
    private const int MaximumNameCharacters = 80;

    private static readonly Dictionary<Guid, Guid[]> LastSelectedIds = new();
    private static DateTime _lastPollUtc;
    private static bool _subscribed;

    internal static void Start()
    {
        if (_subscribed)
        {
            return;
        }
        global::Rhino.RhinoApp.Idle += OnIdle;
        _subscribed = true;
        DevelopmentDiagnosticTrace.TryWrite("Grasshopper", "selection-watcher-started");
    }

    internal static void Stop()
    {
        if (!_subscribed)
        {
            return;
        }
        try
        {
            global::Rhino.RhinoApp.Idle -= OnIdle;
        }
        catch (Exception exception)
        {
            DevelopmentDiagnosticTrace.TryWriteException(
                "Grasshopper",
                "selection-watcher-stop-failed",
                exception);
        }
        _subscribed = false;
        LastSelectedIds.Clear();
    }

    private static void OnIdle(object? sender, EventArgs args)
    {
        var now = DateTime.UtcNow;
        if (now - _lastPollUtc < PollInterval)
        {
            return;
        }
        _lastPollUtc = now;
        try
        {
            Poll();
        }
        catch (Exception exception)
        {
            DevelopmentDiagnosticTrace.TryWriteException(
                "Grasshopper",
                "selection-poll-failed",
                exception);
        }
    }

    private static void Poll()
    {
        foreach (var (documentId, document) in GrasshopperDocumentCatalog.SnapshotLiveDocuments())
        {
            var selected = new List<GrasshopperSelectedObject>();
            foreach (var documentObject in document.Objects)
            {
                if (documentObject?.Attributes is not { Selected: true })
                {
                    continue;
                }
                selected.Add(new GrasshopperSelectedObject(
                    documentObject.InstanceGuid,
                    Cap(documentObject.Name),
                    Cap(documentObject.NickName)));
                if (selected.Count >= MaximumSelectedObjects)
                {
                    break;
                }
            }

            var ids = new Guid[selected.Count];
            for (var index = 0; index < selected.Count; index++)
            {
                ids[index] = selected[index].ObjectId;
            }
            if (LastSelectedIds.TryGetValue(documentId, out var previous) &&
                previous.AsSpan().SequenceEqual(ids))
            {
                continue;
            }
            LastSelectedIds[documentId] = ids;
            BridgeProcessHub.NotifyGrasshopperSelection(documentId, selected);
        }
    }

    private static string Cap(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        return value.Length <= MaximumNameCharacters
            ? value
            : value[..MaximumNameCharacters];
    }
}
