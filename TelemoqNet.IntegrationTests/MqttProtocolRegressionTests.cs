using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TelemoqNet.Api.Configuration;
using TelemoqNet.Api.Logging;
using TelemoqNet.Api.Mqtt;
using TelemoqNet.Api.Mqtt.Broker;
using TelemoqNet.Api.Mqtt.Protocol;
using Xunit;

namespace TelemoqNet.IntegrationTests;

public sealed partial class MqttIntegrationTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task UnsupportedProtocolLevel_IsRefused(byte level)
    {
        await using var fixture = await MqttFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();
        await client.SendAsync(ConnectPacket("version", level));
        Assert.Equal(new byte[] { 0, 1 }, (await client.ReadPacketAsync()).Body);
        await client.ExpectClosedAsync();
        Assert.Equal("unsupported_protocol_level", (await fixture.WaitForSessionAsync()).DisconnectReason);
    }

    public static IEnumerable<object[]> MalformedConnectPackets()
    {
        var valid = ConnectPacket("client", 4);
        foreach (var flags in new byte[] { 3, 0x42, 0x0a, 0x22, 0x1e })
        {
            var packet = valid.ToArray();
            packet[9] = flags;
            yield return [packet];
        }

        yield return [Packet(1, valid[2..^1])]; // Truncated client ID.
        yield return [Packet(1, [.. valid[2..], 0])]; // Trailing bytes.
        var missingUsername = valid.ToArray();
        missingUsername[9] = 0x82;
        yield return [missingUsername];
        var invalidUtf8 = valid.ToArray();
        invalidUtf8[^1] = 0xff;
        yield return [invalidUtf8];
        var nullInId = valid.ToArray();
        nullInId[^1] = 0;
        yield return [nullInId];
        yield return [Packet(1, [0, 6, .. Encoding.ASCII.GetBytes("MQIsdp"), 4, 2, 0, 0, 0, 0])];
        // Will flag set, but the required topic and binary payload are missing.
        var missingWill = valid.ToArray();
        missingWill[9] = 6;
        yield return [missingWill];
    }

    [Theory]
    [MemberData(nameof(MalformedConnectPackets))]
    public async Task MalformedConnect_IsClosedAndClassified(byte[] packet)
    {
        await using var fixture = await MqttFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();
        await client.SendAsync(packet);
        await client.ExpectClosedAsync();
        var session = await fixture.WaitForSessionAsync();
        Assert.Contains(session.Events, e => e.Type == "protocol.error");
        Assert.DoesNotContain(session.Events, e => e.Type == "mqtt.connect");
    }

    [Fact]
    public async Task PersistentSessionRequest_IsExplicitlyRefused()
    {
        await using var fixture = await MqttFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();
        var packet = ConnectPacket("persistent", 4);
        packet[9] = 0;
        await client.SendAsync(packet);
        Assert.Equal(5, (await client.ReadPacketAsync()).Body[1]);
        await client.ExpectClosedAsync();
        Assert.Equal("persistent_sessions_not_supported", (await fixture.WaitForSessionAsync()).DisconnectReason);
    }

    [Theory]
    [InlineData(0xc0, new byte[] { })]
    [InlineData(0xe0, new byte[] { })]
    [InlineData(0x30, new byte[] { 0, 1, 97 })]
    public async Task PacketBeforeConnect_IsRejected(byte header, byte[] body)
    {
        await using var fixture = await MqttFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();
        await client.SendAsync([header, (byte)body.Length, .. body]);
        await client.ExpectClosedAsync();
        Assert.Equal("connect_required", (await fixture.WaitForSessionAsync()).DisconnectReason);
    }

    [Theory]
    [InlineData(0x32, new byte[] { 0, 1, 97 })] // Missing packet ID.
    [InlineData(0x32, new byte[] { 0, 1, 97, 0, 0 })]
    [InlineData(0xc0, new byte[] { 0 })]
    [InlineData(0xe0, new byte[] { 0 })]
    [InlineData(0x40, new byte[] { 0, 0 })]
    [InlineData(0x40, new byte[] { 0, 1, 0 })]
    [InlineData(0x82, new byte[] { 0, 1 })]
    [InlineData(0x82, new byte[] { 0, 1, 0, 1, 97, 3 })]
    [InlineData(0x82, new byte[] { 0, 1, 0, 1, 97, 0, 0 })]
    [InlineData(0xa2, new byte[] { 0, 1 })]
    [InlineData(0xa2, new byte[] { 0, 1, 0, 2, 97 })]
    public async Task MalformedControlPacket_IsClosedAndClassified(byte header, byte[] body)
    {
        await using var fixture = await MqttFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();
        await ConnectClientAsync(client, "malformed");
        await client.SendAsync([header, (byte)body.Length, .. body]);
        await client.ExpectClosedAsync();
        Assert.Contains((await fixture.WaitForSessionAsync()).Events, e => e.Type == "protocol.error");
    }

    [Fact]
    public async Task Qos1_UsesDistinctIdsAndPubackReleasesInflightCapacity()
    {
        await using var fixture = await MqttFixture.StartAsync(o => o.MaxInflightMessages = 2);
        await using var subscriber = await fixture.ConnectAsync();
        await using var publisher = await fixture.ConnectAsync();
        await ConnectClientAsync(subscriber, "subscriber");
        await ConnectClientAsync(publisher, "publisher");
        await subscriber.SendAsync(SubscribePacket(1, "test/qos", 1));
        Assert.Equal(new byte[] { 0, 1, 1 }, (await subscriber.ReadPacketAsync()).Body);
        await publisher.SendAsync(QosPublish("test/qos", "one", 1, 101));
        Assert.Equal(new byte[] { 0, 101 }, (await publisher.ReadPacketAsync()).Body);
        var first = await subscriber.ReadPacketAsync();
        Assert.Equal(2, first.Flags);
        var firstId = ReadPublishId(first.Body);
        Assert.NotEqual(0, firstId);
        await publisher.SendAsync(QosPublish("test/qos", "two", 1, 102));
        Assert.Equal(4, (await publisher.ReadPacketAsync()).Type);
        var secondId = ReadPublishId((await subscriber.ReadPacketAsync()).Body);
        Assert.NotEqual(firstId, secondId);
        await subscriber.SendAsync([0x40, 2, (byte)(firstId >> 8), (byte)firstId, 0xc0, 0]);
        Assert.Equal(13, (await subscriber.ReadPacketAsync()).Type);
        await publisher.SendAsync(QosPublish("test/qos", "three", 1, 103));
        Assert.Equal(4, (await publisher.ReadPacketAsync()).Type);
        Assert.Equal(3, (await subscriber.ReadPacketAsync()).Type);
    }

    [Fact]
    public async Task Qos2Publish_IsRejectedBeforeRouting()
    {
        await using var fixture = await MqttFixture.StartAsync();
        await using var subscriber = await fixture.ConnectAsync();
        await using var publisher = await fixture.ConnectAsync();
        await ConnectClientAsync(subscriber, "subscriber");
        await ConnectClientAsync(publisher, "publisher");
        await subscriber.SendAsync(SubscribePacket(1, "test/qos"));
        Assert.Equal(9, (await subscriber.ReadPacketAsync()).Type);
        await publisher.SendAsync(QosPublish("test/qos", "once", 2, 5));
        await publisher.ExpectClosedAsync();
        Assert.Equal("qos2_not_supported", (await fixture.WaitForSessionAsync()).DisconnectReason);
        await Assert.ThrowsAsync<OperationCanceledException>(() => subscriber.ReadPacketAsync(100));
    }

    [Theory]
    [InlineData("sensor/#/temperature")]
    [InlineData("sensor/temp+")]
    [InlineData("sensor/a#")]
    [InlineData("")]
    public async Task InvalidSubscriptionFilter_IsRejected(string filter)
    {
        await using var fixture = await MqttFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();
        await ConnectClientAsync(client, "filter");
        await client.SendAsync(SubscribePacket(1, filter));
        await client.ExpectClosedAsync();
        Assert.Equal("invalid_subscription", (await fixture.WaitForSessionAsync()).DisconnectReason);
    }

    [Fact]
    public async Task Suback_PreservesOrderAndReplacesExistingSubscriptionsAtLimit()
    {
        await using var fixture = await MqttFixture.StartAsync(o => o.MaxSubscriptionsPerSession = 1);
        await using var client = await fixture.ConnectAsync();
        await ConnectClientAsync(client, "subscriptions");
        await client.SendAsync(SubscribePacket(1, "existing"));
        Assert.Equal(9, (await client.ReadPacketAsync()).Type);
        await client.SendAsync(Packet(8, [0, 2, .. MqttString("new"), 0, .. MqttString("existing"), 2], 2));
        Assert.Equal(new byte[] { 0, 2, 0x80, 1 }, (await client.ReadPacketAsync()).Body);
        await client.SendAsync(Packet(10, [0, 3, .. MqttString("existing")], 2));
        Assert.Equal(new byte[] { 0, 3 }, (await client.ReadPacketAsync()).Body);
        await client.SendAsync(SubscribePacket(4, "new", 1));
        Assert.Equal(new byte[] { 0, 4, 1 }, (await client.ReadPacketAsync()).Body);
    }

    [Fact]
    public async Task Retain_LiveDeliveryClearsFlagAndReplayPreservesQos()
    {
        await using var fixture = await MqttFixture.StartAsync(o => o.GlobalAttackerRetainedEnabled = true);
        await using var client = await fixture.ConnectAsync();
        await ConnectClientAsync(client, "retained");
        await client.SendAsync(SubscribePacket(1, "test/#", 1));
        Assert.Equal(9, (await client.ReadPacketAsync()).Type);
        await client.SendAsync(QosPublish("test/value", "saved", 1, 10, true));
        var live = await client.ReadPacketAsync();
        Assert.Equal(3, live.Type);
        Assert.Equal(2, live.Flags);
        Assert.Equal(4, (await client.ReadPacketAsync()).Type);
        await client.SendAsync(SubscribePacket(2, "test/value", 1));
        Assert.Equal(9, (await client.ReadPacketAsync()).Type);
        var replay = await client.ReadPacketAsync();
        Assert.Equal(3, replay.Flags);
        Assert.NotEqual(0, ReadPublishId(replay.Body));
        await client.SendAsync(QosPublish("test/value", "", 0, 0, true));
        Assert.Equal(0, (await client.ReadPacketAsync()).Flags);
        await client.SendAsync(SubscribePacket(3, "test/#"));
        Assert.Equal(9, (await client.ReadPacketAsync()).Type);
        await Assert.ThrowsAsync<OperationCanceledException>(() => client.ReadPacketAsync(100));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Keepalive_ExpiresWithoutCompleteControlPacket(bool sendFragment)
    {
        await using var fixture = await MqttFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();
        var connect = ConnectPacket("keepalive", 4);
        connect[11] = 1;
        await client.SendAsync(connect);
        Assert.Equal(2, (await client.ReadPacketAsync()).Type);
        if (sendFragment)
        {
            await Task.Delay(900);
            await client.SendAsync([0xc0]);
        }

        await client.ExpectClosedAsync();
        Assert.Equal("keepalive_timeout", (await fixture.WaitForSessionAsync()).DisconnectReason);
    }

    [Fact]
    public async Task Keepalive_RefreshesOnPingAndZeroDisablesTimer()
    {
        await using var fixture = await MqttFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();
        var connect = ConnectPacket("pings", 4);
        connect[11] = 1;
        await client.SendAsync(connect);
        Assert.Equal(2, (await client.ReadPacketAsync()).Type);
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(650);
            await client.SendAsync([0xc0, 0]);
            Assert.Equal(13, (await client.ReadPacketAsync()).Type);
        }

        await using var zero = await fixture.ConnectAsync();
        connect = ConnectPacket("zero", 4);
        connect[11] = 0;
        await zero.SendAsync(connect);
        Assert.Equal(2, (await zero.ReadPacketAsync()).Type);
        await Task.Delay(1600);
        await zero.SendAsync([0xc0, 0]);
        Assert.Equal(13, (await zero.ReadPacketAsync()).Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LastWill_IsPublishedOnlyOnUnexpectedDisconnect(bool graceful)
    {
        await using var fixture = await MqttFixture.StartAsync(o => o.GlobalAttackerRetainedEnabled = true);
        await using var observer = await fixture.ConnectAsync();
        await ConnectClientAsync(observer, "observer");
        await observer.SendAsync(SubscribePacket(1, "test/status", 1));
        Assert.Equal(9, (await observer.ReadPacketAsync()).Type);
        var sender = await fixture.ConnectAsync();
        await sender.SendAsync(Packet(1, [0, 4, .. Encoding.ASCII.GetBytes("MQTT"), 4, 0x2e, 0, 30,
            .. MqttString("will-client"), .. MqttString("test/status"), .. MqttString("offline")]));
        Assert.Equal(2, (await sender.ReadPacketAsync()).Type);
        if (graceful)
        {
            await sender.SendAsync([0xe0, 0]);
        }

        await sender.DisposeAsync();
        if (graceful)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => observer.ReadPacketAsync(250));
        }
        else
        {
            var will = await observer.ReadPacketAsync();
            Assert.Equal("test/status", ReadTopic(will.Body));
            Assert.Equal(2, will.Flags);
            await observer.SendAsync(SubscribePacket(2, "test/status", 1));
            Assert.Equal(9, (await observer.ReadPacketAsync()).Type);
            Assert.Equal(3, (await observer.ReadPacketAsync()).Flags);
        }
    }

    [Fact]
    public async Task WildcardSubscribers_ReceiveSimulatedDeviceTelemetry()
    {
        await using var fixture = await MqttFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();
        await ConnectClientAsync(client, "telemetry");
        await client.SendAsync(SubscribePacket(1, "#"));
        Assert.Equal(9, (await client.ReadPacketAsync()).Type);
        var topics = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            topics.Add(ReadTopic((await client.ReadPacketAsync()).Body));
        }

        Assert.Contains("home/livingroom/temperature", topics);
        Assert.Contains("home/gateway/model", topics);
        Assert.DoesNotContain(topics, topic => topic.StartsWith('$'));
    }

    [Fact]
    public async Task DuplicateClientId_ClosesPreviousConnection()
    {
        await using var fixture = await MqttFixture.StartAsync();
        await using var first = await fixture.ConnectAsync();
        await using var second = await fixture.ConnectAsync();
        await ConnectClientAsync(first, "same");
        await ConnectClientAsync(second, "same");
        await first.ExpectClosedAsync();
        Assert.Equal("client_id_replaced", (await fixture.WaitForSessionAsync()).DisconnectReason);
        await second.SendAsync([0xc0, 0]);
        Assert.Equal(13, (await second.ReadPacketAsync()).Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectionLimits_RejectExcessAndReleaseSlots(bool perIp)
    {
        await using var fixture = await MqttFixture.StartAsync(o =>
        {
            o.MaxConnections = perIp ? 10 : 1;
            o.MaxConnectionsPerIp = perIp ? 1 : 10;
        });
        await using var first = await fixture.ConnectAsync();
        await ConnectClientAsync(first, "first");
        await using var excess = await fixture.ConnectAsync();
        await excess.ExpectClosedAsync();
        await first.SendAsync([0xe0, 0]);
        await first.ExpectClosedAsync();
        await fixture.WaitForSessionAsync();
        await using var replacement = await fixture.ConnectAsync();
        await ConnectClientAsync(replacement, "replacement");
    }

    [Fact]
    public async Task InflightLimit_ClosesOnlySlowSubscriber()
    {
        await using var fixture = await MqttFixture.StartAsync(o => o.MaxInflightMessages = 1);
        await using var subscriber = await fixture.ConnectAsync();
        await using var publisher = await fixture.ConnectAsync();
        await ConnectClientAsync(subscriber, "slow");
        await ConnectClientAsync(publisher, "publisher");
        await subscriber.SendAsync(SubscribePacket(1, "test/#", 1));
        Assert.Equal(9, (await subscriber.ReadPacketAsync()).Type);
        await publisher.SendAsync(QosPublish("test/a", "first", 1, 1));
        Assert.Equal(4, (await publisher.ReadPacketAsync()).Type);
        Assert.Equal(3, (await subscriber.ReadPacketAsync()).Type);
        await publisher.SendAsync(QosPublish("test/a", "second", 1, 2));
        Assert.Equal(4, (await publisher.ReadPacketAsync()).Type);
        await subscriber.ExpectClosedAsync();
        Assert.Equal("inflight_limit", (await fixture.WaitForSessionAsync()).DisconnectReason);
        await publisher.SendAsync([0xc0, 0]);
        Assert.Equal(13, (await publisher.ReadPacketAsync()).Type);
    }

    [Fact]
    public async Task ConnectTimeout_ClosesIncompleteHandshake()
    {
        await using var fixture = await MqttFixture.StartAsync(o => o.ConnectTimeoutSeconds = 1);
        await using var client = await fixture.ConnectAsync();
        await client.SendAsync([0x10]);
        await client.ExpectClosedAsync();
        Assert.Equal("connect_timeout", (await fixture.WaitForSessionAsync()).DisconnectReason);
    }

    [Fact]
    public async Task Shutdown_PersistsActiveSessionsAndClosesConnections()
    {
        var fixture = await MqttFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();
        await ConnectClientAsync(client, "shutdown");
        await fixture.DisposeAsync();
        await client.ExpectClosedAsync();
        var session = Assert.Single(fixture.Sessions);
        Assert.Equal("server_shutdown", session.DisconnectReason);
        Assert.NotNull(session.EndedAt);
    }

    [Fact]
    public async Task PersistenceFailure_IsLoggedAndDoesNotStopListener()
    {
        await using var fixture = await MqttFixture.StartAsync(sessionStore: new FailingMqttStore());
        await using var first = await fixture.ConnectAsync();
        await ConnectClientAsync(first, "failure");
        await first.SendAsync([0xe0, 0]);
        await first.ExpectClosedAsync();
        for (var i = 0; i < 100 && !fixture.Logs.Any(x => x.Contains("Failed to store MQTT session")); i++)
        {
            await Task.Delay(10);
        }

        Assert.Contains(fixture.Logs, x => x.Contains("Failed to store MQTT session"));
        await using var second = await fixture.ConnectAsync();
        await ConnectClientAsync(second, "healthy");
        await second.SendAsync([0xc0, 0]);
        Assert.Equal(13, (await second.ReadPacketAsync()).Type);
    }

    private sealed class FailingMqttStore : ISessionStore
    {
        public Task StoreAsync(HoneypotSession session, CancellationToken cancellationToken) =>
            Task.FromException(new IOException("Test persistence failure"));
    }

    private static async Task ConnectClientAsync(MqttClient client, string id)
    {
        await client.SendAsync(ConnectPacket(id, 4));
        Assert.Equal(new byte[] { 0, 0 }, (await client.ReadPacketAsync()).Body);
    }

    private static byte[] MqttString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return [(byte)(bytes.Length >> 8), (byte)bytes.Length, .. bytes];
    }

    private static byte[] QosPublish(string topic, string payload, byte qos, ushort id, bool retain = false) =>
        Packet(3, [..MqttString(topic), ..(qos > 0 ? new byte[] { (byte)(id >> 8), (byte)id } : []),
            ..Encoding.UTF8.GetBytes(payload)], (byte)((qos << 1) | (retain ? 1 : 0)));

    private static ushort ReadPublishId(byte[] body) =>
        BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(2 + BinaryPrimitives.ReadUInt16BigEndian(body)));
}
