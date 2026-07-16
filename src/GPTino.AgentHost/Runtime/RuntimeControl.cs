namespace GPTino.AgentHost.Runtime;

public sealed class RuntimeControl
{
    private readonly object _sync = new();
    private TaskCompletionSource<bool> _resumed = CompletedSignal();
    private bool _paused;

    public bool IsPaused
    {
        get
        {
            lock (_sync)
            {
                return _paused;
            }
        }
    }

    public bool SetPaused(bool paused)
    {
        TaskCompletionSource<bool>? release = null;
        lock (_sync)
        {
            if (_paused == paused)
            {
                return false;
            }
            _paused = paused;
            if (paused)
            {
                _resumed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            else
            {
                release = _resumed;
            }
        }
        release?.TrySetResult(true);
        return true;
    }

    public Task WaitUntilResumedAsync(CancellationToken cancellationToken)
    {
        Task task;
        lock (_sync)
        {
            task = _resumed.Task;
        }
        return task.WaitAsync(cancellationToken);
    }

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult(true);
        return signal;
    }
}
