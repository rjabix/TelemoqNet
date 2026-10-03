using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TelemoqNet.Api.Configuration;
using TelemoqNet.Api.Logging;
using TelemoqNet.Api.Mqtt;
using TelemoqNet.Api.Mqtt.Broker;
using Xunit;

namespace TelemoqNet.IntegrationTests;

public sealed class MqttIntegrationTests
{
    [Fact]
    public async Task ConnectPingAndDisconnect_AreCaptured()
    {
        await using var fixture = await MqttFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();

        await client.SendAsync(ConnectPacket("test-client", 4));
        Assert.Equal(0, (await client.ReadPacketAsync()).Body[1]);
        Assert.Contains(
            fixture.Logs,
            log => log.Contains("MQTT honeypot listening", StringComparison.Ordinal));

        await client.SendAsync([0xc0, 0]);
        Assert.Equal(13, (await client.ReadPacketAsync()).Type);
        await client.SendAsync([0xe0, 0]);

        var session = await fixture.WaitForSessionAsync();
        Assert.Equal("mqtt", session.Protocol);
        Assert.Contains(session.Events, e => e.Type == "mqtt.connect");
        Assert.Contains(session.Events, e => e.Type == "mqtt.pingreq");
    }

    [Fact]
    public async Task PublishAndSubscribe_RoutesOnlyToMatchingSession()
    {
        await using var fixture = await MqttFixture.StartAsync();
        await using var subscriber = await fixture.ConnectAsync();
        await using var publisher = await fixture.ConnectAsync();

        await subscriber.SendAsync(ConnectPacket("subscriber", 4));
        Assert.Equal(0, (await subscriber.ReadPacketAsync()).Body[1]);
        await publisher.SendAsync(ConnectPacket("publisher", 4));
        Assert.Equal(0, (await publisher.ReadPacketAsync()).Body[1]);

        await subscriber.SendAsync(SubscribePacket(1, "home/test"));
        Assert.Equal(9, (await subscriber.ReadPacketAsync()).Type);
        await publisher.SendAsync(PublishPacket("home/test", "hello"));

        var delivered = await subscriber.ReadPacketAsync();
        Assert.Equal(3, delivered.Type);
        Assert.Equal("home/test", ReadTopic(delivered.Body.AsSpan()));
        Assert.Equal("hello", Encoding.UTF8.GetString(delivered.Body[11..]));
        await Assert.ThrowsAsync<OperationCanceledException>(() => publisher.ReadPacketAsync(100));
    }

    [Fact]
    public async Task InvalidFixedHeader_IsClosedAndRecorded()
    {
        await using var fixture = await MqttFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();

        await client.SendAsync([0x60, 0]);
        await client.ExpectClosedAsync();

        var session = await fixture.WaitForSessionAsync();
        Assert.Contains(session.Events, e => e.Type == "protocol.error" && e.Detail == "invalid_flags");
    }

    [Fact]
    public async Task MqttCommands_ArePreservedInSessionLogs()
    {
        await using var fixture = await MqttFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();

        await client.SendAsync(ConnectPacket("log-client", 4));
        Assert.Equal(0, (await client.ReadPacketAsync()).Body[1]);

        await client.SendAsync(SubscribePacket(7, "commands/test"));
        Assert.Equal(9, (await client.ReadPacketAsync()).Type);

        await client.SendAsync(PublishPacket("commands/test", "reboot"));
        var delivered = await client.ReadPacketAsync();
        Assert.Equal(3, delivered.Type);

        await client.SendAsync([0xc0, 0]);
        Assert.Equal(13, (await client.ReadPacketAsync()).Type);
        await client.SendAsync([0xe0, 0]);

        var session = await fixture.WaitForSessionAsync();
        Assert.Equal(
            ["mqtt.connect", "mqtt.subscribe", "mqtt.publish", "mqtt.pingreq", "mqtt.disconnect"],
            session.Events
                .Where(e => e.Type.StartsWith("mqtt.", StringComparison.Ordinal))
                .Select(e => e.Type)
                .ToArray());
        Assert.Contains(
            session.Events,
            e => e.Type == "mqtt.connect"
                && e.Detail!.Contains("clientId=log-client", StringComparison.Ordinal));
        Assert.Contains(
            session.Events,
            e => e.Type == "mqtt.publish"
                && e.Detail!.Contains("topic=commands/test", StringComparison.Ordinal)
                && e.Detail.Contains("length=6", StringComparison.Ordinal));
    }

