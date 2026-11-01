namespace TelemoqNet.Api.Configuration;

public sealed class MqttOptions
{
    public bool Enabled { get; set; }
    public int Port { get; set; } = 1883;
    public int MaxConnections { get; set; } = 100;
    public int MaxConnectionsPerIp { get; set; } = 10;
    public int ConnectTimeoutSeconds { get; set; } = 10;
    public int MaxSessionLifetimeSeconds { get; set; } = 3600;
    public int MaxPacketSize { get; set; } = 65_536;
    public int MaxSubscriptionsPerSession { get; set; } = 32;
    public int MaxTopicLevels { get; set; } = 32;
    public int PayloadCaptureBytes { get; set; } = 1024;
    public int MaxEventsPerSession { get; set; } = 500;
    public int OutboundQueueLength { get; set; } = 64;
    public int MaxInflightMessages { get; set; } = 64;
    public int MaxRetainedMessages { get; set; } = 256;
    public bool AllowAnonymous { get; set; } = true;
    public string[] Credentials { get; set; } = [];
    public string BrokerProfile { get; set; } = "SmartHome";
    public int SysIntervalSeconds { get; set; } = 10;
    public int ResponseJitterMinMs { get; set; }

    public int ResponseJitterMaxMs { get; set; }

    public bool GlobalAttackerRetainedEnabled { get; set; }
}
