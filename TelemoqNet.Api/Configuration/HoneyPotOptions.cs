using TelemoqNet.Api.Emulation;

namespace TelemoqNet.Api.Configuration;

public sealed class HoneypotOptions
{
    public int TelnetPort { get; set; } = 2323;

    public int MaxConnections { get; set; } = 100;

    public int SessionTimeoutSeconds { get; set; } = 300;

    public int MaxLineLength { get; set; } = 2048;

    public int MaxPacketSize { get; set; } = 8192;

    public int MaxResponseLength { get; set; } = 16384;

    public int CommandLatencyMilliseconds { get; set; } = 25;

    public int AuthenticationRetryDelayMilliseconds { get; set; } = 250;

    public int MaxAuthenticationAttempts { get; set; } = 3;

    public bool CloseOnAuthenticationFailure { get; set; } = true;

    public bool CapturePasswords { get; set; }

    public string Device { get; set; } = "GenericLinux";

    public string Banner { get; set; } = DeviceIdentity.Banner;

    public string Hostname { get; set; } = DeviceIdentity.Hostname;

    public MqttOptions Mqtt { get; set; } = new();

    public string[] Credentials { get; set; } =
        ["admin:admin", "root:root", "root:123456", "admin:1234"];
}
