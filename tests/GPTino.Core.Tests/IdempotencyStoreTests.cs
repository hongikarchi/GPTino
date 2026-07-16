using GPTino.Contracts;

namespace GPTino.Core.Tests;

public sealed class IdempotencyStoreTests
{
    [Fact]
    public async Task ConcurrentDuplicateCallsExecuteFactoryOnce()
    {
        var store = new InMemoryIdempotencyStore<int>();
        var key = new IdempotencyKey(TestData.ProjectId, "thread", "turn", "call");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async ValueTask<int> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return 42;
        }

        var tasks = Enumerable.Range(0, 12)
            .Select(_ => store.ExecuteOnceAsync(key, Factory).AsTask())
            .ToArray();
        await Task.Delay(25);
        release.SetResult();
        var outcomes = await Task.WhenAll(tasks);

        Assert.Equal(1, calls);
        Assert.All(outcomes, outcome => Assert.Equal(42, outcome.Value));
        Assert.Single(outcomes, outcome => !outcome.IsReplay);
    }

    [Fact]
    public async Task SuccessfulValueIsReplayedWithoutCallingNewFactory()
    {
        var store = new InMemoryIdempotencyStore<string>();
        var key = new IdempotencyKey(TestData.ProjectId, "thread", "turn", "call");

        var first = await store.ExecuteOnceAsync(key, _ => ValueTask.FromResult("first"));
        var replay = await store.ExecuteOnceAsync(key, _ => ValueTask.FromResult("second"));

        Assert.False(first.IsReplay);
        Assert.True(replay.IsReplay);
        Assert.Equal("first", replay.Value);
    }

    [Fact]
    public async Task FailedExecutionIsRemovedSoLaterCallCanRetry()
    {
        var store = new InMemoryIdempotencyStore<int>();
        var key = new IdempotencyKey(TestData.ProjectId, "thread", "turn", "call");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.ExecuteOnceAsync(
                key,
                _ => ValueTask.FromException<int>(new InvalidOperationException("failed"))).AsTask());
        var retry = await store.ExecuteOnceAsync(key, _ => ValueTask.FromResult(7));

        Assert.False(retry.IsReplay);
        Assert.Equal(7, retry.Value);
    }

    [Fact]
    public async Task CancellingOneWaiterDoesNotStartDuplicateSharedExecution()
    {
        var store = new InMemoryIdempotencyStore<int>();
        var key = new IdempotencyKey(TestData.ProjectId, "thread", "turn", "call");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async ValueTask<int> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            await release.Task;
            return 11;
        }

        using var cancellation = new CancellationTokenSource();
        var cancelledWaiter = store.ExecuteOnceAsync(key, Factory, cancellation.Token).AsTask();
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWaiter);

        var secondWaiter = store.ExecuteOnceAsync(key, Factory).AsTask();
        release.SetResult();
        var outcome = await secondWaiter;

        Assert.Equal(1, calls);
        Assert.True(outcome.IsReplay);
        Assert.Equal(11, outcome.Value);
    }
}
