using GPTino.AgentHost.Data;
using GPTino.Contracts;

namespace GPTino.AgentHost.Tests;

public sealed class DurableJobStoreTests
{
    [Theory]
    [InlineData(JobState.Queued)]
    [InlineData(JobState.Validating)]
    [InlineData(JobState.Executing)]
    [InlineData(JobState.Verifying)]
    public async Task StartupConvertsEveryInterruptedPhaseWithoutChangingJobIdentity(JobState state)
    {
        using var directory = new TestDirectory();
        var store = new DurableJobStore(directory.GetPath("live-jobs.db"));
        await store.InitializeAsync();
        var record = CreateRecord(state);
        var inserted = await store.InsertOrReadAsync(record);

        var recovered = Assert.Single(await store.RecoverInterruptedAsync());

        Assert.True(inserted.Inserted);
        Assert.Equal(record.JobId, recovered.JobId);
        Assert.Equal(record.ChangeSet.ChangeSetId, recovered.ChangeSet.ChangeSetId);
        Assert.Equal(record.ChangeSet.ProjectId, recovered.ChangeSet.ProjectId);
        Assert.Equal(record.ChangeSet.SessionId, recovered.ChangeSet.SessionId);
        Assert.Equal(record.RequestHash, recovered.RequestHash);
        Assert.Equal(JobState.RecoveryRequired, recovered.State);
        Assert.Equal("recoveryrequired", recovered.Phase);
        Assert.Equal(DurableJobStore.RestartRecoveryMessage, recovered.Message);
    }

    [Fact]
    public async Task TerminalJobRemainsQueryableAndIdempotentAcrossStartupRecovery()
    {
        using var directory = new TestDirectory();
        var store = new DurableJobStore(directory.GetPath("live-jobs.db"));
        await store.InitializeAsync();
        var record = CreateRecord(JobState.Committed) with
        {
            Phase = "committed",
            Message = "Verified and committed."
        };
        _ = await store.InsertOrReadAsync(record);

        var restored = Assert.Single(await store.RecoverInterruptedAsync());
        var duplicate = await store.InsertOrReadAsync(record with { JobId = Guid.NewGuid() });

        Assert.Equal(JobState.Committed, restored.State);
        Assert.Equal(record.JobId, duplicate.Record.JobId);
        Assert.False(duplicate.Inserted);
    }

    private static DurableJobRecord CreateRecord(JobState state)
    {
        var now = DateTimeOffset.UtcNow;
        var projectId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var changeSet = new ChangeSet(
            Guid.NewGuid(),
            projectId,
            sessionId,
            1,
            null,
            Array.Empty<Guid>(),
            Array.Empty<ResourceExpectation>(),
            Array.Empty<ResourceExpectation>(),
            Array.Empty<TypedOperation>(),
            Array.Empty<VerificationPredicate>(),
            Array.Empty<RollbackBeforeImage>(),
            now);
        return new DurableJobRecord(
            Guid.NewGuid(),
            sessionId,
            "same-request",
            "Durability test",
            changeSet,
            7,
            state,
            state.ToString().ToLowerInvariant(),
            null,
            now,
            now,
            now,
            "sha256-request");
    }
}
