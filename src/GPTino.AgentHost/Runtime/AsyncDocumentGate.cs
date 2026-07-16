namespace GPTino.AgentHost.Runtime;

/// <summary>
/// Writer-preferring async gate: independent live reads may overlap, while a document write
/// waits for all readers and prevents new readers until the complete write epoch ends.
/// </summary>
public sealed class AsyncDocumentGate
{
    private readonly SemaphoreSlim _turnstile = new(1, 1);
    private readonly SemaphoreSlim _roomEmpty = new(1, 1);
    private readonly SemaphoreSlim _readerMutex = new(1, 1);
    private int _readerCount;

    public async ValueTask<IDisposable> EnterReadAsync(CancellationToken cancellationToken = default)
    {
        await _turnstile.WaitAsync(cancellationToken).ConfigureAwait(false);
        _turnstile.Release();

        await _readerMutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_readerCount == 0)
            {
                await _roomEmpty.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            checked
            {
                _readerCount++;
            }
        }
        finally
        {
            _readerMutex.Release();
        }

        return new Lease(this, writer: false);
    }

    public async ValueTask<IDisposable> EnterWriteAsync(CancellationToken cancellationToken = default)
    {
        await _turnstile.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _roomEmpty.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _turnstile.Release();
            throw;
        }

        return new Lease(this, writer: true);
    }

    private void ExitRead()
    {
        _readerMutex.Wait();
        try
        {
            if (_readerCount <= 0)
            {
                throw new InvalidOperationException("The document read gate is not held.");
            }
            _readerCount--;
            if (_readerCount == 0)
            {
                _roomEmpty.Release();
            }
        }
        finally
        {
            _readerMutex.Release();
        }
    }

    private void ExitWrite()
    {
        _roomEmpty.Release();
        _turnstile.Release();
    }

    private sealed class Lease(AsyncDocumentGate owner, bool writer) : IDisposable
    {
        private AsyncDocumentGate? _owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null)
            {
                return;
            }
            if (writer)
            {
                current.ExitWrite();
            }
            else
            {
                current.ExitRead();
            }
        }
    }
}
