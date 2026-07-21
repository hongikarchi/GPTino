using GPTino.BridgeContract;

namespace GPTino.AgentHost.Runtime;

/// <summary>
/// Read-only source of the user's latest Rhino selection, pushed by the plugin over the
/// bridge pipe. Selection ids are a discovery hint for turn context — never fingerprints.
/// </summary>
public interface ISelectionContextSource
{
    SelectionChangedEvent? CurrentSelection { get; }
}
