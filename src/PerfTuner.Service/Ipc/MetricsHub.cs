using System.Collections.Concurrent;
using System.Threading.Channels;

namespace PerfTuner.Service.Ipc;

/// <summary>
/// Fan-out of encoded metric frames to subscribed sessions.
/// Each frame is serialized ONCE by the engine and the same immutable byte[] is handed
/// to every subscriber. Each subscriber has a 2-slot channel with DropOldest: a slow or
/// stalled GUI only ever sees the freshest data and can never make the service buffer.
/// </summary>
internal sealed class MetricsHub
{
    private readonly ConcurrentDictionary<long, Channel<byte[]>> _subscribers = new();
    private long _nextId;

    public int Count => _subscribers.Count;

    public (long Id, ChannelReader<byte[]> Reader) Subscribe()
    {
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });
        var id = Interlocked.Increment(ref _nextId);
        _subscribers[id] = channel;
        return (id, channel.Reader);
    }

    public void Unsubscribe(long id)
    {
        if (_subscribers.TryRemove(id, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    public void Broadcast(byte[] frame)
    {
        foreach (var pair in _subscribers)
        {
            pair.Value.Writer.TryWrite(frame);
        }
    }
}
