namespace GPTino.Contracts;

/// <summary>
/// Identifies one explicit Rhino and Grasshopper document pair.
/// </summary>
public sealed record DocumentRuntime(
    Guid ProjectId,
    int RhinoProcessId,
    DateTimeOffset RhinoProcessStartedAt,
    uint RhinoDocumentSerial,
    Guid GrasshopperDocumentId,
    string RhinoPath,
    string GrasshopperPath,
    long Generation)
{
    public string Identity => FormattableString.Invariant(
        $"{ProjectId:N}:{RhinoProcessId}:{RhinoProcessStartedAt.UtcTicks}:{RhinoDocumentSerial}:{GrasshopperDocumentId:N}:{Generation}");
}
