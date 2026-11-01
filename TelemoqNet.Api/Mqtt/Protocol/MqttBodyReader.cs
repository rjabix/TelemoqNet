using System.Buffers.Binary;
using System.Text;

namespace TelemoqNet.Api.Mqtt.Protocol;

// Length-prefixed MQTT strings and binary fields must fit in the packet in full.
internal ref struct MqttBodyReader(ReadOnlySpan<byte> data)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ReadOnlySpan<byte> _data = data;
    private int _offset;

    public bool AtEnd => _offset == _data.Length;
    public ReadOnlySpan<byte> Remaining => _data[_offset..];

    public bool TryReadByte(out byte value)
    {
        value = 0;
        if (_offset >= _data.Length) return false;
        value = _data[_offset++];
        return true;
    }

    public bool TryReadUInt16(out ushort value)
    {
        value = 0;
        if (_data.Length - _offset < 2) return false;
        value = BinaryPrimitives.ReadUInt16BigEndian(_data[_offset..]);
        _offset += 2;
        return true;
    }

    public bool TryReadBinary(out byte[] value)
    {
        value = [];
        if (!TryReadUInt16(out var length) || length > _data.Length - _offset) return false;
        value = _data.Slice(_offset, length).ToArray();
        _offset += length;
        return true;
    }

    public bool TryReadString(out string value)
    {
        value = string.Empty;
        if (!TryReadBinary(out var bytes)) return false;
        try
        {
            value = StrictUtf8.GetString(bytes);
            return !value.Contains('\0');
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
