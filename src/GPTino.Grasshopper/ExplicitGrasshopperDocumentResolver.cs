using System.Diagnostics;
using GPTino.BridgeContract;
using GPTino.CordycepsAdapter;
using GPTino.WireifyAdapter;
using Grasshopper.Kernel;

namespace GPTino.Grasshopper;

/// <summary>
/// Resolves a GH_Document exclusively by the target's DocumentID and verifies its Rhino pair.
/// It never falls back to Instances.ActiveCanvas or Instances.ActiveDocument for an operation.
/// </summary>
public sealed class ExplicitGrasshopperDocumentResolver :
    IWireifyDocumentResolver<GH_Document>,
    ICordycepsDocumentResolver<GH_Document>
{
    public GH_Document Resolve(DocumentTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Validate();
        VerifyCurrentProcess(target);

        if (!GrasshopperDocumentCatalog.TryResolve(target.GrasshopperDocumentId, out var document))
        {
            throw new GrasshopperDocumentUnavailableException(
                $"Grasshopper document {target.GrasshopperDocumentId:D} is not registered.");
        }

        if (!document.IsFilePathDefined || !PathsEqual(document.FilePath, target.GrasshopperPath))
        {
            throw new GrasshopperDocumentUnavailableException(
                $"Grasshopper path does not match target {target.StableTargetKey()}.");
        }

        var rhinoDocument = global::Rhino.RhinoDoc.FromRuntimeSerialNumber(target.RhinoDocumentSerial)
            ?? throw new GrasshopperDocumentUnavailableException(
                $"Paired Rhino document {target.RhinoDocumentSerial} is not open.");
        if (!PathsEqual(rhinoDocument.Path, target.RhinoPath))
        {
            throw new GrasshopperDocumentUnavailableException(
                $"Paired Rhino path does not match target {target.StableTargetKey()}.");
        }

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

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }

        return string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class GrasshopperDocumentUnavailableException : InvalidOperationException
{
    public GrasshopperDocumentUnavailableException(string message)
        : base(message)
    {
    }
}
