namespace TelemoqNet.Api.Mqtt.Protocol;

public sealed class MqttPacketReader(int maxPacketSize)
{
    private readonly List<byte> _buffer = [];
    private static readonly byte[] FixedFlags = [0, 0, 0, 0, 0, 2, 2, 0, 2, 0, 2, 0, 0, 0, 0, 0];

    public MqttReadResult Read(ReadOnlySpan<byte> input)
    {
        if (input.Length > 0) _buffer.AddRange(input.ToArray());
        if (_buffer.Count < 2) return new(MqttReadStatus.NeedMoreData);
        var type = _buffer[0] >> 4;
        if (type is < 1 or > 15) return Error("invalid_packet_type");
        var flags = (byte)(_buffer[0] & 0x0f);
        if (FixedFlags[type] != 0 && flags != FixedFlags[type]) return Error("invalid_flags");

        var multiplier = 1;
        var remaining = 0;
        var index = 1;
        for (var count = 0; count < 4; count++)
        {
            if (index >= _buffer.Count) return new(MqttReadStatus.NeedMoreData);
            var encoded = _buffer[index++];
            remaining += (encoded & 127) * multiplier;
            if (remaining > maxPacketSize) return Error("packet_too_large");
            if ((encoded & 128) == 0)
            {
                var total = index + remaining;
                if (_buffer.Count < total) return new(MqttReadStatus.NeedMoreData);
                var body = _buffer.GetRange(index, remaining).ToArray();
                _buffer.RemoveRange(0, total);
                return new(MqttReadStatus.Packet, new((MqttPacketType)type, flags, body));
            }
            multiplier *= 128;
        }
        return Error("invalid_remaining_length");
    }

    private MqttReadResult Error(string error)
    {
        _buffer.Clear();
        return new(MqttReadStatus.ProtocolError, Error: error);
    }
}
