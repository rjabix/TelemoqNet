namespace TelemoqNet.Api.Mqtt.Protocol;

public enum MqttPacketType
{
    Connect = 1, ConnAck, Publish, PubAck, PubRec, PubRel, PubComp,
    Subscribe, SubAck, Unsubscribe, UnsubAck, PingReq, PingResp, Disconnect, Auth
}

public enum MqttProtocolLevel
{
    V3 = 3, V4 = 4, V5 = 5
}

public enum MqttReadStatus { NeedMoreData, Packet, ProtocolError }

public sealed record MqttReadResult(
    MqttReadStatus Status,
    MqttPacket? Packet = null,
    string? Error = null);

public sealed record MqttPacket(
    MqttPacketType Type,
    byte Flags,
    ReadOnlyMemory<byte> Body);
