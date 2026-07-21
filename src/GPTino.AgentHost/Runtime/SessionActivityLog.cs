namespace GPTino.AgentHost.Runtime;

public sealed record SessionActivityEntry(
    DateTimeOffset At,
    string Kind,
    string Summary,
    bool Ok,
    long DurationMs);

/// <summary>
/// Bounded in-memory feed of what each session is actually doing — tool calls and turn
/// milestones — so the panel can show live activity per node instead of only final
/// answers. Deliberately ephemeral: history that matters is in messages and jobs.
/// </summary>
public sealed class SessionActivityLog
{
    private const int MaximumEntriesPerSession = 40;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Queue<SessionActivityEntry>> _entries = [];
    private readonly EventHub _events;

    public SessionActivityLog(EventHub events)
    {
        _events = events;
    }

    public void Record(Guid sessionId, string kind, string summary, bool ok, long durationMs)
    {
        var entry = new SessionActivityEntry(DateTimeOffset.UtcNow, kind, Truncate(summary), ok, durationMs);
        lock (_gate)
        {
            if (!_entries.TryGetValue(sessionId, out var queue))
            {
                queue = new Queue<SessionActivityEntry>();
                _entries[sessionId] = queue;
            }
            queue.Enqueue(entry);
            while (queue.Count > MaximumEntriesPerSession)
            {
                queue.Dequeue();
            }
        }
        _events.Publish();
    }

    public IReadOnlyList<SessionActivityEntry> Read(Guid sessionId, int limit = 12)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(sessionId, out var queue) || queue.Count == 0)
            {
                return [];
            }
            return queue.Skip(Math.Max(0, queue.Count - limit)).ToArray();
        }
    }

    public SessionActivityEntry? Latest(Guid sessionId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(sessionId, out var queue) && queue.Count > 0
                ? queue.Last()
                : null;
        }
    }

    private static string Truncate(string value) =>
        value.Length > 160 ? $"{value[..159]}…" : value;
}