    private static byte[] ConnectPacket(string clientId, byte level) =>
        Packet(1,
        [
            0, 4, (byte)'M', (byte)'Q', (byte)'T', (byte)'T', level, 2, 0, 30, 0, (byte)clientId.Length,
            .. Encoding.ASCII.GetBytes(clientId)
        ]);

    private static byte[] SubscribePacket(ushort id, string filter) =>
        Packet(8, [(byte)(id >> 8), (byte)id, 0, (byte)filter.Length, .. Encoding.ASCII.GetBytes(filter), 0], 2);

    private static byte[] PublishPacket(string topic, string payload) =>
        Packet(3, [0, (byte)topic.Length, .. Encoding.ASCII.GetBytes(topic), .. Encoding.UTF8.GetBytes(payload)]);

    private static byte[] Packet(byte type, byte[] body, byte flags = 0)
    {
        var result = new List<byte> { (byte)((type << 4) | flags), (byte)body.Length };
        result.AddRange(body);
        return result.ToArray();
    }

    private static string ReadTopic(ReadOnlySpan<byte> body)
    {
        var length = (body[0] << 8) | body[1];
        return Encoding.UTF8.GetString(body[2..(length + 2)]);
    }
}

internal sealed class MqttFixture : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly Task _serverTask;
    private readonly int _port;
    private readonly ILoggerFactory _loggerFactory;

    private MqttFixture(int port, Task serverTask, CapturingSessionStore store, ILoggerFactory loggerFactory,
        CancellationTokenSource stop)
    {
        _port = port;
        _serverTask = serverTask;
        Sessions = store.Sessions;
        _loggerFactory = loggerFactory;
        _stop = stop;
    }

    public ConcurrentBag<HoneypotSession> Sessions { get; }
    public ConcurrentBag<string> Logs { get; private set; } = [];

    public static Task<MqttFixture> StartAsync()
    {
        var options = new MqttOptions
        {
            Enabled = true,
            Port = GetFreePort(),
            MaxSessionLifetimeSeconds = 30,
            OutboundQueueLength = 16
        };
        var store = new CapturingSessionStore();
        var loggerProvider = new CapturingLoggerProvider();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var hub = new BrokerHub(Options.Create(options));
        var stop = new CancellationTokenSource();
        var server = new MqttServer(
            Options.Create(options),
            hub,
            store,
            loggerFactory.CreateLogger<MqttServer>(),
            loggerFactory);
        var task = server.RunAsync(stop.Token);
        var fixture = new MqttFixture(options.Port, task, store, loggerFactory, stop)
        {
            Logs = loggerProvider.Messages
        };
        return Task.FromResult(fixture);
    }

    public async Task<MqttClient> ConnectAsync()
    {
        var client = new MqttClient();
        await client.ConnectAsync(_port);
        return client;
    }

    public async Task<HoneypotSession> WaitForSessionAsync()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var session = Sessions.FirstOrDefault();
            if (session is not null) return session;
            await Task.Delay(10);
        }

        throw new TimeoutException("The MQTT session was not persisted.");
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            await _serverTask;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
        _loggerFactory.Dispose();
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

internal sealed class MqttClient : IAsyncDisposable
{
    private readonly TcpClient _client = new();
    private NetworkStream _stream = null!;

    public async Task ConnectAsync(int port)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                await _client.ConnectAsync(IPAddress.Loopback, port);
                break;
            }
            catch (SocketException) when (attempt < 99)
            {
                await Task.Delay(10);
            }
        }

        _stream = _client.GetStream();
    }

    public Task SendAsync(byte[] packet) => _stream.WriteAsync(packet).AsTask();

    public async Task<(byte Type, byte[] Body)> ReadPacketAsync(int timeoutMilliseconds = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMilliseconds);
        var header = new byte[2];
        await ReadExactlyAsync(header, timeout.Token);
        var body = new byte[header[1]];
        await ReadExactlyAsync(body, timeout.Token);
        return ((byte)(header[0] >> 4), body);
    }

    public async Task ExpectClosedAsync()
    {
        using var timeout = new CancellationTokenSource(5000);
        var buffer = new byte[1];
        var read = await _stream.ReadAsync(buffer, timeout.Token);
        Assert.Equal(0, read);
    }

    private async Task ReadExactlyAsync(byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset), token);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}