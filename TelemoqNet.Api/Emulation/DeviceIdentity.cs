namespace TelemoqNet.Api.Emulation;

public static class DeviceIdentity
{
    public const string Model = "HG-200";
    public const string FirmwareVersion = "1.4.2";
    public const string Hostname = "hg-200";
    public const string Banner = Model + " Smart Home Gateway - Firmware " + FirmwareVersion +
                                 "\r\nBusyBox v1.19.4 (2014-06-12 08:23:17 UTC)";
    public const string MqttTopicPrefix = "home/gateway";
    public const string MqttVersion = Model + " MQTT Service " + FirmwareVersion;
}
