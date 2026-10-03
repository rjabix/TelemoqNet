using Microsoft.Extensions.Options;

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
    public bool AllowAnonymous { get; set; } = true;
    public string[] Credentials { get; set; } = [];
    public string BrokerProfile { get; set; } = "SmartHome";
    public int SysIntervalSeconds { get; set; } = 10;
    public int ResponseJitterMinMs { get; set; }
    public int ResponseJitterMaxMs { get; set; }
    public bool GlobalAttackerRetainedEnabled { get; set; }
}

public sealed class MqttOptionsValidator : IValidateOptions<MqttOptions>
{
    public ValidateOptionsResult Validate(string? name, MqttOptions options)
    {
        var errors = new List<string>();
        if (options.Port is < 1 or > 65535) errors.Add("Honeypot:Mqtt:Port must be between 1 and 65535.");
        if (options.MaxConnections < 1) errors.Add("Honeypot:Mqtt:MaxConnections must be positive.");
        if (options.MaxConnectionsPerIp < 1) errors.Add("Honeypot:Mqtt:MaxConnectionsPerIp must be positive.");
        if (options.ConnectTimeoutSeconds < 1) errors.Add("Honeypot:Mqtt:ConnectTimeoutSeconds must be positive.");
        if (options.MaxSessionLifetimeSeconds < 1) errors.Add("Honeypot:Mqtt:MaxSessionLifetimeSeconds must be positive.");
        if (options.MaxPacketSize is < 128 or > 1_048_576) errors.Add("Honeypot:Mqtt:MaxPacketSize must be between 128 and 1048576.");
        if (options.MaxSubscriptionsPerSession is < 1 or > 1000) errors.Add("Honeypot:Mqtt:MaxSubscriptionsPerSession must be between 1 and 1000.");
        if (options.MaxTopicLevels is < 1 or > 256) errors.Add("Honeypot:Mqtt:MaxTopicLevels must be between 1 and 256.");
        if (options.PayloadCaptureBytes is < 0 or > 1_048_576) errors.Add("Honeypot:Mqtt:PayloadCaptureBytes must be between 0 and 1048576.");
        if (options.MaxEventsPerSession is < 1 or > 10000) errors.Add("Honeypot:Mqtt:MaxEventsPerSession must be between 1 and 10000.");
        if (options.OutboundQueueLength is < 1 or > 10000) errors.Add("Honeypot:Mqtt:OutboundQueueLength must be between 1 and 10000.");
        if (options.SysIntervalSeconds < 1) errors.Add("Honeypot:Mqtt:SysIntervalSeconds must be positive.");
        if (options.ResponseJitterMinMs < 0 || options.ResponseJitterMaxMs < options.ResponseJitterMinMs)
            errors.Add("Honeypot:Mqtt response jitter range is invalid.");
        if (string.IsNullOrWhiteSpace(options.BrokerProfile)) errors.Add("Honeypot:Mqtt:BrokerProfile is required.");
        if (options.Credentials.Any(c => string.IsNullOrWhiteSpace(c) || !c.Contains(':')))
            errors.Add("Honeypot:Mqtt:Credentials entries must use username:password format.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
