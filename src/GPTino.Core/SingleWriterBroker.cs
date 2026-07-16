using GPTino.Contracts;

namespace GPTino.Core;

public interface IJobExecutor
{
    ValueTask<JobExecutionResult> ExecuteAsync(QueuedJob job, CancellationToken cancellationToken);
}

public sealed record BrokerJobTicket(Guid JobId, Task<JobExecutionResult> Completion);

public interface ISingleWriterBroker : IAsyncDisposable
{
    BrokerJobTicket Enqueue(QueuedJob job);

    bool TryCancel(Guid jobId);

    void Pause();

    void Resume();

    void NotifyScheduleChanged();

    void RecordJobState(Guid jobId, JobState state);

    IReadOnlyDictionary<Guid, JobState> SnapshotStates();
}

/// <summary>
/// An in-process broker that schedules queued work but invokes at most one
/// executor call at a time. It deliberately contains no Rhino integration.
/// </summary>
public sealed class SingleWriterBroker : ISingleWriterBroker
{
    private readonly object _gate = new();
    private readonly IJobExecutor _executor;
    private readonly IReadyWorkScheduler _scheduler;
    private readonly Func<SessionOrderSnapshot> _sessionOrderProvider;
    private readonly Func<IReadOnlyDictionary<Guid, SessionRunState>> _sessionStateProvider;
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Dictionary<Guid, JobState> _knownStates = [];
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private bool _paused;
    private bool _disposed;

    public SingleWriterBroker(
        IJobExecutor executor,
        Func<SessionOrderSnapshot> sessionOrderProvider,
        Func<IReadOnlyDictionary<Guid, SessionRunState>> sessionStateProvider,
        IReadyWorkScheduler? scheduler = null)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _sessionOrderProvider = sessionOrderProvider ?? throw new ArgumentNullException(nameof(sessionOrderProvider));
        _sessionStateProvider = sessionStateProvider ?? throw new ArgumentNullException(nameof(sessionStateProvider));
        _scheduler = scheduler ?? new ReadyWorkScheduler();
        _worker = Task.Run(WorkerAsync);
    }

    public BrokerJobTicket Enqueue(QueuedJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        Entry entry;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_entries.TryGetValue(job.JobId, out var existing))
            {
                return new(job.JobId, existing.Completion.Task);
            }

            entry = new(job);
            _entries.Add(job.JobId, entry);
            _knownStates[job.JobId] = JobState.Queued;
        }

        Signal();
        return new(job.JobId, entry.Completion.Task);
    }

    public bool TryCancel(Guid jobId)
    {
        Entry? entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(jobId, out entry) ||
                _knownStates.GetValueOrDefault(jobId) != JobState.Queued)
            {
                return false;
            }

            _knownStates[jobId] = JobState.Cancelled;
        }

        entry.Completion.TrySetResult(new(jobId, JobState.Cancelled, "Cancelled before execution."));
        Signal();
        return true;
    }

    public void Pause()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _paused = true;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _paused = false;
        }

        Signal();
    }

    public void NotifyScheduleChanged() => Signal();

    public void RecordJobState(Guid jobId, JobState state)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _knownStates[jobId] = state;
        }

        Signal();
    }

    public IReadOnlyDictionary<Guid, JobState> SnapshotStates()
    {
        lock (_gate)
        {
            return new Dictionary<Guid, JobState>(_knownStates);
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Entry> unfinished;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            unfinished = _entries.Values
                .Where(entry => !entry.Completion.Task.IsCompleted)
                .ToList();
        }

        _shutdown.Cancel();
        Signal();

        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }

        foreach (var entry in unfinished)
        {
            entry.Completion.TrySetCanceled();
        }

        _shutdown.Dispose();
        _signal.Dispose();
    }

    private async Task WorkerAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);

            while (TryTakeNext(out var entry))
            {
                JobExecutionResult result;
                try
                {
                    result = await _executor.ExecuteAsync(entry.Job, _shutdown.Token).ConfigureAwait(false);
                    if (!IsTerminal(result.State))
                    {
                        result = new(
                            entry.Job.JobId,
                            JobState.Failed,
                            $"Executor returned non-terminal state {result.State}.");
                    }
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    entry.Completion.TrySetCanceled(_shutdown.Token);
                    throw;
                }
                catch (Exception exception)
                {
                    result = new(entry.Job.JobId, JobState.Failed, exception.Message);
                }

                lock (_gate)
                {
                    _knownStates[entry.Job.JobId] = result.State;
                }

                entry.Completion.TrySetResult(result);
            }
        }
    }

    private bool TryTakeNext(out Entry entry)
    {
        lock (_gate)
        {
            if (_paused || _disposed)
            {
                entry = null!;
                return false;
            }

            var queued = _entries.Values
                .Where(candidate => _knownStates.GetValueOrDefault(candidate.Job.JobId) == JobState.Queued)
                .Select(candidate => candidate.Job)
                .ToArray();
            var selected = _scheduler.SelectNext(
                queued,
                _sessionOrderProvider(),
                _knownStates,
                _sessionStateProvider());

            if (selected is null)
            {
                entry = null!;
                return false;
            }

            entry = _entries[selected.JobId];
            _knownStates[selected.JobId] = JobState.Executing;
            return true;
        }
    }

    private void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (ObjectDisposedException)
        {
            // A late UI notification raced with broker disposal.
        }
    }

    private static bool IsTerminal(JobState state) => state is
        JobState.Committed or
        JobState.RolledBack or
        JobState.Blocked or
        JobState.RecoveryRequired or
        JobState.Failed or
        JobState.Cancelled;

    private sealed class Entry(QueuedJob job)
    {
        public QueuedJob Job { get; } = job;

        public TaskCompletionSource<JobExecutionResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
