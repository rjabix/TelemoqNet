using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using TelemoqNet.Api.Configuration;
using TelemoqNet.Api.Logging;
using TelemoqNet.Api.Mqtt.Broker;
using TelemoqNet.Api.Mqtt.Protocol;

namespace TelemoqNet.Api.Mqtt;

public sealed class MqttSession : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly string _remoteEndpoint;
    private readonly ISessionStore _store;
    private readonly BrokerHub _hub;
    private readonly MqttOptions _options;
    private readonly ILogger<MqttSession> _logger;
    private readonly HoneypotSession _session;
    private readonly Channel<byte[]> _outbound;
    private readonly MqttPacketReader _reader;
    private readonly List<string> _subscriptions = [];
    private MqttProtocolLevel _level = MqttProtocolLevel.V4;
    private bool _connected;
    private long _sequence;

    public MqttSession(Stream stream, string remoteEndpoint, ISessionStore store, BrokerHub hub,
        IOptions<MqttOptions> options, ILogger<MqttSession> logger)
    {
        _stream = stream;
        _remoteEndpoint = remoteEndpoint;
        _store = store;
        _hub = hub;
        _options = options.Value;
        _logger = logger;
        _reader = new(_options.MaxPacketSize);
        _outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(_options.OutboundQueueLength)
            { FullMode = BoundedChannelFullMode.DropOldest });
        _session = new()
        {
            SessionId = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow, RemoteEndpoint = remoteEndpoint,
            Protocol = "mqtt", DeviceProfile = _options.BrokerProfile
        };
        AddEvent("connection.opened", remoteEndpoint);
    }

    public void Enqueue(byte[] packet) => _outbound.Writer.TryWrite(packet);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _hub.Register(this);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(TimeSpan.FromSeconds(_options.MaxSessionLifetimeSeconds));
        var readBuffer = new byte[4096];
        var writerTask = WriteQueuedAsync(lifetime.Token);
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var readTask = _stream.ReadAsync(readBuffer, lifetime.Token).AsTask();
                var count = await readTask;
                if (count == 0)
                {
                    _session.DisconnectReason = "client_closed";
                    break;
                }

                var result = _reader.Read(readBuffer.AsSpan(0, count));
                if (result.Status == MqttReadStatus.NeedMoreData) continue;
                if (result.Status == MqttReadStatus.ProtocolError)
                {
                    AddEvent("protocol.error", result.Error);
                    _session.DisconnectReason = result.Error;
                    break;
                }

                if (!await HandlePacketAsync(result.Packet!, lifetime.Token)) break;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            _session.DisconnectReason = "timeout";
        }
        catch (Exception ex)
        {
            _session.DisconnectReason = "transport_error";
            AddEvent("connection.error", ex.GetType().Name);
            _logger.LogError(ex, "MQTT session {SessionId} failed", _session.SessionId);
        }
        finally
        {
            _outbound.Writer.TryComplete();
            try
            {
                await writerTask;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }

            _hub.Remove(this);
            AddEvent("connection.closed", _session.DisconnectReason);
            _session.EndedAt = DateTimeOffset.UtcNow;
            try
            {
                await _store.StoreAsync(_session, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to store MQTT session {SessionId}", _session.SessionId);
            }
        }
    }

    private async Task<bool> HandlePacketAsync(MqttPacket packet, CancellationToken cancellationToken)
    {
        switch (packet.Type)
        {
            case MqttPacketType.Connect: return await ConnectAsync(packet.Body, cancellationToken);
            case MqttPacketType.PingReq:
                AddEvent("mqtt.pingreq", packet.Body.Length.ToString());
                Enqueue(MqttPacketWriter.PingResp());
                return true;
            case MqttPacketType.Disconnect:
                AddEvent("mqtt.disconnect", packet.Body.Length.ToString());
                _session.DisconnectReason = "client_disconnect";
                return false;
            case MqttPacketType.Publish: return Publish(packet);
            case MqttPacketType.Subscribe: return Subscribe(packet);
            case MqttPacketType.Unsubscribe: return Unsubscribe(packet);
            case MqttPacketType.PubRel:
                Enqueue(MqttPacketWriter.PubAck(ReadId(packet.Body), 7));
                return true;
            default: return _connected;
        }
    }

    private Task<bool> ConnectAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        if (_connected)
        {
            AddEvent("protocol.error", "second_connect");
            return Task.FromResult(false);
        }

        var data = body.Span;
        var offset = 0;
        var protocol = ReadString(data, ref offset);
        if (protocol is not ("MQTT" or "MQIsdp") || offset >= data.Length)
        {
            AddEvent("protocol.error", "invalid_protocol_name");
            return Task.FromResult(false);
        }

        _level = (MqttProtocolLevel)data[offset++];
        if (_level is not (MqttProtocolLevel.V3 or MqttProtocolLevel.V4 or MqttProtocolLevel.V5))
        {
            AddEvent("protocol.error", "unsupported_protocol_level");
            return Task.FromResult(false);
        }

        if (offset + 3 > data.Length) return Task.FromResult(false);
        var flags = data[offset++];
        var keepAlive = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        if (_level == MqttProtocolLevel.V5)
        {
            if (!SkipProperties(data, ref offset)) return Task.FromResult(false);
        }

        var clientId = ReadString(data, ref offset);
        _session.Username = null;
        _session.AuthenticationSucceeded = _options.AllowAnonymous;
        _connected = true;
        _session.Events.Add(new(++_sequence, "mqtt.connect", DateTimeOffset.UtcNow,
            $"clientId={Sanitize(clientId)},level={(int)_level},keepAlive={keepAlive}"));
        Enqueue(MqttPacketWriter.ConnAck(_level, false));
        return Task.FromResult(true);
    }

    private bool Publish(MqttPacket packet)
    {
        if (!_connected) return false;
        var offset = 0;
        var topic = ReadString(packet.Body.Span, ref offset);
        var qos = (byte)((packet.Flags >> 1) & 3);
        var id = qos > 0 ? ReadId(packet.Body, offset) : (ushort)0;
        if (string.IsNullOrWhiteSpace(topic) || topic.Contains('#') || topic.Contains('+'))
        {
            AddEvent("protocol.error", "invalid_publish_topic");
            return false;
        }

        if (qos > 0) offset += 2;
        var payload = packet.Body[offset..].ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(payload));
        AddEvent("mqtt.publish", $"topic={Sanitize(topic)},qos={qos},length={payload.Length},sha256={digest}");
        _hub.Publish(this, topic, payload, qos, (packet.Flags & 1) != 0);
        if (qos == 1) Enqueue(MqttPacketWriter.PubAck(id));
        else if (qos == 2) Enqueue(MqttPacketWriter.PubAck(id, 5));
        return true;
    }

    private bool Subscribe(MqttPacket packet)
    {
        if (!_connected || packet.Body.Length < 5) return false;
        AddEvent("mqtt.subscribe", packet.Body.Length.ToString());
        var offset = 0;
        var id = ReadId(packet.Body);
        offset = 2;
        var requested = new List<Subscription>();
        while (offset + 3 <= packet.Body.Length)
        {
            var filter = ReadString(packet.Body.Span, ref offset);
            if (offset >= packet.Body.Length) return false;
            var qos = (byte)Math.Min(packet.Body.Span[offset++] & 3, 1);
            requested.Add(new(filter, qos));
        }

        var retained = _hub.Subscribe(this, requested);
        _subscriptions.AddRange(requested.Select(x => x.Filter));
        foreach (var message in retained) Enqueue(MqttPacketWriter.Publish(message.Topic, message.Payload, 0, true));
        Enqueue(MqttPacketWriter.SubAck(id, requested.Select(_ => (byte)0).ToArray()));
        return true;
    }

    private bool Unsubscribe(MqttPacket packet)
    {
        AddEvent("mqtt.unsubscribe", packet.Body.Length.ToString());
        var offset = 2;
        var filters = new List<string>();
        while (offset + 2 <= packet.Body.Length) filters.Add(ReadString(packet.Body.Span, ref offset));
        _hub.Unsubscribe(this, filters);
        Enqueue(MqttPacketWriter.UnsubAck(ReadId(packet.Body)));
        return true;
    }

    private async Task WriteQueuedAsync(CancellationToken token)
    {
        while (await _outbound.Reader.WaitToReadAsync(token))
        {
            while (_outbound.Reader.TryRead(out var packet))
                await _stream.WriteAsync(packet, token);
            await _stream.FlushAsync(token);
        }
    }

    private void AddEvent(string type, string? detail)
    {
        if (_session.Events.Count < _options.MaxEventsPerSession)
            _session.Events.Add(new(++_sequence, type, DateTimeOffset.UtcNow, detail));
    }

    private static ushort ReadId(ReadOnlyMemory<byte> body, int offset = 0) => body.Length >= offset + 2
        ? BinaryPrimitives.ReadUInt16BigEndian(body.Span[offset..])
        : (ushort)0;

    private static string ReadString(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset + 2 > data.Length) return string.Empty;
        var length = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        if (length > data.Length - offset)
        {
            offset = data.Length;
            return string.Empty;
        }

        var value = Encoding.UTF8.GetString(data.Slice(offset, length));
        offset += length;
        return value;
    }

    private static bool SkipProperties(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset >= data.Length) return false;
        var length = data[offset++];
        if (length > data.Length - offset) return false;
        offset += length;
        return true;
    }

    private static string Sanitize(string value) => new(value.Where(c => !char.IsControl(c)).Take(128).ToArray());

    public ValueTask DisposeAsync()
    {
        _outbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}