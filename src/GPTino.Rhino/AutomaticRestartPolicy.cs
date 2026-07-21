namespace GPTino.Rhino;

/// <summary>
/// Bounds automatic AgentHost restarts across process generations. A file-pair lifecycle boundary
/// is the only event that replenishes the budget; elapsed failure time alone never does. The host
/// serializes every access under its runtime gate; this helper is intentionally not thread-safe.
/// </summary>
internal sealed class AutomaticRestartPolicy
{
    public const int MaximumAttempts = 3;

    public int AttemptCount { get; private set; }

    public bool IsSuppressed { get; private set; }

    public bool TryReserve(out TimeSpan delay)
    {
        if (IsSuppressed || AttemptCount >= MaximumAttempts)
        {
            IsSuppressed = true;
            delay = Timeout.InfiniteTimeSpan;
            return false;
        }

        AttemptCount++;
        delay = TimeSpan.FromSeconds(1 << (AttemptCount - 1));
        return true;
    }

    public void Reset()
    {
        AttemptCount = 0;
        IsSuppressed = false;
    }
}
