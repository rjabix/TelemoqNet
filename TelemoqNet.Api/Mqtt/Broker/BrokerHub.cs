using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using TelemoqNet.Api.Configuration;
using TelemoqNet.Api.Mqtt.Protocol;

namespace TelemoqNet.Api.Mqtt.Broker;

public sealed class BrokerHub(IOptions<MqttOptions> options)
{
    private readonly MqttOptions _options = options.Value;
    private readonly ConcurrentDictionary<MqttSession, List<Subscription>> _subscriptions = new();
    private readonly ConcurrentDictionary<string, (string Topic, byte[] Payload)> _retained = new();

    public void Register(MqttSession session) => _subscriptions[session] = [];
    public void Remove(MqttSession session) => _subscriptions.TryRemove(session, out _);

    public IReadOnlyList<(string Topic, byte[] Payload)> Subscribe(MqttSession session, IEnumerable<Subscription> subscriptions)
    {
        if (!_subscriptions.TryGetValue(session, out var current)) return [];
        var retained = new List<(string, byte[])>();
        lock (current)
        {
            foreach (var subscription in subscriptions)
            {
                if (current.Count >= _options.MaxSubscriptionsPerSession) break;
                if (current.Any(x => x.Filter == subscription.Filter)) continue;
                current.Add(subscription);
                retained.AddRange(_retained.Values.Where(x => TopicFilter.Matches(subscription.Filter, x.Topic)));
            }
        }
        return retained;
    }

    public void Unsubscribe(MqttSession session, IEnumerable<string> filters)
    {
        if (!_subscriptions.TryGetValue(session, out var current)) return;
        lock (current) current.RemoveAll(x => filters.Contains(x.Filter, StringComparer.Ordinal));
    }

    public void Publish(MqttSession sender, string topic, ReadOnlyMemory<byte> payload, byte qos, bool retain)
    {
        if (retain && payload.Length == 0) _retained.TryRemove(topic, out _);
        else if (retain) _retained[topic] = (topic, payload.ToArray());

        foreach (var pair in _subscriptions)
        {
            var subscriptions = pair.Value;
            lock (subscriptions)
            {
                foreach (var subscription in subscriptions.Where(x => TopicFilter.Matches(x.Filter, topic)))
                    pair.Key.Enqueue(MqttPacketWriter.Publish(topic, payload.Span, Math.Min(qos, subscription.Qos), false));
            }
        }
    }
}

public sealed record Subscription(string Filter, byte Qos);

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
