using GPTino.Contracts;

namespace GPTino.Core.Tests;

public sealed class SingleWriterBrokerTests
{
    [Fact]
    public async Task BrokerExecutesAtMostOneJobAtATime()
    {
        var session = Guid.NewGuid();
        var executor = new RecordingExecutor(delay: TimeSpan.FromMilliseconds(20));
        await using var broker = CreateBroker(executor, [session]);
        var tickets = Enumerable.Range(0, 8)
            .Select(index => broker.Enqueue(TestData.Job(session, index)))
            .ToArray();

        var results = await Task.WhenAll(tickets.Select(ticket => ticket.Completion));

        Assert.All(results, result => Assert.Equal(JobState.Committed, result.State));
        Assert.Equal(1, executor.MaximumConcurrency);
    }

    [Fact]
    public async Task PausedBrokerUsesLatestManualSessionOrderWhenResumed()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var currentOrder = new SessionOrderSnapshot(TestData.ProjectId, [first, second], 0);
        var executor = new RecordingExecutor();
        await using var broker = new SingleWriterBroker(
            executor,
            () => currentOrder,
            () => new Dictionary<Guid, SessionRunState>());
        broker.Pause();
        var firstTicket = broker.Enqueue(TestData.Job(first, 0));
        var secondTicket = broker.Enqueue(TestData.Job(second, 1));
        currentOrder = new(TestData.ProjectId, [second, first], 1);

        broker.Resume();
        await Task.WhenAll(firstTicket.Completion, secondTicket.Completion);

        Assert.Equal([second, first], executor.SessionOrder);
    }

    [Fact]
    public async Task PausedBrokerDoesNotStartNewJobUntilResume()
    {
        var session = Guid.NewGuid();
        var executor = new RecordingExecutor();
        await using var broker = CreateBroker(executor, [session]);
        broker.Pause();
        var ticket = broker.Enqueue(TestData.Job(session, 0));

        await Task.Delay(40);
        Assert.False(ticket.Completion.IsCompleted);
        Assert.Empty(executor.SessionOrder);

        broker.Resume();
        var result = await ticket.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(JobState.Committed, result.State);
    }

    [Fact]
    public async Task DependencyExecutesBeforeHigherOrderedDependentSession()
    {
        var top = Guid.NewGuid();
        var bottom = Guid.NewGuid();
        var executor = new RecordingExecutor();
        await using var broker = CreateBroker(executor, [top, bottom]);
        broker.Pause();
        var prerequisite = TestData.Job(bottom, 1);
        var dependent = TestData.Job(top, 0, [prerequisite.JobId]);
        var dependentTicket = broker.Enqueue(dependent);
        var prerequisiteTicket = broker.Enqueue(prerequisite);

        broker.Resume();
        await Task.WhenAll(dependentTicket.Completion, prerequisiteTicket.Completion);

        Assert.Equal([bottom, top], executor.SessionOrder);
    }

    [Fact]
    public async Task DuplicateJobIdReturnsOriginalCompletion()
    {
        var session = Guid.NewGuid();
        var executor = new RecordingExecutor();
        await using var broker = CreateBroker(executor, [session]);
        broker.Pause();
        var job = TestData.Job(session, 0);

        var first = broker.Enqueue(job);
        var duplicate = broker.Enqueue(job);

        Assert.Same(first.Completion, duplicate.Completion);
        broker.Resume();
        await first.Completion;
        Assert.Single(executor.SessionOrder);
    }

    [Fact]
    public async Task QueuedJobCanBeCancelledWithoutExecutorCall()
    {
        var session = Guid.NewGuid();
        var executor = new RecordingExecutor();
        await using var broker = CreateBroker(executor, [session]);
        broker.Pause();
        var ticket = broker.Enqueue(TestData.Job(session, 0));

        var cancelled = broker.TryCancel(ticket.JobId);
        var result = await ticket.Completion;

        Assert.True(cancelled);
        Assert.Equal(JobState.Cancelled, result.State);
        Assert.Empty(executor.SessionOrder);
    }

    [Fact]
    public async Task ExecutorExceptionBecomesFailedResultAndNextJobContinues()
    {
        var session = Guid.NewGuid();
        var executor = new RecordingExecutor(failFirst: true);
        await using var broker = CreateBroker(executor, [session]);
        var first = broker.Enqueue(TestData.Job(session, 0));
        var second = broker.Enqueue(TestData.Job(session, 1));

        var firstResult = await first.Completion;
        var secondResult = await second.Completion;

        Assert.Equal(JobState.Failed, firstResult.State);
        Assert.Equal(JobState.Committed, secondResult.State);
    }

    private static SingleWriterBroker CreateBroker(
        IJobExecutor executor,
        IReadOnlyList<Guid> sessionOrder) =>
        new(
            executor,
            () => new SessionOrderSnapshot(TestData.ProjectId, sessionOrder, 0),
            () => new Dictionary<Guid, SessionRunState>());

    private sealed class RecordingExecutor(
        TimeSpan? delay = null,
        bool failFirst = false) : IJobExecutor
    {
        private readonly object _gate = new();
        private readonly TimeSpan _delay = delay ?? TimeSpan.Zero;
        private int _active;
        private int _calls;
        private int _maximumConcurrency;

        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public IReadOnlyList<Guid> SessionOrder
        {
            get
            {
                lock (_gate)
                {
                    return _sessionOrder.ToArray();
                }
            }
        }

        private readonly List<Guid> _sessionOrder = [];

        public async ValueTask<JobExecutionResult> ExecuteAsync(
            QueuedJob job,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            var call = Interlocked.Increment(ref _calls);
            lock (_gate)
            {
                _sessionOrder.Add(job.ChangeSet.SessionId);
            }

            try
            {
                if (_delay > TimeSpan.Zero)
                {
                    await Task.Delay(_delay, cancellationToken);
                }

                if (failFirst && call == 1)
                {
                    throw new InvalidOperationException("simulated failure");
                }

                return new(job.JobId, JobState.Committed);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdateMaximum(int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maximumConcurrency);
                if (candidate <= current ||
                    Interlocked.CompareExchange(ref _maximumConcurrency, candidate, current) == current)
                {
                    return;
                }
            }
        }
    }
}
