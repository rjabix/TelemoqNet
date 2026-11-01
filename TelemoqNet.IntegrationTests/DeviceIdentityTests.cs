using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using TelemoqNet.Api.Configuration;
using TelemoqNet.Api.Emulation;
using Xunit;

namespace TelemoqNet.IntegrationTests;

public sealed partial class MqttIntegrationTests
{
    [Theory]
    [InlineData("hg-200")]
    [InlineData("gateway-lab")]
    public async Task GatewayIdentity_IsConsistentAcrossTelnetAndMqtt(string hostname)
    {
        var options = new HoneypotOptions { Hostname = hostname };
        await using var mqtt = await MqttFixture.StartAsync(deviceOptions: options);
        await using var subscriber = await mqtt.ConnectAsync();
        await subscriber.SendAsync(ConnectPacket("identity-client", 4));
        Assert.Equal(0, (await subscriber.ReadPacketAsync()).Body[1]);
        await subscriber.SendAsync(SubscribePacket(1, "home/gateway/#"));
        Assert.Equal(9, (await subscriber.ReadPacketAsync()).Type);

        var metadata = new Dictionary<string, string>();
        for (var index = 0; index < 4; index++)
        {
            var packet = await subscriber.ReadPacketAsync();
            Assert.Equal(3, packet.Type);
            var topic = ReadTopic(packet.Body);
            metadata[topic] = Encoding.UTF8.GetString(packet.Body[(2 + Encoding.UTF8.GetByteCount(topic))..]);
        }

        Assert.Equal("HG-200", metadata["home/gateway/model"]);
        Assert.Equal("1.4.2", metadata["home/gateway/firmware"]);
        Assert.Equal(hostname, metadata["home/gateway/hostname"]);
        Assert.Equal("online", metadata["home/gateway/status"]);

        await using var telnet = await TelnetFixture.StartAsync(settings =>
        {
            settings.Hostname = hostname;
            settings.Mqtt.Enabled = true;
        });
        await using var shell = await telnet.ConnectAsync();
        var greeting = await shell.ReadUntilAsync("login: ");
        Assert.Contains(metadata["home/gateway/model"], greeting);
        Assert.Contains(metadata["home/gateway/firmware"], greeting);
        await shell.LoginAfterPromptAsync();

        var probes = new Dictionary<string, string>
        {
            ["hostname"] = hostname,
            ["uname -a"] = $"Linux {hostname} ",
            ["busybox uname -a"] = $"Linux {hostname} ",
            ["cat /etc/hostname"] = hostname,
            ["cat /etc/hosts"] = $"192.168.1.1 {hostname}",
            ["cat /etc/config/system"] = $"hostname '{hostname}'",
            ["cat /proc/version"] = $"build@{hostname}",
            ["cat /var/log/messages"] = hostname,
            ["cat /etc/device-release"] = $"MODEL={metadata["home/gateway/model"]}\r\nFIRMWARE={metadata["home/gateway/firmware"]}",
            ["cat /etc/config/mqtt"] = "topic_prefix 'home/gateway'",
            ["netstat -lnt"] = "0.0.0.0:1883",
            ["ps"] = "mqttd"
        };
        foreach (var (command, expected) in probes)
        {
            await shell.SendLineAsync(command, useCrNul: true);
            var response = await shell.ReadUntilAsync("# ");
            Assert.Contains(expected, response);
            Assert.DoesNotContain("router", response);
        }
    }

    [Fact]
    public void ProductionConfiguration_UsesSharedDeviceDefaults()
    {
        var configuration = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json").Build();
        var options = new HoneypotOptions();
        configuration.GetSection("Honeypot").Bind(options);
        Assert.Equal(DeviceIdentity.Hostname, options.Hostname);
        Assert.Equal(DeviceIdentity.Banner, options.Banner);
        Assert.True(options.Mqtt.Enabled);
        Assert.Equal(1883, options.Mqtt.Port);
    }

    [Theory]
    [InlineData(true, 2883)]
    [InlineData(false, 1883)]
    public async Task TelnetServiceDetails_FollowMqttConfiguration(bool enabled, int port)
    {
        var device = new GenericLinuxDevice(Options.Create(new HoneypotOptions
        {
            CommandLatencyMilliseconds = 0,
            Mqtt = new MqttOptions { Enabled = enabled, Port = port }
        }));
        var listeners = await device.ExecuteCommandAsync("netstat -lnt", CancellationToken.None);
        var processes = await device.ExecuteCommandAsync("ps", CancellationToken.None);
        var config = await device.ExecuteCommandAsync("cat /etc/config/mqtt", CancellationToken.None);
        Assert.Contains("0.0.0.0:23 ", listeners);
        Assert.Equal(enabled, listeners!.Contains($"0.0.0.0:{port}"));
        Assert.Equal(enabled, processes!.Contains("mqttd"));
        Assert.Contains($"enabled '{(enabled ? 1 : 0)}'", config);
        Assert.Contains($"port '{port}'", config);
    }
}
