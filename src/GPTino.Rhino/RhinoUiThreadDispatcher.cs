namespace GPTino.Rhino;

internal static class RhinoUiThreadDispatcher
{
    public static Task<T> InvokeAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        cancellationToken.ThrowIfCancellationRequested();

        if (!global::Rhino.RhinoApp.InvokeRequired)
        {
            return action().WaitAsync(cancellationToken);
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationRegistration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        try
        {
            global::Rhino.RhinoApp.InvokeOnUiThread((Action)(async () =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    completion.TrySetResult(await action().ConfigureAwait(true));
                }
                catch (OperationCanceledException exception)
                {
                    completion.TrySetCanceled(exception.CancellationToken);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }));
        }
        catch
        {
            cancellationRegistration.Dispose();
            throw;
        }
        return AwaitCompletionAsync(completion.Task, cancellationRegistration);
    }

    private static async Task<T> AwaitCompletionAsync<T>(
        Task<T> completion,
        CancellationTokenRegistration cancellationRegistration)
    {
        try
        {
            return await completion.ConfigureAwait(false);
        }
        finally
        {
            cancellationRegistration.Dispose();
        }
    }
}
