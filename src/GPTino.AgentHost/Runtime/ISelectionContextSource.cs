using GPTino.BridgeContract;

namespace GPTino.AgentHost.Runtime;

/// <summary>Compact cached-canvas summary used as a turn-context hint; never fingerprints.</summary>
public sealed record CanvasDigest(long Revision, int ComponentCount);

/// <summary>
/// Read-only ambient context for agent turns: the user's latest Rhino selection (pushed
/// by the plugin over the bridge pipe) and a cached canvas digest. Both are discovery
/// hints for turn context — never a substitute for snapshot fingerprints.
/// </summary>
public interface ISelectionContextSource
{
    SelectionChangedEvent? CurrentSelection { get; }

    CanvasDigest? CurrentCanvasDigest { get; }

    /// <summary>
    /// The selection of one Grasshopper document, routed by its durable docKey. Non-throwing:
    /// a null docKey resolves to the only registered document when exactly one is open, and an
    /// unknown key (or null among several documents) yields null. The defaults keep simple
    /// single-document sources (tests, fakes) working unchanged.
    /// </summary>
    SelectionChangedEvent? SelectionFor(string? docKey) => CurrentSelection;

    /// <summary>Per-document canvas digest with the same non-throwing resolution as <see cref="SelectionFor"/>.</summary>
    CanvasDigest? CanvasDigestFor(string? docKey) => CurrentCanvasDigest;
}
