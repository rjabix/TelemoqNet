using System.Buffers.Binary;
using System.Diagnostics;
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
    private readonly ISessionStore _store;
    private readonly BrokerHub _hub;
    private readonly MqttOptions _options;
    private readonly ILogger<MqttSession> _logger;
    private readonly HoneypotSession _session;
    private readonly Channel<byte[]> _outbound;
    private readonly MqttPacketReader _reader;
    private readonly CancellationTokenSource _abort = new();
    private readonly HashSet<ushort> _inflight = [];
    private ushort _nextPacketId;
    private string? _abortReason;
    private bool _connected;
    private bool _closed;
    private bool _disposed;
    private bool _gracefulDisconnect;
    private BrokerMessage? _will;
    private bool _willRetain;
    private ushort _keepAlive;
    private long _lastPacketAt = Stopwatch.GetTimestamp();
    private long _sequence;

    public MqttSession(Stream stream, string remoteEndpoint, ISessionStore store, BrokerHub hub,
        IOptions<MqttOptions> options, ILogger<MqttSession> logger)
    {
        _stream = stream;
        _store = store;
        _hub = hub;
        _options = options.Value;
        _logger = logger;
        _reader = new(_options.MaxPacketSize);
        _outbound = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(_options.OutboundQueueLength)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
        _session = new()
        {
            SessionId = Guid.NewGuid(),
            StartedAt = DateTimeOffset.UtcNow,
            RemoteEndpoint = remoteEndpoint,
            Protocol = "mqtt",
            DeviceProfile = _options.BrokerProfile
        };
        AddEvent("connection.opened", remoteEndpoint);
    }

    public void Disconnect(string reason)
    {
        lock (_inflight)
        {
            if (_disposed)
            {
                return;
            }

            Interlocked.CompareExchange(ref _abortReason, reason, null);
            _abort.Cancel();
        }
    }

    private bool Enqueue(byte[] packet)
    {
        if (_abort.IsCancellationRequested)
        {
            return false;
        }

        if (_outbound.Writer.TryWrite(packet))
        {
            return true;
        }

        Disconnect("outbound_queue_full");
        return false;
    }

    public bool Deliver(string topic, ReadOnlySpan<byte> payload, byte qos, bool retain)
    {
        // Serialize allocation and enqueue so concurrent publishers cannot reuse an in-flight ID.
        lock (_inflight)
        {
            if (_closed || _abort.IsCancellationRequested)
            {
                return false;
            }

            if (Encoding.UTF8.GetByteCount(topic) + 2 + (qos > 0 ? 2 : 0) + payload.Length > _options.MaxPacketSize)
            {
                Disconnect("outbound_packet_too_large");
                return false;
            }

            ushort id = 0;
            if (qos > 0)
            {
                if (_inflight.Count >= _options.MaxInflightMessages)
                {
                    Disconnect("inflight_limit");
                    return false;
                }

                do
                {
                    _nextPacketId = _nextPacketId == ushort.MaxValue ? (ushort)1 : (ushort)(_nextPacketId + 1);
                }
                while (_inflight.Contains(_nextPacketId));
                id = _nextPacketId;
                _inflight.Add(id);
            }

            return Enqueue(MqttPacketWriter.Publish(topic, payload, qos, retain, id));
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _hub.Register(this);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _abort.Token);
        lifetime.CancelAfter(TimeSpan.FromSeconds(_options.MaxSessionLifetimeSeconds));
        using var writerStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var writerTask = WriteQueuedAsync(writerStop.Token);
        try
        {
            var readBuffer = new byte[4096];
            while (!lifetime.IsCancellationRequested && await ReadAndDispatchAsync(readBuffer, lifetime.Token))
            {
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            _session.DisconnectReason = cancellationToken.IsCancellationRequested ? "server_shutdown" : "timeout";
        }
        catch (Exception ex)
        {
            _session.DisconnectReason = "transport_error";
            AddEvent("connection.error", ex.GetType().Name);
            _logger.LogError(ex, "MQTT session {SessionId} failed", _session.SessionId);
        }
        finally
        {
            _hub.Remove(this);
            lock (_inflight)
            {
                _closed = true;
                _outbound.Writer.TryComplete();
            }

            if (_connected && !_gracefulDisconnect && _will is not null)
            {
                _hub.Publish(this, _will.Topic, _will.Payload, _will.Qos, _willRetain);
                AddEvent("mqtt.will", BuildPublishDetail(_will.Topic, _will.Qos, _will.Payload));
            }

            // A peer that stops reading must not hold a connection slot during shutdown.
            writerStop.CancelAfter(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
            await writerTask;
            if (_abortReason is not null)
            {
                _session.DisconnectReason = _abortReason;
            }

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

            _logger.LogInformation("MQTT session {SessionId} closed: {DisconnectReason}",
                _session.SessionId, _session.DisconnectReason);
        }
    }

    private async Task<bool> ReadAndDispatchAsync(byte[] buffer, CancellationToken token)
    {
        var timeout = !_connected ? TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds) :
            _keepAlive == 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(_keepAlive * 1.5);
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (timeout != Timeout.InfiniteTimeSpan)
        {
            var remaining = timeout - Stopwatch.GetElapsedTime(_lastPacketAt);
            if (remaining <= TimeSpan.Zero)
            {
                return SetReadTimeout();
            }

            readTimeout.CancelAfter(remaining);
        }

        int count;
        try
        {
            count = await _stream.ReadAsync(buffer, readTimeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && readTimeout.IsCancellationRequested)
        {
            return SetReadTimeout();
        }

        if (count == 0)
        {
            _session.DisconnectReason = "client_closed";
            return false;
        }

        _reader.Append(buffer.AsSpan(0, count));
        while (!token.IsCancellationRequested)
        {
            var result = _reader.Read();
            if (result.Status == MqttReadStatus.NeedMoreData)
            {
                return true;
            }

            if (result.Status == MqttReadStatus.ProtocolError)
            {
                return HandleProtocolError(result.Error!);
            }

            _lastPacketAt = Stopwatch.GetTimestamp();
            if (_options.ResponseJitterMaxMs > 0)
            {
                await Task.Delay(Random.Shared.Next(_options.ResponseJitterMinMs, _options.ResponseJitterMaxMs + 1), token);
            }

            if (!HandlePacket(result.Packet!))
            {
                return false;
            }
        }

        return false;
    }

    private bool SetReadTimeout()
    {
        _session.DisconnectReason = _connected ? "keepalive_timeout" : "connect_timeout";
        return false;
    }

    private bool HandleProtocolError(string error)
    {
        AddEvent("protocol.error", error);
        _session.DisconnectReason = error;
        _logger.LogWarning("MQTT protocol error in session {SessionId}: {Error}", _session.SessionId, error);
        return false;
    }

    private bool HandlePacket(MqttPacket packet)
    {
        if (!_connected && packet.Type != MqttPacketType.Connect)
        {
            return HandleProtocolError("connect_required");
        }

        return packet.Type switch
        {
            MqttPacketType.Connect => Connect(packet.Body.Span),
            MqttPacketType.PingReq => HandlePing(packet),
            MqttPacketType.Disconnect => HandleDisconnect(packet),
            MqttPacketType.Publish => Publish(packet),
            MqttPacketType.Subscribe => Subscribe(packet),
            MqttPacketType.Unsubscribe => Unsubscribe(packet),
            MqttPacketType.PubAck => HandlePubAck(packet),
            MqttPacketType.PubRec or MqttPacketType.PubRel or MqttPacketType.PubComp => HandleProtocolError("qos2_not_supported"),
            _ => HandleProtocolError("unexpected_packet_type")
        };
    }

    private bool HandlePing(MqttPacket packet)
    {
        if (packet.Body.Length != 0)
        {
            return HandleProtocolError("invalid_pingreq");
        }

        AddEvent("mqtt.pingreq", "0");
        return Enqueue(MqttPacketWriter.PingResp());
    }

    private bool HandleDisconnect(MqttPacket packet)
    {
        if (packet.Body.Length != 0)
        {
            return HandleProtocolError("invalid_disconnect");
        }

        AddEvent("mqtt.disconnect", "0");
        _gracefulDisconnect = true;
        _session.DisconnectReason = "client_disconnect";
        return false;
    }

    private bool HandlePubAck(MqttPacket packet)
    {
        if (packet.Body.Length != 2)
        {
            return HandleProtocolError("invalid_puback");
        }

        var id = BinaryPrimitives.ReadUInt16BigEndian(packet.Body.Span);
        if (id == 0)
        {
            return HandleProtocolError("invalid_packet_id");
        }

        lock (_inflight)
        {
            _inflight.Remove(id);
        }

        AddEvent("mqtt.puback", id.ToString());
        return true;
    }

    private bool Connect(ReadOnlySpan<byte> data)
    {
        if (_connected)
        {
            return HandleProtocolError("second_connect");
        }

        var reader = new MqttBodyReader(data);
        if (!reader.TryReadString(out var protocol) || !reader.TryReadByte(out var level))
        {
            return HandleProtocolError("malformed_connect");
        }

        if (protocol is not ("MQTT" or "MQIsdp"))
        {
            return HandleProtocolError("invalid_protocol_name");
        }

        if (level != 4)
        {
            return RejectConnect("unsupported_protocol_level", 1);
        }

        if (protocol != "MQTT")
        {
            return HandleProtocolError("invalid_protocol_name");
        }

        if (!reader.TryReadByte(out var flags) || !reader.TryReadUInt16(out var keepAlive))
        {
            return HandleProtocolError("malformed_connect");
        }

        var hasWill = (flags & 4) != 0;
        var willQos = (byte)((flags >> 3) & 3);
        if ((flags & 1) != 0 || ((flags & 0x40) != 0 && (flags & 0x80) == 0) ||
            willQos == 3 || (!hasWill && (flags & 0x38) != 0))
        {
            return HandleProtocolError("invalid_connect_flags");
        }

        if (!reader.TryReadString(out var clientId))
        {
            return HandleProtocolError("malformed_connect");
        }

        BrokerMessage? will = null;
        if (hasWill)
        {
            if (!reader.TryReadString(out var topic) || !IsValidPublishTopic(topic) ||
                !reader.TryReadBinary(out var payload))
            {
                return HandleProtocolError("invalid_will");
            }

            will = new(topic, payload, willQos);
        }

        string? username = null;
        byte[]? password = null;
        if ((flags & 0x80) != 0)
        {
            if (!reader.TryReadString(out var value))
            {
                return HandleProtocolError("malformed_connect");
            }

            username = value;
        }

        if ((flags & 0x40) != 0)
        {
            if (!reader.TryReadBinary(out var value))
            {
                return HandleProtocolError("malformed_connect");
            }

            password = value;
        }

        if (!reader.AtEnd)
        {
            return HandleProtocolError("trailing_connect_data");
        }

        if ((flags & 2) == 0)
        {
            return RejectConnect("persistent_sessions_not_supported", 5);
        }

        if (willQos == 2)
        {
            return RejectConnect("qos2_not_supported", 5);
        }

        var authenticated = _options.AllowAnonymous ||
            (username is not null && password is not null && IsValidCredential(username, password));
        _session.Username = username;
        _session.AuthenticationSucceeded = authenticated;
        if (!authenticated)
        {
            _session.AuthenticationFailureReason = "invalid_credentials";
            AddEvent("authentication.attempt", "failure");
            return RejectConnect("invalid_credentials", 4);
        }

        _keepAlive = keepAlive;
        _will = will;
        _willRetain = (flags & 0x20) != 0;
        _connected = true;
        _hub.Activate(this, clientId.Length == 0 ? Guid.NewGuid().ToString("N") : clientId);
        AddEvent("mqtt.connect", $"clientId={Sanitize(clientId)},level=4,keepAlive={keepAlive}");
        return Enqueue(MqttPacketWriter.ConnAck());
    }

    private bool RejectConnect(string reason, byte code)
    {
        HandleProtocolError(reason);
        Enqueue(MqttPacketWriter.ConnAck(reason: code));
        return false;
    }

    private bool Publish(MqttPacket packet)
    {
        var reader = new MqttBodyReader(packet.Body.Span);
        var qos = (byte)((packet.Flags >> 1) & 3);
        if (!reader.TryReadString(out var topic) || !IsValidPublishTopic(topic))
        {
            return HandleProtocolError("invalid_publish_topic");
        }

        ushort id = 0;
        if (qos > 0 && (!reader.TryReadUInt16(out id) || id == 0))
        {
            return HandleProtocolError("invalid_packet_id");
        }

        if (qos == 2)
        {
            return HandleProtocolError("qos2_not_supported");
        }

        var payload = reader.Remaining.ToArray();
        AddEvent("mqtt.publish", BuildPublishDetail(topic, qos, payload));
        _hub.Publish(this, topic, payload, qos, (packet.Flags & 1) != 0);
        return qos == 0 || Enqueue(MqttPacketWriter.PubAck(id));
    }

    private bool IsValidPublishTopic(string topic) => topic.Length > 0 &&
        !topic.Contains('#') && !topic.Contains('+') && topic.Split('/').Length <= _options.MaxTopicLevels;

    private string BuildPublishDetail(string topic, byte qos, byte[] payload)
    {
        var digest = Convert.ToHexString(SHA256.HashData(payload));
        var detail = $"topic={Sanitize(topic)},qos={qos},length={payload.Length},sha256={digest}";
        if (_options.PayloadCaptureBytes > 0)
        {
            detail += $",sampleBase64={Convert.ToBase64String(payload.AsSpan(0, Math.Min(_options.PayloadCaptureBytes, payload.Length)))}";
        }

        return detail;
    }

    private bool Subscribe(MqttPacket packet)
    {
        var reader = new MqttBodyReader(packet.Body.Span);
        if (!reader.TryReadUInt16(out var id) || id == 0)
        {
            return HandleProtocolError("invalid_packet_id");
        }

        var requested = new List<Subscription>();
        while (!reader.AtEnd)
        {
            if (!reader.TryReadString(out var filter) || !reader.TryReadByte(out var qos) || qos > 2 ||
                !TopicFilter.IsValid(filter, _options.MaxTopicLevels))
            {
                return HandleProtocolError("invalid_subscription");
            }

            requested.Add(new(filter, qos));
        }

        if (requested.Count == 0)
        {
            return HandleProtocolError("empty_subscribe");
        }

        AddEvent("mqtt.subscribe", packet.Body.Length.ToString());
        var result = _hub.Subscribe(this, requested);
        if (!Enqueue(MqttPacketWriter.SubAck(id, result.Results)))
        {
            return false;
        }

        foreach (var message in result.Retained)
        {
            if (!Deliver(message.Topic, message.Payload, message.Qos, true))
            {
                return false;
            }
        }

        return true;
    }

    private bool Unsubscribe(MqttPacket packet)
    {
        var reader = new MqttBodyReader(packet.Body.Span);
        if (!reader.TryReadUInt16(out var id) || id == 0)
        {
            return HandleProtocolError("invalid_packet_id");
        }

        var filters = new List<string>();
        while (!reader.AtEnd)
        {
            if (!reader.TryReadString(out var filter) || !TopicFilter.IsValid(filter, _options.MaxTopicLevels))
            {
                return HandleProtocolError("invalid_subscription");
            }

            filters.Add(filter);
        }

        if (filters.Count == 0)
        {
            return HandleProtocolError("empty_unsubscribe");
        }

        AddEvent("mqtt.unsubscribe", packet.Body.Length.ToString());
        _hub.Unsubscribe(this, filters);
        return Enqueue(MqttPacketWriter.UnsubAck(id));
    }

    private async Task WriteQueuedAsync(CancellationToken token)
    {
        try
        {
            await foreach (var packet in _outbound.Reader.ReadAllAsync(token))
            {
                await _stream.WriteAsync(packet, token);
            }

            await _stream.FlushAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            Disconnect("transport_error");
        }
    }

    private void AddEvent(string type, string? detail)
    {
        if (_session.Events.Count < _options.MaxEventsPerSession)
        {
            _session.Events.Add(new(++_sequence, type, DateTimeOffset.UtcNow, detail));
        }
    }

    private bool IsValidCredential(string username, byte[] password) => _options.Credentials
        .Select(value => value.Split(':', 2))
        .Any(parts => parts.Length == 2 && parts[0] == username &&
                      password.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(parts[1])));

    private static string Sanitize(string value) => new(value.Where(c => !char.IsControl(c)).Take(128).ToArray());

    public ValueTask DisposeAsync()
    {
        lock (_inflight)
        {
            _closed = true;
            _outbound.Writer.TryComplete();
            if (!_disposed)
            {
                _abort.Cancel();
                _abort.Dispose();
                _disposed = true;
            }
        }

        return ValueTask.CompletedTask;
    }
}
