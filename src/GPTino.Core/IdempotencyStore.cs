using System.Collections.Concurrent;
using GPTino.Contracts;

namespace GPTino.Core;

public sealed record IdempotencyOutcome<T>(T Value, bool IsReplay);

public interface IIdempotencyStore<T>
{
    ValueTask<IdempotencyOutcome<T>> ExecuteOnceAsync(
        IdempotencyKey key,
        Func<CancellationToken, ValueTask<T>> factory,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Coalesces concurrent calls and replays successful values. Failed executions
/// are removed so a later call can make progress.
/// </summary>
public sealed class InMemoryIdempotencyStore<T> : IIdempotencyStore<T>
{
    private readonly ConcurrentDictionary<IdempotencyKey, Entry> _entries = new();

    public async ValueTask<IdempotencyOutcome<T>> ExecuteOnceAsync(
        IdempotencyKey key,
        Func<CancellationToken, ValueTask<T>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);

        var candidate = new Entry(factory);
        var entry = _entries.GetOrAdd(key, candidate);
        var isReplay = !ReferenceEquals(candidate, entry);

        try
        {
            var value = await entry.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new(value, isReplay);
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested &&
            !entry.Value.IsCanceled)
        {
            // Cancelling one waiter must not discard a still-running shared call.
            throw;
        }
        catch
        {
            _entries.TryRemove(new KeyValuePair<IdempotencyKey, Entry>(key, entry));
            throw;
        }
    }

    private sealed class Entry(Func<CancellationToken, ValueTask<T>> factory)
    {
        private readonly Lazy<Task<T>> _value = new(
            () => factory(CancellationToken.None).AsTask(),
            LazyThreadSafetyMode.ExecutionAndPublication);

        public Task<T> Value => _value.Value;
    }
}
