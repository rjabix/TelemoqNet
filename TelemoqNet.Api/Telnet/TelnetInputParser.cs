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
    private int _bytesRead;
    private readonly OptionState[] _localOptions = new OptionState[256];
    private readonly OptionState[] _remoteOptions = new OptionState[256];

    private enum OptionState { Disabled, Offered, Enabled }

    public async Task NegotiateAsync(CancellationToken cancellationToken)
    {
        // Offers happen only at startup, so no queued option reversals are needed.
        foreach (var option in new[] { TelnetProtocol.SUPPRESS_GO_AHEAD, TelnetProtocol.ECHO })
        {
            if (_localOptions[option] != OptionState.Disabled) continue;
            _localOptions[option] = OptionState.Offered;
            await _write(new[] { TelnetProtocol.IAC, TelnetProtocol.WILL, option }, cancellationToken);
        }
    }

    private bool CanEcho(bool requested) =>
        requested && _localOptions[TelnetProtocol.ECHO] == OptionState.Enabled;

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
        _bytesRead = 0;
        var tooLong = false;
        while (true)
        {
            var value = await ReadByteAsync(cancellationToken);
            if (value is null)
                return new TelnetReadResult(null, EndOfStream: true);

            var data = value.Value;
            if (data == TelnetProtocol.IAC)
            {
                var command = await ReadRequiredByteAsync(cancellationToken);
                if (command != TelnetProtocol.IAC)
                {
                    if (command == TelnetProtocol.IP)
                    {
                        if (!tooLong)
                        {
                            if (CanEcho(echo)) await WriteAsync("^C\r\n", cancellationToken);
                            return new TelnetReadResult(string.Empty, Interrupt: true);
                        }
                    }
                    else if (command == TelnetProtocol.EC && !tooLong)
                        await RemoveLastByteAsync(line, CanEcho(echo), cancellationToken);
                    else if (command == TelnetProtocol.EL && !tooLong)
                    {
                        while (line.Count > 0)
                            await RemoveLastByteAsync(line, CanEcho(echo), cancellationToken);
                    }
                    else
                        await ConsumeCommandAsync(command, cancellationToken);
                    continue;
                }
            }

            if (ConsumePendingCr(data)) continue;
            if (tooLong)
            {
                // Discard data, but still parse Telnet commands while finding the boundary.
                if (data is not (13 or 10)) continue;
                _pendingCr = data == 13;
                return new TelnetReadResult(null, TooLong: true);
            }

            var result = await ProcessByteAsync(data, line, CanEcho(echo), cancellationToken);
            if (result?.TooLong == true)
                tooLong = true;
            else if (result is not null)
                return result;
        }
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
        if (echo)
            await _write(value == TelnetProtocol.IAC
                ? new[] { value, value } : new[] { value }, cancellationToken);
        return null;
    }

    private static TelnetReadResult CreateLineResult(List<byte> line) =>
        new(Encoding.Latin1.GetString(line.ToArray()));

    private async Task RemoveLastByteAsync(
        List<byte> line,
        bool echo,
        CancellationToken cancellationToken)
    {
        if (line.Count == 0) return;

        line.RemoveAt(line.Count - 1);
        if (echo) await WriteAsync("\b \b", cancellationToken);
    }

    private async Task ConsumeCommandAsync(byte command, CancellationToken cancellationToken)
    {
        if (IsNegotiationCommand(command))
        {
            await RespondToNegotiationAsync(command, cancellationToken);
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
        var option = await ReadRequiredByteAsync(cancellationToken);
        var local = command is TelnetProtocol.DO or TelnetProtocol.DONT;
        var states = local ? _localOptions : _remoteOptions;
        var previous = states[option];
        var enable = command is TelnetProtocol.DO or TelnetProtocol.WILL;
        // Client-side echo is deliberately rejected to avoid double echo/echo loops.
        var supported = option == TelnetProtocol.SUPPRESS_GO_AHEAD ||
                        local && option == TelnetProtocol.ECHO;
        byte? response = null;
        if (enable)
        {
            if (!supported)
                response = local ? TelnetProtocol.WONT : TelnetProtocol.DONT;
            else
            {
                states[option] = OptionState.Enabled;
                if (previous == OptionState.Disabled)
                    response = local ? TelnetProtocol.WILL : TelnetProtocol.DO;
            }
        }
        else
        {
            states[option] = OptionState.Disabled;
            // A negative acknowledgment of our startup offer needs no reply.
            if (previous == OptionState.Enabled)
                response = local ? TelnetProtocol.WONT : TelnetProtocol.DONT;
        }

        if (response is not null)
            await _write(new[] { TelnetProtocol.IAC, response.Value, option }, cancellationToken);
    }

    private async Task ConsumeSubnegotiationAsync(CancellationToken cancellationToken)
    {
        var sawIac = false;
        while (true)
        {
            var value = await ReadRequiredByteAsync(cancellationToken);
            if (sawIac && value == TelnetProtocol.SE) return;
            sawIac = value == TelnetProtocol.IAC && !sawIac;
        }
    }

    private async Task<byte> ReadRequiredByteAsync(CancellationToken cancellationToken) =>
        await ReadByteAsync(cancellationToken) ??
        throw new EndOfStreamException("Truncated Telnet command or subnegotiation.");

    private async Task<byte?> ReadByteAsync(CancellationToken cancellationToken)
    {
        var read = await _stream.ReadAsync(_readBuffer, cancellationToken);
        if (read == 0) return null;
        if (++_bytesRead > _maxPacketSize)
            throw new InvalidDataException("Telnet input byte limit exceeded before a line boundary.");
        return _readBuffer[0];
    }

    private Task WriteAsync(string value, CancellationToken cancellationToken) =>
        _write(Encoding.ASCII.GetBytes(value), cancellationToken);
}
