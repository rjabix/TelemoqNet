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

public sealed class MqttSession : IAsyncDisposable, IBrokerClient
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
            var connectDeadline = DateTimeOffset.UtcNow.AddSeconds(_options.ConnectTimeoutSeconds);
            while (!lifetime.IsCancellationRequested &&
                   await ReadAndDispatchAsync(readBuffer, connectDeadline, lifetime))
            {
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
            _logger.LogInformation(
                "MQTT session {SessionId} closed: {DisconnectReason}",
                _session.SessionId,
                _session.DisconnectReason);
        }
    }

    private async Task<bool> ReadAndDispatchAsync(
        byte[] readBuffer, DateTimeOffset connectDeadline, CancellationTokenSource lifetime)
    {
        var readTask = _stream.ReadAsync(readBuffer, lifetime.Token).AsTask();
        if (!_connected && !await WaitForConnectAsync(readTask, connectDeadline, lifetime))
            return false;

        var count = await readTask;
        if (count == 0)
        {
            _session.DisconnectReason = "client_closed";
            return false;
        }

        _reader.Append(readBuffer.AsSpan(0, count));
        return await DispatchAvailablePacketsAsync(lifetime.Token);
    }

    private async Task<bool> WaitForConnectAsync(
        Task<int> readTask, DateTimeOffset connectDeadline, CancellationTokenSource lifetime)
    {
        var remaining = connectDeadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            SetConnectTimeout(lifetime);
            return false;
        }

        var timeoutTask = Task.Delay(remaining, lifetime.Token);
        if (await Task.WhenAny(readTask, timeoutTask) == readTask)
            return true;

        SetConnectTimeout(lifetime);
        return false;
    }

    private void SetConnectTimeout(CancellationTokenSource lifetime)
    {
        _session.DisconnectReason = "connect_timeout";
        lifetime.Cancel();
    }

    private async Task<bool> DispatchAvailablePacketsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = _reader.Read();
            if (result.Status == MqttReadStatus.NeedMoreData)
                return true;
            if (result.Status == MqttReadStatus.ProtocolError)
                return HandleProtocolError(result.Error);
            if (!await HandlePacketAsync(result.Packet!, cancellationToken))
                return false;
        }
    }

    private bool HandleProtocolError(string? error)
    {
        AddEvent("protocol.error", error);
        _session.DisconnectReason = error;
        _logger.LogWarning(
            "MQTT protocol error in session {SessionId}: {Error}",
            _session.SessionId,
            error);
        return false;
    }

    private async Task<bool> HandlePacketAsync(MqttPacket packet, CancellationToken cancellationToken)
    {
        await ApplyResponseJitterAsync(cancellationToken);
        return await DispatchPacketAsync(packet, cancellationToken);
    }

    private async Task ApplyResponseJitterAsync(CancellationToken cancellationToken)
    {
        if (_options.ResponseJitterMaxMs <= 0)
            return;

        var delay = Random.Shared.Next(_options.ResponseJitterMinMs, _options.ResponseJitterMaxMs + 1);
        await Task.Delay(delay, cancellationToken);
    }

    private async Task<bool> DispatchPacketAsync(
        MqttPacket packet, CancellationToken cancellationToken) => packet.Type switch
    {
        MqttPacketType.Connect => await ConnectAsync(packet.Body, cancellationToken),
        MqttPacketType.PingReq => HandlePing(packet),
        MqttPacketType.Disconnect => HandleDisconnect(packet),
        MqttPacketType.Publish => Publish(packet),
        MqttPacketType.Subscribe => Subscribe(packet),
        MqttPacketType.Unsubscribe => Unsubscribe(packet),
        MqttPacketType.PubRel => HandlePubRel(packet),
        _ => _connected
    };

    private bool HandlePing(MqttPacket packet)
    {
        AddEvent("mqtt.pingreq", packet.Body.Length.ToString());
        Enqueue(MqttPacketWriter.PingResp());
        return true;
    }

    private bool HandleDisconnect(MqttPacket packet)
    {
        AddEvent("mqtt.disconnect", packet.Body.Length.ToString());
        _session.DisconnectReason = "client_disconnect";
        return false;
    }

    private bool HandlePubRel(MqttPacket packet)
    {
        Enqueue(MqttPacketWriter.PubAck(ReadId(packet.Body), 7));
        return true;
    }

    private Task<bool> ConnectAsync(ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        if (_connected)
        {
            AddEvent("protocol.error", "second_connect");
            return Task.FromResult(false);
        }

        if (!TryParseConnect(body.Span, out var request))
            return Task.FromResult(false);

        _level = request.Level;
        var authenticated = _options.AllowAnonymous ||
            (request.Username is not null && request.Password is not null &&
             IsValidCredential(request.Username, request.Password));
        _session.Username = request.Username;
        _session.AuthenticationSucceeded = authenticated;
        if (!authenticated)
        {
            return Task.FromResult(RejectAuthentication());
        }

        _connected = true;
        _session.Events.Add(new(++_sequence, "mqtt.connect", DateTimeOffset.UtcNow,
            $"clientId={Sanitize(request.ClientId)},level={(int)_level},keepAlive={request.KeepAlive}"));
        Enqueue(MqttPacketWriter.ConnAck(_level, false));
        return Task.FromResult(true);
    }

    private bool TryParseConnect(ReadOnlySpan<byte> data, out ConnectRequest request)
    {
        request = default;
        var offset = 0;
        var protocol = ReadString(data, ref offset);
        if (protocol is not ("MQTT" or "MQIsdp") || offset >= data.Length)
        {
            AddEvent("protocol.error", "invalid_protocol_name");
            return false;
        }

        var level = (MqttProtocolLevel)data[offset++];
        if (level is not (MqttProtocolLevel.V3 or MqttProtocolLevel.V4 or MqttProtocolLevel.V5))
        {
            AddEvent("protocol.error", "unsupported_protocol_level");
            return false;
        }

        if (offset + 3 > data.Length)
            return false;

        var flags = data[offset++];
        var keepAlive = BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
        offset += 2;
        if (level == MqttProtocolLevel.V5 && !SkipProperties(data, ref offset))
            return false;

        var clientId = ReadString(data, ref offset);
        if ((flags & 4) != 0 && level == MqttProtocolLevel.V5 &&
            !SkipProperties(data, ref offset))
            return false;
        if ((flags & 4) != 0)
        {
            ReadString(data, ref offset);
            ReadString(data, ref offset);
        }

        var username = (flags & 0x80) != 0 ? ReadString(data, ref offset) : null;
        var password = (flags & 0x40) != 0 ? ReadString(data, ref offset) : null;
        request = new(level, clientId, keepAlive, username, password);
        return true;
    }

    private bool RejectAuthentication()
    {
        _session.AuthenticationFailureReason = "invalid_credentials";
        AddEvent("authentication.attempt", "failure");
        Enqueue(MqttPacketWriter.ConnAck(
            _level,
            false,
            _level == MqttProtocolLevel.V5 ? (byte)0x86 : (byte)4));
        return false;
    }

    private bool Publish(MqttPacket packet)
    {
        if (!_connected) return false;
        var offset = 0;
        var topic = ReadString(packet.Body.Span, ref offset);
        var qos = (byte)((packet.Flags >> 1) & 3);
        var id = qos > 0 ? ReadId(packet.Body, offset) : (ushort)0;
        if (!IsValidPublishTopic(topic))
        {
            AddEvent("protocol.error", "invalid_publish_topic");
            return false;
        }

        if (qos > 0) offset += 2;
        var payload = packet.Body[offset..].ToArray();
        AddEvent("mqtt.publish", BuildPublishDetail(topic, qos, payload));
        PublishToBroker(packet, topic, payload, qos);
        EnqueuePublishAcknowledgement(qos, id);
        return true;
    }

    private bool IsValidPublishTopic(string topic) =>
        !string.IsNullOrWhiteSpace(topic) &&
        !topic.Contains('#') &&
        !topic.Contains('+') &&
        topic.Split('/').Length <= _options.MaxTopicLevels;

    private string BuildPublishDetail(string topic, byte qos, byte[] payload)
    {
        var digest = Convert.ToHexString(SHA256.HashData(payload));
        var detail = $"topic={Sanitize(topic)},qos={qos},length={payload.Length},sha256={digest}";
        if (_options.PayloadCaptureBytes > 0)
        {
            var sampleLength = Math.Min(_options.PayloadCaptureBytes, payload.Length);
            detail += $",sampleBase64={Convert.ToBase64String(payload[..sampleLength])}";
        }

        return detail;
    }

    private void PublishToBroker(MqttPacket packet, string topic, byte[] payload, byte qos) =>
        _hub.Publish(
            this,
            topic,
            payload,
            qos,
            (packet.Flags & 1) != 0 && _options.GlobalAttackerRetainedEnabled);

    private void EnqueuePublishAcknowledgement(byte qos, ushort id)
    {
        if (qos == 1)
            Enqueue(MqttPacketWriter.PubAck(id));
        else if (qos == 2)
            Enqueue(MqttPacketWriter.PubAck(id, 5));
    }

    private bool Subscribe(MqttPacket packet)
    {
        if (!_connected || packet.Body.Length < 5) return false;
        AddEvent("mqtt.subscribe", packet.Body.Length.ToString());
        if (!TryReadSubscriptions(packet.Body, out var id, out var requested))
            return false;

        var result = _hub.Subscribe(this, requested);
        _subscriptions.AddRange(requested.Take(result.AcceptedCount).Select(x => x.Filter));
        foreach (var message in result.Retained)
            Enqueue(MqttPacketWriter.Publish(message.Topic, message.Payload, 0, true));
        Enqueue(MqttPacketWriter.SubAck(
            id,
            requested.Select((_, index) => (byte)(index < result.AcceptedCount ? 0 : 0x80)).ToArray()));
        return true;
    }

    private bool TryReadSubscriptions(
        ReadOnlyMemory<byte> body, out ushort id, out List<Subscription> requested)
    {
        id = ReadId(body);
        requested = [];
        var offset = 2;
        while (offset + 3 <= body.Length)
        {
            var filter = ReadString(body.Span, ref offset);
            if (offset >= body.Length)
                return false;

            var qos = (byte)Math.Min(body.Span[offset++] & 3, 1);
            if (filter.Split('/').Length > _options.MaxTopicLevels)
                return false;
            requested.Add(new(filter, qos));
        }

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

    private bool IsValidCredential(string username, string password) =>
        _options.Credentials
            .Select(value => value.Split(':', 2))
            .Any(parts => parts.Length == 2 &&
                          parts[0] == username &&
                          parts[1] == password);

    private static string Sanitize(string value) => new(value.Where(c => !char.IsControl(c)).Take(128).ToArray());

    private readonly record struct ConnectRequest(
        MqttProtocolLevel Level,
        string ClientId,
        ushort KeepAlive,
        string? Username,
        string? Password);

    public ValueTask DisposeAsync()
    {
        _outbound.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}