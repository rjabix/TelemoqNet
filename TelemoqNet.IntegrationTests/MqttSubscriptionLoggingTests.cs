using System.Text.Json;
using Xunit;

namespace TelemoqNet.IntegrationTests;

public sealed partial class MqttIntegrationTests
{
    [Fact]
    public async Task Subscribe_CapturesRequestedFiltersAndQosIncludingRejectedRequests()
    {
        await using var fixture = await MqttFixture.StartAsync(options => options.MaxSubscriptionsPerSession = 1);
        await using var client = await fixture.ConnectAsync();
        await ConnectClientAsync(client, "subscription-log");
        byte[] body = [0, 7, .. MqttString("analysis/+/status"), 2, .. MqttString("analysis/#"), 1];
        await client.SendAsync(Packet(8, body, 2));
        Assert.Equal(new byte[] { 0, 7, 1, 0x80 }, (await client.ReadPacketAsync()).Body);
        await client.SendAsync([0xe0, 0]);

        var session = await fixture.WaitForSessionAsync();
        var subscription = Assert.Single(session.Events, e => e.Type == "mqtt.subscribe");
        var detail = Assert.IsType<string>(subscription.Detail);
        Assert.StartsWith($"length={body.Length},packetId=7,count=2,omittedFilters=0,filters=", detail);
        using var filters = JsonDocument.Parse(detail[(detail.IndexOf("filters=", StringComparison.Ordinal) + 8)..]);
        Assert.Equal(2, filters.RootElement.GetArrayLength());
        Assert.Equal("analysis/+/status", filters.RootElement[0].GetProperty("filter").GetString());
        Assert.Equal(2, filters.RootElement[0].GetProperty("qos").GetByte());
        Assert.Equal("analysis/#", filters.RootElement[1].GetProperty("filter").GetString());
        Assert.Equal(1, filters.RootElement[1].GetProperty("qos").GetByte());
        Assert.Equal(2, session.SchemaVersion);
    }

    [Fact]
    public async Task Subscribe_SanitizesAndEscapesFiltersWithoutChangingRouting()
    {
        const string topic = "analysis/\r\n\t\u001b\u007f\"\\,qos=2/温度\u2028";
        const string sanitized = "analysis/\"\\,qos=2/温度\u2028";
        await using var fixture = await MqttFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();
        await ConnectClientAsync(client, "subscription-sanitize");
        await client.SendAsync(Packet(8, [0, 1, .. MqttString(topic), 0], 2));
        Assert.Equal(new byte[] { 0, 1, 0 }, (await client.ReadPacketAsync()).Body);
        await client.SendAsync(QosPublish(topic, "test", 0, 0));
        var delivered = await client.ReadPacketAsync();
        Assert.Equal(3, delivered.Type);
        Assert.Equal(topic, ReadTopic(delivered.Body));
        await client.SendAsync([0xe0, 0]);

        var subscription = Assert.Single((await fixture.WaitForSessionAsync()).Events, e => e.Type == "mqtt.subscribe");
        var detail = Assert.IsType<string>(subscription.Detail);
        Assert.DoesNotContain(detail, char.IsControl);
        Assert.DoesNotContain('\u2028', detail);
        using var filters = JsonDocument.Parse(detail[(detail.IndexOf("filters=", StringComparison.Ordinal) + 8)..]);
        Assert.Equal(sanitized, filters.RootElement[0].GetProperty("filter").GetString());
    }

    [Fact]
    public async Task Subscribe_BoundsCapturedFilterLengthAndCount()
    {
        await using var fixture = await MqttFixture.StartAsync(options => options.MaxSubscriptionsPerSession = 1);
        await using var client = await fixture.ConnectAsync();
        await ConnectClientAsync(client, "subscription-bounds");
        var body = new List<byte> { 0, 1 };
        for (var index = 0; index < 40; index++)
        {
            body.AddRange(MqttString(new string('a', 128) + $"omitted-{index}"));
            body.Add(0);
        }

        await client.SendAsync(Packet(8, body.ToArray(), 2));
        var response = await client.ReadPacketAsync();
        Assert.Equal(9, response.Type);
        Assert.Equal(42, response.Body.Length);
        await client.SendAsync([0xe0, 0]);

        var subscription = Assert.Single((await fixture.WaitForSessionAsync()).Events, e => e.Type == "mqtt.subscribe");
        var detail = Assert.IsType<string>(subscription.Detail);
        Assert.StartsWith($"length={body.Count},packetId=1,count=40,omittedFilters=8,filters=", detail);
        using var filters = JsonDocument.Parse(detail[(detail.IndexOf("filters=", StringComparison.Ordinal) + 8)..]);
        Assert.Equal(32, filters.RootElement.GetArrayLength());
        Assert.All(filters.RootElement.EnumerateArray(), filter =>
            Assert.Equal(new string('a', 128), filter.GetProperty("filter").GetString()));
    }
}
