namespace GPTino.BridgeContract;

/// <summary>
/// Marks the span in which a bridge operation is executing on the host UI thread. Grasshopper
/// solves inside an operation pump the Windows message queue, so user actions (opening another
/// GH document, closing one) dispatch re-entrantly ON TOP of the in-flight operation's stack.
/// Catalog/runtime bookkeeping that reacts to those document events must not run inside that
/// window — tearing down the very document the operation is mutating is how Rhino dies with no
/// crash dump (native state mutated after disposal). Consumers check
/// <see cref="IsActiveOnCurrentThread"/> to defer their reaction and subscribe to
/// <see cref="Exited"/> to run it immediately after the operation completes.
/// </summary>
public static class BridgeUiOperationScope
{
    private static int _depth;
    private static int _activeThreadId;

    /// <summary>
    /// Raised on the operation's thread right after the outermost scope exits — the deferred
    /// bookkeeping's cue to drain. Handler exceptions are swallowed: a bookkeeping failure must
    /// never corrupt the operation result that is already on its way back over the bridge.
    /// </summary>
    public static event Action? Exited;

    /// <summary>
    /// True when a bridge operation is currently executing on THIS thread — i.e. the caller was
    /// re-entered from the operation's message pump. Other threads keep normal behavior: the
    /// crash window is same-thread re-entrancy only.
    /// </summary>
    public static bool IsActiveOnCurrentThread =>
        Volatile.Read(ref _depth) > 0 &&
        Volatile.Read(ref _activeThreadId) == Environment.CurrentManagedThreadId;

    /// <summary>Enters the scope; dispose to exit. Nesting is supported (outermost exit fires
    /// <see cref="Exited"/>). Operations are serialized onto one UI thread, so concurrent scopes
    /// on different threads do not occur in practice; the counter still balances if they do.</summary>
    public static IDisposable Enter()
    {
        if (Interlocked.Increment(ref _depth) == 1)
        {
            Volatile.Write(ref _activeThreadId, Environment.CurrentManagedThreadId);
        }
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (Interlocked.Decrement(ref _depth) != 0)
            {
                return;
            }
            Volatile.Write(ref _activeThreadId, 0);
            try
            {
                Exited?.Invoke();
            }
            catch (Exception)
            {
                // Deferred bookkeeping failures must not surface into the operation path.
            }
        }
    }
}
