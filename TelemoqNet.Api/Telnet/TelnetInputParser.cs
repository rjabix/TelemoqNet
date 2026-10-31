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
    private readonly byte[] _readBuffer = new byte[1];
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

            if (ConsumePendingCr(value.Value))
                continue;

            var result = await ProcessByteAsync(value.Value, line, echo, cancellationToken);
            if (result is not null)
                return result;
        }

        return new TelnetReadResult(null, TooLong: true);
    }

    private bool ConsumePendingCr(byte value)
    {
        if (!_pendingCr) return false;

        _pendingCr = false;
        return value is 10 or 0;
    }

    private async Task<TelnetReadResult?> ProcessByteAsync(
        byte value,
        List<byte> line,
        bool echo,
        CancellationToken cancellationToken)
    {
        if (value == TelnetProtocol.IAC)
        {
            await ConsumeCommandAsync(cancellationToken);
            return null;
        }

        if (value is 13 or 10)
        {
            _pendingCr = value == 13;
            return CreateLineResult(line);
        }

        if (value is 8 or 127)
        {
            await RemoveLastByteAsync(line, echo, cancellationToken);
            return null;
        }

        if (value == 3)
        {
            if (echo) await WriteAsync("^C\r\n", cancellationToken);
            return new TelnetReadResult(string.Empty, Interrupt: true);
        }

        if (value == 4)
            return new TelnetReadResult(string.Empty, EndOfFile: true);

        if (value < 32 && value != 9)
            return null;
        if (line.Count == _maxLineLength)
            return new TelnetReadResult(null, TooLong: true);

        line.Add(value);
        if (echo) await _write(new[] { value }, cancellationToken);
        return null;
    }

    private static TelnetReadResult CreateLineResult(List<byte> line) =>
        new(Encoding.ASCII.GetString(line.ToArray()));

    private async Task RemoveLastByteAsync(
        List<byte> line,
        bool echo,
        CancellationToken cancellationToken)
    {
        if (line.Count == 0) return;

        line.RemoveAt(line.Count - 1);
        if (echo) await WriteAsync("\b \b", cancellationToken);
    }

    private async Task ConsumeCommandAsync(CancellationToken cancellationToken)
    {
        var command = await ReadByteAsync(cancellationToken);
        if (command is null || command == TelnetProtocol.IAC) return;

        if (IsNegotiationCommand(command.Value))
        {
            await RespondToNegotiationAsync(command.Value, cancellationToken);
            return;
        }

        if (command == TelnetProtocol.SB)
        {
            await ConsumeSubnegotiationAsync(cancellationToken);
            return;
        }

        if (command == TelnetProtocol.AYT)
            await WriteAsync("\r\n[yes]\r\n", cancellationToken);
    }

    private static bool IsNegotiationCommand(byte command) =>
        command is TelnetProtocol.WILL or TelnetProtocol.WONT or
            TelnetProtocol.DO or TelnetProtocol.DONT;

    private async Task RespondToNegotiationAsync(
        byte command,
        CancellationToken cancellationToken)
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
    }

    private async Task ConsumeSubnegotiationAsync(CancellationToken cancellationToken)
    {
        var sawIac = false;
        for (var consumed = 0; consumed < _maxPacketSize; consumed++)
        {
            var value = await ReadByteAsync(cancellationToken);
            if (value is null || sawIac && value == TelnetProtocol.SE)
                return;

            sawIac = value == TelnetProtocol.IAC && !sawIac;
        }
    }

    private async Task<byte?> ReadByteAsync(CancellationToken cancellationToken)
    {
        var read = await _stream.ReadAsync(_readBuffer, cancellationToken);
        return read == 0 ? null : _readBuffer[0];
    }

    private Task WriteAsync(string value, CancellationToken cancellationToken) =>
        _write(Encoding.ASCII.GetBytes(value), cancellationToken);
}
