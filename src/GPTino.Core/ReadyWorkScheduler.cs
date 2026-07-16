using GPTino.Contracts;

namespace GPTino.Core;

public interface IReadyWorkScheduler
{
    QueuedJob? SelectNext(
        IEnumerable<QueuedJob> jobs,
        SessionOrderSnapshot sessionOrder,
        IReadOnlyDictionary<Guid, JobState> jobStates,
        IReadOnlyDictionary<Guid, SessionRunState> sessionStates);
}

/// <summary>
/// Selects ready work deterministically. Recovery and dependencies are invariants;
/// the user's ordered session list resolves remaining contention.
/// </summary>
public sealed class ReadyWorkScheduler : IReadyWorkScheduler
{
    public QueuedJob? SelectNext(
        IEnumerable<QueuedJob> jobs,
        SessionOrderSnapshot sessionOrder,
        IReadOnlyDictionary<Guid, JobState> jobStates,
        IReadOnlyDictionary<Guid, SessionRunState> sessionStates)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(sessionOrder);
        ArgumentNullException.ThrowIfNull(jobStates);
        ArgumentNullException.ThrowIfNull(sessionStates);

        var rank = sessionOrder.OrderedSessionIds
            .Select((sessionId, index) => (sessionId, index))
            .ToDictionary(item => item.sessionId, item => item.index);

        return jobs
            .Where(job => IsQueued(job.JobId, jobStates))
            .Where(job => IsSessionSchedulable(job.ChangeSet.SessionId, sessionStates))
            .Where(job => DependenciesCommitted(job.ChangeSet.Dependencies, jobStates))
            .OrderByDescending(job => job.IsSystemRecovery)
            .ThenBy(job => rank.GetValueOrDefault(job.ChangeSet.SessionId, int.MaxValue))
            .ThenBy(job => job.ChangeSet.SessionId)
            .ThenBy(job => job.EnqueueSequence)
            .ThenBy(job => job.EnqueuedAt)
            .ThenBy(job => job.JobId)
            .FirstOrDefault();
    }

    private static bool IsQueued(Guid jobId, IReadOnlyDictionary<Guid, JobState> states) =>
        !states.TryGetValue(jobId, out var state) || state == JobState.Queued;

    private static bool DependenciesCommitted(
        IEnumerable<Guid> dependencies,
        IReadOnlyDictionary<Guid, JobState> states) =>
        dependencies.All(dependency =>
            states.TryGetValue(dependency, out var state) && state == JobState.Committed);

    private static bool IsSessionSchedulable(
        Guid sessionId,
        IReadOnlyDictionary<Guid, SessionRunState> states)
    {
        if (!states.TryGetValue(sessionId, out var state))
        {
            return true;
        }

        return state is not (
            SessionRunState.Paused or
            SessionRunState.Blocked or
            SessionRunState.WaitingForDependency or
            SessionRunState.Completed or
            SessionRunState.Failed);
    }
}
