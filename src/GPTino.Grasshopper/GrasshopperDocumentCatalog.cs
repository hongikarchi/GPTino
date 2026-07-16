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
    private static readonly object Gate = new();
    private static readonly HashSet<GH_Canvas> AttachedCanvases = new();
    private static bool _initialized;

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            global::Grasshopper.Instances.CanvasCreated += OnCanvasCreated;
            global::Grasshopper.Instances.CanvasDestroyed += OnCanvasDestroyed;
            if (global::Grasshopper.Instances.ActiveCanvas is { } canvas)
            {
                AttachCanvas(canvas);
            }
        }
    }

    public static bool TryResolve(Guid documentId, out GH_Document document)
    {
        if (documentId == Guid.Empty)
        {
            throw new ArgumentException("Document ID is required.", nameof(documentId));
        }

        if (Documents.TryGetValue(documentId, out var reference) &&
            reference.TryGetTarget(out document!) &&
            document.DocumentID == documentId)
        {
            return true;
        }

        Documents.TryRemove(documentId, out _);
        document = null!;
        return false;
    }

    internal static void Register(GH_Document? document, bool observe = true)
    {
        if (document is null || document.DocumentID == Guid.Empty)
        {
            return;
        }

        Documents[document.DocumentID] = new WeakReference<GH_Document>(document);
        if (observe && document.IsFilePathDefined)
        {
            BridgeProcessHub.ObserveGrasshopperDocument(document.DocumentID, document.FilePath);
        }
    }

    private static void OnCanvasCreated(GH_Canvas canvas)
    {
        lock (Gate)
        {
            AttachCanvas(canvas);
        }
    }

    private static void OnCanvasDestroyed(GH_Canvas canvas)
    {
        lock (Gate)
        {
            if (AttachedCanvases.Remove(canvas))
            {
                canvas.DocumentChanged -= OnDocumentChanged;
                if (canvas.Document is { } document)
                {
                    BridgeProcessHub.ForgetGrasshopperDocument(document.DocumentID);
                }
            }
        }
    }

    private static void AttachCanvas(GH_Canvas canvas)
    {
        if (!AttachedCanvases.Add(canvas))
        {
            return;
        }

        canvas.DocumentChanged += OnDocumentChanged;
        Register(canvas.Document);
    }

    private static void OnDocumentChanged(
        GH_Canvas sender,
        GH_CanvasDocumentChangedEventArgs args)
    {
        Register(args.OldDocument, observe: false);
        if (args.OldDocument is { } oldDocument)
        {
            BridgeProcessHub.ForgetGrasshopperDocument(oldDocument.DocumentID);
        }

        Register(args.NewDocument);
    }
}
