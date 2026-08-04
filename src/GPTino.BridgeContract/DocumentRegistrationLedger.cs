using System.Collections.Concurrent;
using GPTino.Contracts;

namespace GPTino.BridgeContract;

/// <summary>
/// Which document targets the AgentHost has CONFIRMED it registered, keyed by stable target key.
///
/// Registration used to be fire-and-forget: the plugin sent a frame and never learned whether it
/// arrived. That was survivable only because every target existed before the AgentHost was even
/// spawned, so the replay at connect covered them all. The moment a target can appear LATER — a
/// Grasshopper file opened mid-session, a Save As rebind, a curator-only runtime that a definition
/// joins afterwards — a single silent drop stranded it forever, and a live gate spent hours on a
/// bridge that reported "connected" while one target had simply never registered.
///
/// Confirmation is per (target key, generation): a Save As re-registers the same live document at a
/// higher generation, and the old confirmation must not be mistaken for the new one.
///
/// Deliberately has no timer. The plugin already knows when documents appear — it raises the
/// observation events itself — so this only needs to answer "is this one confirmed?" and let the
/// caller retry against a real reply. A heartbeat would burn power to rediscover what the events
/// already said.
/// </summary>
public sealed class DocumentRegistrationLedger
{
    private readonly ConcurrentDictionary<string, long> _confirmed = new(StringComparer.Ordinal);

    /// <summary>Records the AgentHost's acknowledgement of one registration.</summary>
    public void Confirm(string targetKey, long generation)
    {
        if (string.IsNullOrWhiteSpace(targetKey))
        {
            return;
        }
        // A late acknowledgement for an older generation must not overwrite a newer one.
        _confirmed.AddOrUpdate(
            targetKey,
            generation,
            (_, current) => Math.Max(current, generation));
    }

    /// <summary>Forgets one target — it closed, so a future registration must be sent again.</summary>
    public void Forget(string targetKey) => _confirmed.TryRemove(targetKey, out _);

    /// <summary>
    /// Forgets everything. Called when the bridge connection drops: the next AgentHost process knows
    /// nothing about this plugin's targets, so treating them as confirmed would strand every one.
    /// </summary>
    public void Clear() => _confirmed.Clear();

    /// <summary>True when this exact target and generation has been acknowledged.</summary>
    public bool IsConfirmed(DocumentRuntime target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return _confirmed.TryGetValue(target.StableTargetKey(), out var generation) &&
            generation >= target.Generation;
    }

    /// <summary>
    /// The targets that still need a registration frame, in the caller's order. Pure over the
    /// ledger's state, so the "what should be sent" decision is testable without a bridge.
    /// </summary>
    public IReadOnlyList<DocumentRuntime> Outstanding(IEnumerable<DocumentRuntime> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return targets.Where(target => !IsConfirmed(target)).ToArray();
    }
}
