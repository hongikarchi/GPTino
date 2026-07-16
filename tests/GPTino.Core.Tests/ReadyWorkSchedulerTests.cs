using GPTino.Contracts;

namespace GPTino.Core.Tests;

public sealed class ReadyWorkSchedulerTests
{
    private readonly ReadyWorkScheduler _scheduler = new();

    [Fact]
    public void UserOrderResolvesReadyJobContention()
    {
        var top = Guid.NewGuid();
        var bottom = Guid.NewGuid();
        var bottomJob = TestData.Job(bottom, 0);
        var topJob = TestData.Job(top, 1);

        var selected = _scheduler.SelectNext(
            [bottomJob, topJob],
            new(TestData.ProjectId, [top, bottom], 4),
            new Dictionary<Guid, JobState>(),
            new Dictionary<Guid, SessionRunState>());

        Assert.Equal(topJob.JobId, selected!.JobId);
    }

    [Fact]
    public void PausedOrBlockedHigherSessionDoesNotBlockLowerReadySession()
    {
        var top = Guid.NewGuid();
        var bottom = Guid.NewGuid();
        var topJob = TestData.Job(top, 0);
        var bottomJob = TestData.Job(bottom, 1);

        var selected = _scheduler.SelectNext(
            [topJob, bottomJob],
            new(TestData.ProjectId, [top, bottom], 0),
            new Dictionary<Guid, JobState>(),
            new Dictionary<Guid, SessionRunState> { [top] = SessionRunState.Paused });

        Assert.Equal(bottomJob.JobId, selected!.JobId);
    }

    [Fact]
    public void DependencyMustCommitBeforeDependentJobBecomesReady()
    {
        var top = Guid.NewGuid();
        var bottom = Guid.NewGuid();
        var prerequisite = TestData.Job(bottom, 1);
        var dependent = TestData.Job(top, 0, [prerequisite.JobId]);
        var order = new SessionOrderSnapshot(TestData.ProjectId, [top, bottom], 0);
        var states = new Dictionary<Guid, JobState>
        {
            [prerequisite.JobId] = JobState.Queued,
            [dependent.JobId] = JobState.Queued,
        };

        var first = _scheduler.SelectNext(
            [dependent, prerequisite], order, states, new Dictionary<Guid, SessionRunState>());
        states[prerequisite.JobId] = JobState.Committed;
        var second = _scheduler.SelectNext(
            [dependent], order, states, new Dictionary<Guid, SessionRunState>());

        Assert.Equal(prerequisite.JobId, first!.JobId);
        Assert.Equal(dependent.JobId, second!.JobId);
    }

    [Fact]
    public void SystemRecoveryPrecedesManualSessionOrder()
    {
        var top = Guid.NewGuid();
        var bottom = Guid.NewGuid();
        var normal = TestData.Job(top, 0);
        var recovery = TestData.Job(bottom, 1, recovery: true);

        var selected = _scheduler.SelectNext(
            [normal, recovery],
            new(TestData.ProjectId, [top, bottom], 0),
            new Dictionary<Guid, JobState>(),
            new Dictionary<Guid, SessionRunState>());

        Assert.Equal(recovery.JobId, selected!.JobId);
    }

    [Fact]
    public void SameSessionUsesStableEnqueueSequence()
    {
        var session = Guid.NewGuid();
        var later = TestData.Job(session, 8);
        var earlier = TestData.Job(session, 3);

        var selected = _scheduler.SelectNext(
            [later, earlier],
            new(TestData.ProjectId, [session], 0),
            new Dictionary<Guid, JobState>(),
            new Dictionary<Guid, SessionRunState>());

        Assert.Equal(earlier.JobId, selected!.JobId);
    }
}
