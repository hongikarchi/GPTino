using GPTino.Contracts;

namespace GPTino.Core;

/// <summary>
/// Stores the user-authored session order with compare-and-swap semantics.
/// </summary>
public sealed class SessionOrderBook
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];

    public SessionOrderSnapshot Initialize(Guid projectId, IEnumerable<Guid> sessionIds)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);
        var ids = sessionIds.ToArray();
        EnsureDistinct(ids, nameof(sessionIds));

        lock (_gate)
        {
            if (_entries.TryGetValue(projectId, out var existing))
            {
                return Snapshot(projectId, existing);
            }

            var entry = new Entry([.. ids], 0);
            _entries.Add(projectId, entry);
            return Snapshot(projectId, entry);
        }
    }

    public SessionOrderSnapshot? Read(Guid projectId)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(projectId, out var entry)
                ? Snapshot(projectId, entry)
                : null;
        }
    }

    public SessionOrderChangeResult TryReorder(SessionOrderChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var requested = change.OrderedSessionIds.ToArray();

        lock (_gate)
        {
            if (!_entries.TryGetValue(change.ProjectId, out var entry))
            {
                return new(SessionOrderChangeStatus.ProjectNotFound, null, "The project has no session order.");
            }

            if (entry.Version != change.ExpectedVersion)
            {
                return new(
                    SessionOrderChangeStatus.VersionMismatch,
                    Snapshot(change.ProjectId, entry),
                    $"Expected version {change.ExpectedVersion}, but current version is {entry.Version}.");
            }

            if (requested.Distinct().Count() != requested.Length)
            {
                return new(
                    SessionOrderChangeStatus.DuplicateSession,
                    Snapshot(change.ProjectId, entry),
                    "A session can occur only once in the order.");
            }

            if (requested.Length != entry.SessionIds.Count ||
                !requested.ToHashSet().SetEquals(entry.SessionIds))
            {
                return new(
                    SessionOrderChangeStatus.InvalidMembership,
                    Snapshot(change.ProjectId, entry),
                    "A reorder must contain exactly the currently registered sessions.");
            }

            entry.SessionIds.Clear();
            entry.SessionIds.AddRange(requested);
            entry.Version++;
            return new(SessionOrderChangeStatus.Applied, Snapshot(change.ProjectId, entry), null);
        }
    }

    public SessionOrderChangeResult TryAppend(Guid projectId, Guid sessionId, long expectedVersion)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(projectId, out var entry))
            {
                return new(SessionOrderChangeStatus.ProjectNotFound, null, "The project has no session order.");
            }

            if (entry.Version != expectedVersion)
            {
                return VersionMismatch(projectId, expectedVersion, entry);
            }

            if (entry.SessionIds.Contains(sessionId))
            {
                return new(
                    SessionOrderChangeStatus.DuplicateSession,
                    Snapshot(projectId, entry),
                    "The session is already registered.");
            }

            entry.SessionIds.Add(sessionId);
            entry.Version++;
            return new(SessionOrderChangeStatus.Applied, Snapshot(projectId, entry), null);
        }
    }

    public SessionOrderChangeResult TryRemove(Guid projectId, Guid sessionId, long expectedVersion)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(projectId, out var entry))
            {
                return new(SessionOrderChangeStatus.ProjectNotFound, null, "The project has no session order.");
            }

            if (entry.Version != expectedVersion)
            {
                return VersionMismatch(projectId, expectedVersion, entry);
            }

            if (!entry.SessionIds.Remove(sessionId))
            {
                return new(
                    SessionOrderChangeStatus.InvalidMembership,
                    Snapshot(projectId, entry),
                    "The session is not registered.");
            }

            entry.Version++;
            return new(SessionOrderChangeStatus.Applied, Snapshot(projectId, entry), null);
        }
    }

    private static SessionOrderChangeResult VersionMismatch(Guid projectId, long expectedVersion, Entry entry) =>
        new(
            SessionOrderChangeStatus.VersionMismatch,
            Snapshot(projectId, entry),
            $"Expected version {expectedVersion}, but current version is {entry.Version}.");

    private static SessionOrderSnapshot Snapshot(Guid projectId, Entry entry) =>
        new(projectId, entry.SessionIds.ToArray(), entry.Version);

    private static void EnsureDistinct(IReadOnlyCollection<Guid> ids, string parameterName)
    {
        if (ids.Distinct().Count() != ids.Count)
        {
            throw new ArgumentException("Session IDs must be distinct.", parameterName);
        }
    }

    private sealed class Entry(List<Guid> sessionIds, long version)
    {
        public List<Guid> SessionIds { get; } = sessionIds;

        public long Version { get; set; } = version;
    }
}
