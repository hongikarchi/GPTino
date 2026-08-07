using System.Diagnostics;
using GPTino.BridgeContract;
using GPTino.CanvasSceneAdapter;
using GPTino.ScriptAdapter;
using Grasshopper.Kernel;

namespace GPTino.Grasshopper;

/// <summary>
/// Resolves a GH_Document exclusively by the target's DocumentID and verifies its Rhino pair.
/// It never falls back to Instances.ActiveCanvas or Instances.ActiveDocument for an operation.
/// </summary>
public sealed class ExplicitGrasshopperDocumentResolver :
    IScriptDocumentResolver<GH_Document>,
    ICanvasSceneDocumentResolver<GH_Document>
{
    public GH_Document Resolve(DocumentTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Validate();
        VerifyCurrentProcess(target);

        // A Rhino-only target is a legitimate runtime (document work needs no canvas), so this is a
        // plain unavailability, not a corrupt target: canvas ops refuse, Rhino ops carry on.
        if (target.GrasshopperDocumentId is not { } documentId)
        {
            throw new GrasshopperDocumentUnavailableException(
                "No Grasshopper document is open for this Rhino document. Open a Grasshopper " +
                "definition to work on the canvas.");
        }

        if (!GrasshopperDocumentCatalog.TryResolve(documentId, out var document))
        {
            throw new GrasshopperDocumentUnavailableException(
                $"Grasshopper document {documentId:D} is not registered.");
        }

        // Identity is the GH DocumentID (resolved above) plus the paired RhinoDoc serial — not the file
        // paths. A Save As changes FilePath while the DocumentID is unchanged, so paths are mutable metadata.
        // Still require the paired Rhino document to be open as a liveness invariant.
        _ = global::Rhino.RhinoDoc.FromRuntimeSerialNumber(target.RhinoDocumentSerial)
            ?? throw new GrasshopperDocumentUnavailableException(
                $"Paired Rhino document {target.RhinoDocumentSerial} is not open.");

        return document;
    }

    private static void VerifyCurrentProcess(DocumentTarget target)
    {
        using var process = Process.GetCurrentProcess();
        var startTicks = process.StartTime.ToUniversalTime().Ticks;
        if (process.Id != target.RhinoProcessId || startTicks != target.RhinoProcessStartedAt.UtcTicks)
        {
            throw new GrasshopperDocumentUnavailableException(
                $"Target {target.StableTargetKey()} belongs to a different Rhino process.");
        }
    }
}

public sealed class GrasshopperDocumentUnavailableException : InvalidOperationException
{
    public GrasshopperDocumentUnavailableException(string message)
        : base(message)
    {
    }
}
