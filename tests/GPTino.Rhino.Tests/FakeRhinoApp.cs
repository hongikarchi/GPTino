namespace Rhino;

internal static class RhinoApp
{
    private static Action? _queuedCallback;

    public static bool InvokeRequired { get; set; }

    public static void InvokeOnUiThread(Action callback)
    {
        if (_queuedCallback is not null)
        {
            throw new InvalidOperationException("A Rhino UI callback is already queued.");
        }
        _queuedCallback = callback;
    }

    public static void RunQueuedCallback()
    {
        var callback = Interlocked.Exchange(ref _queuedCallback, null)
            ?? throw new InvalidOperationException("No Rhino UI callback was queued.");
        callback();
    }

    public static void Reset()
    {
        _queuedCallback = null;
        InvokeRequired = false;
    }
}
