using System.Globalization;
using System.Text;
using TelemoqNet.Api.Configuration;
using TelemoqNet.Api.Emulation;
using TelemoqNet.Api.Mqtt.Broker;

namespace TelemoqNet.Api.Mqtt;

public sealed class SysTopicPublisher(
    BrokerHub hub,
    MqttOptions options,
    ILogger<SysTopicPublisher> logger,
    string hostname = DeviceIdentity.Hostname)
{
    private readonly DateTimeOffset _bootTime = DateTimeOffset.UtcNow;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "MQTT $SYS publisher started with interval {IntervalSeconds}s",
            options.SysIntervalSeconds);

        PublishSnapshot();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.SysIntervalSeconds));
        while (await timer.WaitForNextTickAsync(cancellationToken))
            PublishSnapshot();
    }

    private void PublishSnapshot()
    {
        var uptime = (long)(DateTimeOffset.UtcNow - _bootTime).TotalSeconds;
        var metrics = new[]
        {
            ("version", DeviceIdentity.MqttVersion),
            ("uptime", uptime.ToString(CultureInfo.InvariantCulture)),
            ("clients/connected", hub.ConnectedClients.ToString(CultureInfo.InvariantCulture)),
            ("clients/total", hub.TotalConnections.ToString(CultureInfo.InvariantCulture)),
            ("messages/received", hub.MessagesReceived.ToString(CultureInfo.InvariantCulture)),
            ("messages/sent", hub.MessagesSent.ToString(CultureInfo.InvariantCulture)),
            ("bytes/received", hub.BytesReceived.ToString(CultureInfo.InvariantCulture)),
            ("bytes/sent", hub.BytesSent.ToString(CultureInfo.InvariantCulture))
        };

        foreach (var (name, value) in metrics)
            Publish($"$SYS/broker/{name}", value);

        // Fixed profile topics only. Values are simulated locally and never trigger device actions.
        Publish($"{DeviceIdentity.MqttTopicPrefix}/model", DeviceIdentity.Model);
        Publish($"{DeviceIdentity.MqttTopicPrefix}/firmware", DeviceIdentity.FirmwareVersion);
        Publish($"{DeviceIdentity.MqttTopicPrefix}/hostname", hostname);
        Publish($"{DeviceIdentity.MqttTopicPrefix}/status", "online");
        Publish("home/livingroom/temperature", (21.5 + Math.Sin(uptime / 60.0) * 0.4).ToString("F1", CultureInfo.InvariantCulture));
        Publish("home/livingroom/humidity", "44");
    }

    private void Publish(string topic, string payload) =>
        hub.PublishSystem(topic, Encoding.UTF8.GetBytes(payload));
}
