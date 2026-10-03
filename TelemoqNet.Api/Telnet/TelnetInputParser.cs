using System.Text;

namespace TelemoqNet.Api.Telnet;

public sealed record TelnetReadResult(
    string? Line,
    bool EndOfStream = false,
    bool Interrupt = false,
    bool EndOfFile = false,
    bool TooLong = false);

public sealed class TelnetInputParser
{
    private readonly Stream _stream;
    private readonly int _maxLineLength;
    private readonly int _maxPacketSize;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> _write;
    private bool _pendingCr;

    public TelnetInputParser(
        Stream stream,
        int maxLineLength,
        int maxPacketSize,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> write)
    {
        _stream = stream;
        _maxLineLength = maxLineLength;
        _maxPacketSize = maxPacketSize;
        _write = write;
    }

    public async Task<TelnetReadResult> ReadLineAsync(
        bool echo,
        CancellationToken cancellationToken)
    {
        var line = new List<byte>();
        var packetBytes = 0;
        while (line.Count <= _maxLineLength && packetBytes++ < _maxPacketSize)
        {
            var value = await ReadByteAsync(cancellationToken);
            if (value is null) return new TelnetReadResult(null, EndOfStream: true);

            if (_pendingCr)
            {
                _pendingCr = false;
                if (value is 10 or 0) continue;
            }

            switch (value)
            {
                case TelnetProtocol.IAC:
                    await ConsumeCommandAsync(cancellationToken);
                    continue;
                case 13:
                    _pendingCr = true;
                    return new TelnetReadResult(Encoding.ASCII.GetString(line.ToArray()));
                case 10:
                    return new TelnetReadResult(Encoding.ASCII.GetString(line.ToArray()));
                case 8:
                case 127:
                    if (line.Count > 0)
                    {
                        line.RemoveAt(line.Count - 1);
                        if (echo) await WriteAsync("\b \b", cancellationToken);
                    }
                    continue;
                case 3:
                    if (echo) await WriteAsync("^C\r\n", cancellationToken);
                    return new TelnetReadResult(string.Empty, Interrupt: true);
                case 4:
                    return new TelnetReadResult(string.Empty, EndOfFile: true);
                default:
                    if (value.Value < 32 && value != 9) continue;
                    if (line.Count == _maxLineLength)
                        return new TelnetReadResult(null, TooLong: true);
                    line.Add(value.Value);
                    if (echo) await _write(new[] { value.Value }, cancellationToken);
                    break;
            }
        }

        return new TelnetReadResult(null, TooLong: true);
    }

    private async Task ConsumeCommandAsync(CancellationToken cancellationToken)
    {
        var command = await ReadByteAsync(cancellationToken);
        if (command is null || command == TelnetProtocol.IAC) return;

        if (command is TelnetProtocol.WILL or TelnetProtocol.WONT or TelnetProtocol.DO or TelnetProtocol.DONT)
        {
            var option = await ReadByteAsync(cancellationToken);
            if (option is null) return;
            var supported = option is TelnetProtocol.ECHO or TelnetProtocol.SUPPRESS_GO_AHEAD;
            var response = command switch
            {
                TelnetProtocol.DO => supported ? TelnetProtocol.WILL : TelnetProtocol.WONT,
                TelnetProtocol.DONT => TelnetProtocol.WONT,
                TelnetProtocol.WILL => supported ? TelnetProtocol.DO : TelnetProtocol.DONT,
                _ => TelnetProtocol.DONT
            };
            await _write(new[] { TelnetProtocol.IAC, response, option.Value }, cancellationToken);
            return;
        }

        if (command == TelnetProtocol.SB)
        {
            var sawIac = false;
            var consumed = 0;
            while (consumed++ < _maxPacketSize)
            {
                var value = await ReadByteAsync(cancellationToken);
                if (value is null || (sawIac && value == TelnetProtocol.SE)) return;
                sawIac = value == TelnetProtocol.IAC;
                if (!sawIac) continue;
                if (value == TelnetProtocol.IAC) sawIac = false;
            }
            return;
        }

        if (command == TelnetProtocol.AYT)
            await WriteAsync("\r\n[yes]\r\n", cancellationToken);
    }

    private async Task<byte?> ReadByteAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var read = await _stream.ReadAsync(buffer, cancellationToken);
        return read == 0 ? null : buffer[0];
    }

    private Task WriteAsync(string value, CancellationToken cancellationToken) =>
        _write(Encoding.ASCII.GetBytes(value), cancellationToken);
}
