using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using TelemoqNet.Api.Configuration;
using TelemoqNet.Api.Mqtt.Protocol;

namespace TelemoqNet.Api.Mqtt.Broker;

public sealed class BrokerHub(IOptions<MqttOptions> options)
{
    private readonly MqttOptions _options = options.Value;
    private readonly ConcurrentDictionary<IBrokerClient, List<Subscription>> _subscriptions = new();
    private readonly ConcurrentDictionary<string, (string Topic, byte[] Payload)> _retained = new();
    private long _totalConnections;
    private long _messagesReceived;
    private long _messagesSent;
    private long _bytesReceived;
    private long _bytesSent;

    public int ConnectedClients => _subscriptions.Count;
    public long TotalConnections => Interlocked.Read(ref _totalConnections);
    public long MessagesReceived => Interlocked.Read(ref _messagesReceived);
    public long MessagesSent => Interlocked.Read(ref _messagesSent);
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    public void Register(IBrokerClient session)
    {
        _subscriptions[session] = [];
        Interlocked.Increment(ref _totalConnections);
    }
    public void Remove(IBrokerClient session) => _subscriptions.TryRemove(session, out _);

    public SubscriptionResult Subscribe(IBrokerClient session, IEnumerable<Subscription> subscriptions)
    {
        if (!_subscriptions.TryGetValue(session, out var current))
            return new([], 0);

        var retained = new List<(string, byte[])>();
        var accepted = 0;
        lock (current)
        {
            foreach (var subscription in subscriptions)
            {
                if (current.Count >= _options.MaxSubscriptionsPerSession) break;
                if (current.Any(x => x.Filter == subscription.Filter)) continue;
                current.Add(subscription);
                accepted++;
                retained.AddRange(_retained.Values.Where(x => TopicFilter.Matches(subscription.Filter, x.Topic)));
            }
        }
        return new(retained, accepted);
    }

    public void Unsubscribe(IBrokerClient session, IEnumerable<string> filters)
    {
        if (!_subscriptions.TryGetValue(session, out var current)) return;
        lock (current) current.RemoveAll(x => filters.Contains(x.Filter, StringComparer.Ordinal));
    }

    public void Publish(IBrokerClient sender, string topic, ReadOnlyMemory<byte> payload, byte qos, bool retain)
    {
        Interlocked.Increment(ref _messagesReceived);
        Interlocked.Add(ref _bytesReceived, payload.Length);
        PublishToSubscribers(topic, payload, qos, retain);
    }

    public void PublishSystem(string topic, ReadOnlyMemory<byte> payload)
    {
        _retained[topic] = (topic, payload.ToArray());
        PublishToSubscribers(topic, payload, 0, true);
    }

    private void PublishToSubscribers(string topic, ReadOnlyMemory<byte> payload, byte qos, bool retain)
    {
        if (retain && payload.Length == 0) _retained.TryRemove(topic, out _);
        else if (retain && _options.GlobalAttackerRetainedEnabled)
            _retained[topic] = (topic, payload.ToArray());

        foreach (var pair in _subscriptions)
        {
            var subscriptions = pair.Value;
            lock (subscriptions)
            {
                foreach (var subscription in subscriptions.Where(x => TopicFilter.Matches(x.Filter, topic)))
                {
                    pair.Key.Enqueue(MqttPacketWriter.Publish(topic, payload.Span, Math.Min(qos, subscription.Qos), retain));
                    Interlocked.Increment(ref _messagesSent);
                    Interlocked.Add(ref _bytesSent, payload.Length);
                }
            }
        }
    }
}

public interface IBrokerClient
{
    void Enqueue(byte[] packet);
}

public sealed record Subscription(string Filter, byte Qos);

public sealed record SubscriptionResult(
    IReadOnlyList<(string Topic, byte[] Payload)> Retained,
    int AcceptedCount);

public static class TopicFilter
{
    public static bool Matches(string filter, string topic)
    {
        var filterParts = filter.Split('/');
        var topicParts = topic.Split('/');
        if (topic.StartsWith('$') && filterParts[0] is "+" or "#") return false;
        for (var i = 0; i < filterParts.Length; i++)
        {
            if (filterParts[i] == "#") return i == filterParts.Length - 1 && i <= topicParts.Length;
            if (i >= topicParts.Length || filterParts[i] != "+" && filterParts[i] != topicParts[i]) return false;
        }
        return filterParts.Length == topicParts.Length;
    }
}
