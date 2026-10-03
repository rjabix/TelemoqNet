using System.Globalization;
using System.Text;
using TelemoqNet.Api.Configuration;
using TelemoqNet.Api.Mqtt.Broker;

namespace TelemoqNet.Api.Mqtt;

public sealed class SysTopicPublisher(
    BrokerHub hub,
    MqttOptions options,
    ILogger<SysTopicPublisher> logger)
{
    private readonly DateTimeOffset _bootTime = DateTimeOffset.UtcNow;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "MQTT $SYS publisher started with interval {IntervalSeconds}s",
            options.SysIntervalSeconds);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.SysIntervalSeconds));
        while (await timer.WaitForNextTickAsync(cancellationToken))
            PublishSnapshot();
    }

    private void PublishSnapshot()
    {
        var uptime = (long)(DateTimeOffset.UtcNow - _bootTime).TotalSeconds;
        Publish("$SYS/broker/version", "TelemoqNet MQTT Broker");
        Publish("$SYS/broker/uptime", uptime.ToString(CultureInfo.InvariantCulture));
        Publish("$SYS/broker/clients/connected", hub.ConnectedClients.ToString(CultureInfo.InvariantCulture));
        Publish("$SYS/broker/clients/total", hub.TotalConnections.ToString(CultureInfo.InvariantCulture));
        Publish("$SYS/broker/messages/received", hub.MessagesReceived.ToString(CultureInfo.InvariantCulture));
        Publish("$SYS/broker/messages/sent", hub.MessagesSent.ToString(CultureInfo.InvariantCulture));
        Publish("$SYS/broker/bytes/received", hub.BytesReceived.ToString(CultureInfo.InvariantCulture));
        Publish("$SYS/broker/bytes/sent", hub.BytesSent.ToString(CultureInfo.InvariantCulture));
    }

    private void Publish(string topic, string payload) =>
        hub.PublishSystem(topic, Encoding.UTF8.GetBytes(payload));
}
