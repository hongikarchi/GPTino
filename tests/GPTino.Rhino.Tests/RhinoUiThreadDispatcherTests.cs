using GPTino.Rhino;
using Xunit;

namespace GPTino.Rhino.Tests;

public sealed class RhinoUiThreadDispatcherTests : IDisposable
{
    public RhinoUiThreadDispatcherTests()
    {
        global::Rhino.RhinoApp.Reset();
    }

    public void Dispose() => global::Rhino.RhinoApp.Reset();

    [Fact]
    public async Task QueuedInvocation_CancellationBeforeCallbackRuns_CompletesCanceled()
    {
        global::Rhino.RhinoApp.InvokeRequired = true;
        using var cancellation = new CancellationTokenSource();
        var invocationCount = 0;

        var task = RhinoUiThreadDispatcher.InvokeAsync(
            () =>
            {
                invocationCount++;
                return Task.FromResult(1);
            },
            cancellation.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, invocationCount);

        global::Rhino.RhinoApp.RunQueuedCallback();
        Assert.Equal(0, invocationCount);
    }

    [Fact]
    public async Task QueuedInvocation_CancellationWhileActionRuns_DoesNotRetainCaller()
    {
        global::Rhino.RhinoApp.InvokeRequired = true;
        using var cancellation = new CancellationTokenSource();
        var actionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = RhinoUiThreadDispatcher.InvokeAsync(
            () =>
            {
                actionStarted.TrySetResult();
                return neverCompletes.Task;
            },
            cancellation.Token);

        global::Rhino.RhinoApp.RunQueuedCallback();
        await actionStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.False(neverCompletes.Task.IsCompleted);
    }

    [Fact]
    public async Task DirectInvocation_CancellationDoesNotRetainCaller()
    {
        global::Rhino.RhinoApp.InvokeRequired = false;
        using var cancellation = new CancellationTokenSource();
        var neverCompletes = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = RhinoUiThreadDispatcher.InvokeAsync(
            () => neverCompletes.Task,
            cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.False(neverCompletes.Task.IsCompleted);
    }
}
