using GPTino.AgentHost.Runtime;

namespace GPTino.AgentHost.Tests;

public sealed class RuntimeControlTests
{
    [Fact]
    public async Task WaitCompletesImmediatelyWhileRuntimeIsRunning()
    {
        var control = new RuntimeControl();

        await control.WaitUntilResumedAsync(CancellationToken.None);

        Assert.False(control.IsPaused);
    }

    [Fact]
    public async Task PauseBlocksWaitersUntilResumeAndTransitionsAreIdempotent()
    {
        var control = new RuntimeControl();

        Assert.True(control.SetPaused(true));
        Assert.True(control.IsPaused);
        Assert.False(control.SetPaused(true));
        var waiter = control.WaitUntilResumedAsync(CancellationToken.None);
        Assert.False(waiter.IsCompleted);

        Assert.True(control.SetPaused(false));
        await waiter.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(control.IsPaused);
        Assert.False(control.SetPaused(false));
    }

    [Fact]
    public async Task PausedWaitHonorsCancellation()
    {
        var control = new RuntimeControl();
        control.SetPaused(true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => control.WaitUntilResumedAsync(cancellation.Token));
    }
}
