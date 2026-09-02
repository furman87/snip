using System.Collections.Concurrent;
using System.Threading.Channels;

public sealed class LiveSnipNotifier
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, ChannelWriter<bool>>> _subscribers = new();

    public (Guid Id, ChannelReader<bool> Reader) Subscribe(string ownerId)
    {
        var channel = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var id = Guid.NewGuid();
        _subscribers.GetOrAdd(ownerId, _ => new ConcurrentDictionary<Guid, ChannelWriter<bool>>())[id] = channel.Writer;
        return (id, channel.Reader);
    }

    public void Unsubscribe(string ownerId, Guid id)
    {
        if (!_subscribers.TryGetValue(ownerId, out var userSubscribers)) return;
        if (userSubscribers.TryRemove(id, out var writer)) writer.TryComplete();
        if (userSubscribers.IsEmpty) _subscribers.TryRemove(new KeyValuePair<string, ConcurrentDictionary<Guid, ChannelWriter<bool>>>(ownerId, userSubscribers));
    }

    public void Publish(string ownerId)
    {
        if (!_subscribers.TryGetValue(ownerId, out var userSubscribers)) return;
        foreach (var writer in userSubscribers.Values) writer.TryWrite(true);
    }
}
