using System.Diagnostics;
using GPTino.BridgeContract;
using GPTino.CordycepsAdapter;

namespace GPTino.Rhino;

/// <summary>Resolves only the serial carried by the request and never consults RhinoDoc.ActiveDoc.</summary>
public sealed class ExplicitRhinoDocumentResolver : ICordycepsDocumentResolver<global::Rhino.RhinoDoc>
{
    public global::Rhino.RhinoDoc Resolve(DocumentTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        target.Validate();
        VerifyCurrentProcess(target);

        var document = global::Rhino.RhinoDoc.FromRuntimeSerialNumber(target.RhinoDocumentSerial)
            ?? throw new DocumentTargetUnavailableException(
                $"Rhino document serial {target.RhinoDocumentSerial} is not open.");
        if (document.RuntimeSerialNumber != target.RhinoDocumentSerial)
        {
            throw new DocumentTargetUnavailableException("Rhino returned a different document serial.");
        }

        // Identity is the runtime serial (verified above), not the file path. A Save As / rename changes
        // document.Path while this stays the same live document, so the path is mutable metadata, not a gate.
        return document;
    }

    private static void VerifyCurrentProcess(DocumentTarget target)
    {
        using var process = Process.GetCurrentProcess();
        var startTicks = process.StartTime.ToUniversalTime().Ticks;
        if (process.Id != target.RhinoProcessId || startTicks != target.RhinoProcessStartedAt.UtcTicks)
        {
            throw new DocumentTargetUnavailableException(
                $"Target {target.StableTargetKey()} belongs to a different Rhino process.");
        }
    }
}

public sealed class DocumentTargetUnavailableException : InvalidOperationException
{
    public DocumentTargetUnavailableException(string message)
        : base(message)
    {
    }
}
