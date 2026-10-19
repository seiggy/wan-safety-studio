using System.Runtime.CompilerServices;
using System.Threading.Channels;
using VideoSafetyStudio.Application.Abstractions;

namespace VideoSafetyStudio.Infrastructure.Events;

/// <summary>Data is serialized at publish time, so later mutation of the source object cannot change what a client sees.</summary>
public sealed record ServerEvent(string Type, string Data);

/// <summary>In-memory fan-out of state changes to SSE subscribers. A slow client drops its oldest events, never blocks a job.</summary>
public sealed class EventHub : IEventPublisher
{
    private readonly object _gate = new();
    private readonly List<Channel<ServerEvent>> _subscribers = [];

    public int Subscribers
    {
        get
        {
            lock (_gate)
            {
                return _subscribers.Count;
            }
        }
    }

    public void Publish(string type, object payload)
    {
        var message = new ServerEvent(type, System.Text.Json.JsonSerializer.Serialize(payload, payload.GetType(), Application.Json.Options));
        lock (_gate)
        {
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryWrite(message);
            }
        }
    }

    public async IAsyncEnumerable<ServerEvent> SubscribeAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<ServerEvent>(new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_gate)
        {
            _subscribers.Add(channel);
        }

        try
        {
            await foreach (var message in channel.Reader.ReadAllAsync(ct))
            {
                yield return message;
            }
        }
        finally
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
            }
        }
    }
}
