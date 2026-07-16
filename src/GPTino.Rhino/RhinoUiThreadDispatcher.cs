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
            return action();
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        return completion.Task;
    }
}
