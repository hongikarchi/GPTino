namespace GPTino.Contracts;

/// <summary>
/// Identifies one explicit Rhino document, optionally paired with a Grasshopper document.
/// The Grasshopper half is nullable because the curator works on the Rhino document alone: a
/// saved Rhino file with no .gh open is a complete, usable runtime, not a half-formed one. Canvas
/// operations resolve the Grasshopper document per call and fail honestly when there is none.
/// </summary>
public sealed record DocumentRuntime(
    Guid ProjectId,
    int RhinoProcessId,
    DateTimeOffset RhinoProcessStartedAt,
    uint RhinoDocumentSerial,
    Guid? GrasshopperDocumentId,
    string RhinoPath,
    string? GrasshopperPath,
    long Generation)
{
    /// <summary>True when this target carries a Grasshopper document (canvas work is possible).</summary>
    public bool HasGrasshopper => GrasshopperDocumentId is not null;

    public string Identity => FormattableString.Invariant(
        $"{ProjectId:N}:{RhinoProcessId}:{RhinoProcessStartedAt.UtcTicks}:{RhinoDocumentSerial}:{GrasshopperIdentity}:{Generation}");

    /// <summary>The Grasshopper half of an identity string; "none" keeps a Rhino-only target distinct.</summary>
    internal string GrasshopperIdentity =>
        GrasshopperDocumentId is { } id ? id.ToString("N") : "none";
}
