using System.Collections.Concurrent;

namespace GPTino.BridgeContract;

/// <summary>
/// In-process rendezvous between the separately loaded .rhp and .gha assemblies. It contains no
/// Rhino or Grasshopper types and therefore does not create a plug-in load-order dependency.
/// </summary>
public static class BridgeProcessHub
{
    private static readonly ConcurrentDictionary<Guid, string> GrasshopperDocuments = new();
    private static readonly ConcurrentDictionary<BridgeAdapterOwner, IBridgeOperationHandler> Handlers = new();

    public static event Action<Guid, string>? GrasshopperDocumentObserved;

    public static event Action<Guid>? GrasshopperDocumentForgotten;

    public static event Action<IBridgeOperationHandler>? OperationHandlerRegistered;

    public static IReadOnlyDictionary<Guid, string> GetGrasshopperDocuments() =>
        new Dictionary<Guid, string>(GrasshopperDocuments);

    public static IReadOnlyCollection<IBridgeOperationHandler> GetOperationHandlers() =>
        Handlers.Values.ToArray();

    public static void ObserveGrasshopperDocument(Guid documentId, string filePath)
    {
        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("Document ID is required.", nameof(documentId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!Path.IsPathFullyQualified(filePath))
        {
            throw new ArgumentException("Grasshopper path must be fully qualified.", nameof(filePath));
        }

        var normalized = Path.GetFullPath(filePath);
        GrasshopperDocuments[documentId] = normalized;
        GrasshopperDocumentObserved?.Invoke(documentId, normalized);
    }

    public static void ForgetGrasshopperDocument(Guid documentId)
    {
        if (documentId != Guid.Empty && GrasshopperDocuments.TryRemove(documentId, out _))
        {
            GrasshopperDocumentForgotten?.Invoke(documentId);
        }
    }

    public static void RegisterOperationHandler(IBridgeOperationHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Handlers[handler.Owner] = handler;
        OperationHandlerRegistered?.Invoke(handler);
    }
}
