using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TelemoqNet.Api.Configuration;
using TelemoqNet.Api.Logging;
using TelemoqNet.Api.Mqtt;
using TelemoqNet.Api.Mqtt.Broker;
using TelemoqNet.Api.Mqtt.Protocol;
using Xunit;

namespace TelemoqNet.IntegrationTests;

public sealed class MqttBrokerRegressionTests
{
    [Theory]
    [InlineData(0x11)]
    [InlineData(0x41)]
    [InlineData(0x52)]
    [InlineData(0x60)]
    [InlineData(0x80)]
    [InlineData(0xa0)]
    [InlineData(0xc1)]
    [InlineData(0xe1)]
    [InlineData(0x36)]
    [InlineData(0x38)]
    [InlineData(0xf0)]
    public void PacketReader_RejectsInvalidFixedHeaders(byte header)
    {
        var reader = new MqttPacketReader(128);
        reader.Append([header, 0]);
        Assert.Equal(MqttReadStatus.ProtocolError, reader.Read().Status);
    }

    [Theory]
    [InlineData(0x50)]
    [InlineData(0x62)]
    [InlineData(0x32)]
    [InlineData(0x3c)]
    public void PacketReader_AcceptsValidFixedHeaders(byte header)
    {
        var reader = new MqttPacketReader(128);
        reader.Append([header, 0]);
        Assert.Equal(MqttReadStatus.Packet, reader.Read().Status);
    }

    [Fact]
    public void PacketWriter_RejectsZeroIdentifierForQos1()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MqttPacketWriter.Publish("test", [], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MqttPacketWriter.Publish("test", [], 2, packetId: 1));
    }

    [Fact]
    public void RetainedStorage_IsBoundedAndTrustedMessagesCannotBeOverwritten()
    {
        var hub = new BrokerHub(Options.Create(new MqttOptions
        {
            GlobalAttackerRetainedEnabled = true,
            MaxRetainedMessages = 2
        }));
        var client = new RecordingClient();
        hub.Register(client);
        hub.PublishSystem("home/model", new byte[] { 1 });
        hub.Publish(client, "home/model", new byte[] { 9 }, 0, true);
        hub.Publish(client, "attacker/one", new byte[] { 2 }, 1, true);
        hub.Publish(client, "attacker/two", new byte[] { 3 }, 0, true);
        hub.Publish(client, "attacker/three", new byte[] { 4 }, 0, true);
        var result = hub.Subscribe(client, [new("#", 1)]);
        Assert.Equal(3, result.Retained.Count);
        Assert.Equal(new byte[] { 1 }, Assert.Single(result.Retained, x => x.Topic == "home/model").Payload);
        Assert.DoesNotContain(result.Retained, x => x.Topic == "attacker/three");
        hub.Publish(client, "attacker/one", ReadOnlyMemory<byte>.Empty, 0, true);
        hub.Publish(client, "attacker/three", new byte[] { 4 }, 0, true);
        result = hub.Subscribe(client, [new("#", 1)]);
        Assert.Contains(result.Retained, x => x.Topic == "attacker/three");
    }

    [Fact]
    public void DisabledAttackerRetention_DoesNotAffectTrustedProfileMessages()
    {
        var hub = new BrokerHub(Options.Create(new MqttOptions()));
        var client = new RecordingClient();
        hub.Register(client);
        hub.PublishSystem("home/model", new byte[] { 1 });
        hub.Publish(client, "home/model", ReadOnlyMemory<byte>.Empty, 0, true);
        hub.Publish(client, "attacker/topic", new byte[] { 2 }, 0, true);
        var result = hub.Subscribe(client, [new("#", 0)]);
        Assert.Equal("home/model", Assert.Single(result.Retained).Topic);
    }

    [Fact]
    public void OverlappingSubscriptions_ProduceOneDeliveryAtHighestMatchingQos()
    {
        var hub = new BrokerHub(Options.Create(new MqttOptions()));
        var client = new RecordingClient();
        hub.Register(client);
        hub.Subscribe(client, [new("home/#", 0), new("home/+", 1)]);
        hub.Publish(client, "home/value", new byte[] { 1 }, 1, true);
        var message = Assert.Single(client.Messages);
        Assert.Equal(1, message.Qos);
        Assert.False(message.Retain);
    }

    [Fact]
    public async Task FullOutboundQueue_ClosesSessionAndPersistsReason()
    {
        var options = Options.Create(new MqttOptions { OutboundQueueLength = 1 });
        var store = new CapturingSessionStore();
        await using var stream = new StalledWriterStream();
        await using var session = new MqttSession(stream, "test", store, new BrokerHub(options), options,
            NullLogger<MqttSession>.Instance);
        var run = session.RunAsync(CancellationToken.None);
        await stream.Writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.Deliver("test", [1], 0, false));
        Assert.False(session.Deliver("test", [2], 0, false));
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("outbound_queue_full", Assert.Single(store.Sessions).DisconnectReason);
    }

    private sealed class RecordingClient : IBrokerClient
    {
        public List<(byte Qos, bool Retain)> Messages { get; } = [];
        public bool Deliver(string topic, ReadOnlySpan<byte> payload, byte qos, bool retain)
        {
            Messages.Add((qos, retain));
            return true;
        }

        public void Disconnect(string reason)
        {
        }
    }

    private sealed class StalledWriterStream : Stream
    {
        public TaskCompletionSource Writing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _read;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }

            _read = true;
            byte[] connect = [0x10, 15, 0, 4, 77, 81, 84, 84, 4, 2, 0, 0, 0, 3, 97, 98, 99];
            connect.CopyTo(buffer);
            return connect.Length;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writing.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException(); set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
