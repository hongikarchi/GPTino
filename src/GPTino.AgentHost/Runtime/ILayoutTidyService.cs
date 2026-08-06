using GPTino.AgentHost.Api;

namespace GPTino.AgentHost.Runtime;

/// <summary>
/// Server-owned automatic canvas tidy. The orchestrator marks turn boundaries; when a turn that created
/// Grasshopper components finishes, it asks the backend to lay out the affected dataflow cluster(s) using
/// the real component bounds — the same deterministic layered layout as the <c>arrange_layout</c> tool.
/// This removes the dependence on the model remembering to call <c>arrange_layout</c> as its last step.
/// </summary>
public interface ILayoutTidyService
{
    /// <summary>Resets the per-session "created this turn" accumulator at the start of a turn.</summary>
    void BeginTurn(Guid sessionId);

    /// <summary>
    /// If the just-finished turn created any canvas objects, tidies the cluster(s) they belong to and
    /// returns the number of seed components tidied (0 when nothing was created or the cluster was already
    /// clean). Best effort: it never throws into the turn-completion path.
    /// </summary>
    Task<int> TidyTurnCreationsAsync(SessionRecord session, CancellationToken cancellationToken);
}
