using System.Collections.Concurrent;
using System.Threading.Channels;

namespace GPTino.AgentHost.Runtime;

public sealed class EventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<bool>> _subscribers = new();
    private long _revision;

    public long Revision => Interlocked.Read(ref _revision);

    public EventSubscription Subscribe()
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        if (!_subscribers.TryAdd(id, channel))
        {
            throw new InvalidOperationException("Unable to create event subscription.");
        }
        return new EventSubscription(channel.Reader, () => _subscribers.TryRemove(id, out _));
    }

    public void Publish()
    {
        Interlocked.Increment(ref _revision);
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(true);
        }
    }
}

public sealed class EventSubscription(ChannelReader<bool> reader, Action dispose) : IDisposable
{
    public ChannelReader<bool> Reader { get; } = reader;

    public void Dispose() => dispose();
}
