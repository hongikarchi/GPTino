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
}
