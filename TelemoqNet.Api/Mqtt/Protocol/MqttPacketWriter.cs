using System.Buffers.Binary;
using System.Text;

namespace TelemoqNet.Api.Mqtt.Protocol;

public static class MqttPacketWriter
{
    public static byte[] ConnAck(MqttProtocolLevel level, bool sessionPresent, byte reason = 0) =>
        Packet(2, [(byte)(sessionPresent ? 1 : 0), reason, ..(level == MqttProtocolLevel.V5 ? new byte[] { 0 } : [])]);

    public static byte[] PingResp() => [0xd0, 0];
    public static byte[] Disconnect(MqttProtocolLevel level, byte reason = 0) =>
        level == MqttProtocolLevel.V5 ? Packet(14, [reason, 0]) : [0xe0, 0];

    public static byte[] PubAck(ushort id, byte type = 4) =>
        Packet(type, [..U16(id)]);

    public static byte[] SubAck(ushort id, IReadOnlyList<byte> results) =>
        Packet(9, [..U16(id), ..results]);

    public static byte[] UnsubAck(ushort id) => Packet(11, [..U16(id)]);

    public static byte[] Publish(string topic, ReadOnlySpan<byte> payload, byte qos = 0, bool retain = false, ushort packetId = 0)
    {
        var body = new List<byte>(Encoding.UTF8.GetByteCount(topic) + payload.Length + 4);
        AddString(body, topic);
        if (qos > 0) body.AddRange(U16(packetId));
        body.AddRange(payload.ToArray());
        return Packet(3, body, (byte)((qos << 1) | (retain ? 1 : 0)));
    }

    private static byte[] Packet(byte type, IReadOnlyList<byte> body, byte flags = 0)
    {
        var result = new List<byte> { (byte)((type << 4) | flags) };
        var length = body.Count;
        do { var digit = length % 128; length /= 128; if (length > 0) digit |= 128; result.Add((byte)digit); } while (length > 0);
        result.AddRange(body);
        return result.ToArray();
    }

    private static byte[] U16(ushort value) { var bytes = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, value); return bytes; }
    private static void AddString(List<byte> target, string value) { var bytes = Encoding.UTF8.GetBytes(value); target.AddRange(U16((ushort)bytes.Length)); target.AddRange(bytes); }
}
