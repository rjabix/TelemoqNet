namespace TelemoqNet.Api.Mqtt.Protocol;

public sealed class MqttPacketReader(int maxPacketSize)
{
    private readonly List<byte> _buffer = [];
    private static readonly byte[] FixedFlags = [0, 0, 0, 0, 0, 2, 2, 0, 2, 0, 2, 0, 0, 0, 0, 0];

    public void Append(ReadOnlySpan<byte> input)
    {
        if (input.Length > 0) _buffer.AddRange(input.ToArray());
    }

    public MqttReadResult Read()
    {
        if (_buffer.Count < 2) return new(MqttReadStatus.NeedMoreData);
        var type = _buffer[0] >> 4;
        var flags = (byte)(_buffer[0] & 0x0f);
        var headerError = ValidateHeader(type, flags);
        if (headerError is not null) return Error(headerError);

        var length = ReadRemainingLength();
        if (length.Status == MqttReadStatus.ProtocolError)
            return Error(length.Error!);
        if (length.Status == MqttReadStatus.NeedMoreData)
            return new(MqttReadStatus.NeedMoreData);

        var total = length.HeaderLength + length.RemainingLength;
        if (_buffer.Count < total) return new(MqttReadStatus.NeedMoreData);

        var body = _buffer.GetRange(length.HeaderLength, length.RemainingLength).ToArray();
        _buffer.RemoveRange(0, total);
        return new(MqttReadStatus.Packet, new((MqttPacketType)type, flags, body));
    }

    private string? ValidateHeader(int type, byte flags)
    {
        if (type is < 1 or > 15) return "invalid_packet_type";
        return FixedFlags[type] != 0 && flags != FixedFlags[type]
            ? "invalid_flags"
            : null;
    }

    private RemainingLengthResult ReadRemainingLength()
    {
        var multiplier = 1;
        var remaining = 0;
        for (var index = 1; index <= 4; index++)
        {
            if (index >= _buffer.Count) return new(MqttReadStatus.NeedMoreData);

            var encoded = _buffer[index];
            remaining += (encoded & 127) * multiplier;
            if (remaining > maxPacketSize)
                return new(MqttReadStatus.ProtocolError, Error: "packet_too_large");
            if ((encoded & 128) == 0)
                return new(MqttReadStatus.Packet, index + 1, remaining);

            multiplier *= 128;
        }

        return new(MqttReadStatus.ProtocolError, Error: "invalid_remaining_length");
    }

    private sealed record RemainingLengthResult(
        MqttReadStatus Status,
        int HeaderLength = 0,
        int RemainingLength = 0,
        string? Error = null);

    private MqttReadResult Error(string error)
    {
        _buffer.Clear();
        return new(MqttReadStatus.ProtocolError, Error: error);
    }
}
