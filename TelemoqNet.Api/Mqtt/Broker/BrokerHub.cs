using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using TelemoqNet.Api.Configuration;

namespace TelemoqNet.Api.Mqtt.Broker;

public sealed class BrokerHub(IOptions<MqttOptions> options)
{
    private readonly MqttOptions _options = options.Value;
    private readonly ConcurrentDictionary<IBrokerClient, List<Subscription>> _subscriptions = new();
    private readonly Dictionary<string, BrokerMessage> _retained = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BrokerMessage> _systemRetained = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IBrokerClient> _clients = new(StringComparer.Ordinal);
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

    public void Activate(IBrokerClient session, string clientId)
    {
        IBrokerClient? previous;
        lock (_clients)
        {
            _clients.TryGetValue(clientId, out previous);
            if (previous != session && previous is not null)
            {
                _subscriptions.TryRemove(previous, out _);
            }

            _clients[clientId] = session;
        }

        // Cancellation may run cleanup synchronously, including removal from this registry.
        if (previous != session)
        {
            previous?.Disconnect("client_id_replaced");
        }
    }

    public void Remove(IBrokerClient session)
    {
        _subscriptions.TryRemove(session, out _);
        lock (_clients)
        {
            foreach (var key in _clients.Where(pair => pair.Value == session).Select(pair => pair.Key).ToArray())
            {
                _clients.Remove(key);
            }
        }
    }

    public SubscriptionResult Subscribe(IBrokerClient session, IEnumerable<Subscription> subscriptions)
    {
        var requested = subscriptions.ToArray();
        if (!_subscriptions.TryGetValue(session, out var current))
        {
            return new([], requested.Select(_ => (byte)0x80).ToArray());
        }

        var retained = new Dictionary<string, BrokerMessage>(StringComparer.Ordinal);
        var results = new List<byte>();
        lock (current)
        {
            foreach (var subscription in requested)
            {
                var index = current.FindIndex(x => x.Filter == subscription.Filter);
                if (!TopicFilter.IsValid(subscription.Filter, _options.MaxTopicLevels) || subscription.Qos > 2 ||
                    (index < 0 && current.Count >= _options.MaxSubscriptionsPerSession))
                {
                    results.Add(0x80);
                    continue;
                }

                var granted = subscription with
                {
                    Qos = Math.Min(subscription.Qos, (byte)1)
                };
                if (index >= 0)
                {
                    current[index] = granted;
                }
                else
                {
                    current.Add(granted);
                }

                results.Add(granted.Qos);
                lock (_retained)
                {
                    foreach (var message in _retained.Values.Concat(_systemRetained.Values)
                                 .Where(x => TopicFilter.Matches(granted.Filter, x.Topic)))
                    {
                        var qos = Math.Min(message.Qos, granted.Qos);
                        if (retained.TryGetValue(message.Topic, out var previous))
                        {
                            qos = Math.Max(qos, previous.Qos);
                        }

                        retained[message.Topic] = message with
                        {
                            Qos = qos
                        };
                    }
                }
            }
        }

        return new(retained.Values.ToArray(), results);
    }

    public void Unsubscribe(IBrokerClient session, IEnumerable<string> filters)
    {
        if (!_subscriptions.TryGetValue(session, out var current))
        {
            return;
        }

        lock (current)
        {
            current.RemoveAll(x => filters.Contains(x.Filter, StringComparer.Ordinal));
        }
    }

    public void Publish(IBrokerClient sender, string topic, ReadOnlyMemory<byte> payload, byte qos, bool retain)
    {
        Interlocked.Increment(ref _messagesReceived);
        Interlocked.Add(ref _bytesReceived, payload.Length);
        // Trusted profile publications have their own storage. Remote clients cannot overwrite them.
        if (retain && _options.GlobalAttackerRetainedEnabled)
        {
            UpdateRetained(_retained, topic, payload, qos);
        }

        PublishToSubscribers(topic, payload, qos);
    }

    public void PublishSystem(string topic, ReadOnlyMemory<byte> payload)
    {
        UpdateRetained(_systemRetained, topic, payload, 0);
        PublishToSubscribers(topic, payload, 0);
    }

    private void UpdateRetained(Dictionary<string, BrokerMessage> target, string topic, ReadOnlyMemory<byte> payload, byte qos)
    {
        lock (_retained)
        {
            if (target == _retained && _systemRetained.ContainsKey(topic))
            {
                return;
            }

            if (target == _systemRetained)
            {
                _retained.Remove(topic);
            }

            if (payload.Length == 0)
            {
                target.Remove(topic);
            }
            else if (target.ContainsKey(topic) || target.Count < _options.MaxRetainedMessages)
            {
                target[topic] = new(topic, payload.ToArray(), qos);
            }
        }
    }

    private void PublishToSubscribers(string topic, ReadOnlyMemory<byte> payload, byte qos)
    {
        foreach (var (client, subscriptions) in _subscriptions)
        {
            byte? grantedQos = null;
            lock (subscriptions)
            {
                foreach (var subscription in subscriptions.Where(x => TopicFilter.Matches(x.Filter, topic)))
                {
                    grantedQos = Math.Max(grantedQos ?? 0, subscription.Qos);
                }
            }

            if (grantedQos is null)
            {
                continue;
            }

            // One live delivery at the highest matching QoS; RETAIN is only set on subscription replay.
            if (!client.Deliver(topic, payload.Span, Math.Min(qos, grantedQos.Value), false))
            {
                continue;
            }

            Interlocked.Increment(ref _messagesSent);
            Interlocked.Add(ref _bytesSent, payload.Length);
        }
    }
}

public interface IBrokerClient
{
    bool Deliver(string topic, ReadOnlySpan<byte> payload, byte qos, bool retain);
    void Disconnect(string reason);
}

public sealed record Subscription(string Filter, byte Qos);
public sealed record BrokerMessage(string Topic, byte[] Payload, byte Qos);
public sealed record SubscriptionResult(IReadOnlyList<BrokerMessage> Retained, IReadOnlyList<byte> Results);

public static class TopicFilter
{
    public static bool IsValid(string filter, int maxLevels)
    {
        if (filter.Length == 0 || filter.Contains('\0'))
        {
            return false;
        }

        var parts = filter.Split('/');
        return parts.Length <= maxLevels && parts.Select((part, index) =>
            (!part.Contains('#') || (part == "#" && index == parts.Length - 1)) &&
            (!part.Contains('+') || part == "+")).All(valid => valid);
    }

    public static bool Matches(string filter, string topic)
    {
        var filterParts = filter.Split('/');
        var topicParts = topic.Split('/');
        if (topic.StartsWith('$') && filterParts[0] is "+" or "#")
        {
            return false;
        }

        for (var i = 0; i < filterParts.Length; i++)
        {
            if (filterParts[i] == "#")
            {
                return i == filterParts.Length - 1 && i <= topicParts.Length;
            }

            if (i >= topicParts.Length || (filterParts[i] != "+" && filterParts[i] != topicParts[i]))
            {
                return false;
            }
        }

        return filterParts.Length == topicParts.Length;
    }
}
