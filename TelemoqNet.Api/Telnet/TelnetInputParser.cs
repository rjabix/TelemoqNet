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
    private const byte NullByte = 0; // ASCII NUL (\0): padding after CR in a Telnet CR-NUL pair.
    private const byte EndOfText = 3; // ASCII ETX (Ctrl+C): requests an input interrupt.
    private const byte EndOfTransmission = 4; // ASCII EOT (Ctrl+D): requests end of input.
    private const byte Backspace = 8; // ASCII BS (\b): commonly sent by the Backspace key.
    private const byte HorizontalTab = 9; // ASCII HT (\t): horizontal tab, preserved in input.
    private const byte LineFeed = 10; // ASCII LF (\n): moves to the next line.
    private const byte CarriageReturn = 13; // ASCII CR (\r): returns to the start of the line.
    private const byte Space = 32; // ASCII space: the first printable ASCII character.
    private const byte Delete = 127; // ASCII DEL: another common encoding of the Backspace key.

    private readonly Stream _stream;
    private readonly int _maxLineLength;
    private readonly int _maxPacketSize;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> _write;
    private readonly byte[] _readBuffer = new byte[1];
    private bool _pendingCr;
    private int _bytesRead;
    private readonly OptionState[] _localOptions = new OptionState[256];
    private readonly OptionState[] _remoteOptions = new OptionState[256];

    private enum OptionState
    {
        Disabled, Offered, Enabled
    }

    public async Task NegotiateAsync(CancellationToken cancellationToken)
    {
        // Offers happen only at startup, so no queued option reversals are needed.
        foreach (var option in new[] { TelnetProtocol.SUPPRESS_GO_AHEAD, TelnetProtocol.ECHO })
        {
            // Skip options already offered or enabled so repeated calls do not resend offers.
            if (_localOptions[option] != OptionState.Disabled)
            {
                continue;
            }

            _localOptions[option] = OptionState.Offered;
            await _write(new[] { TelnetProtocol.IAC, TelnetProtocol.WILL, option }, cancellationToken);
        }
    }

    // Echo only when the caller permits it (e.g. not for passwords) and the client negotiated it.
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
            // No byte means the peer closed its sending side; do not return an incomplete line.
            if (value is null)
            {
                return new TelnetReadResult(null, EndOfStream: true);
            }

            var data = value.Value;
            // IAC introduces Telnet protocol bytes, which must be handled separately from text.
            if (data == TelnetProtocol.IAC)
            {
                var command = await ReadRequiredByteAsync(cancellationToken);
                // A doubled IAC is one literal data byte; any other second byte is a command.
                if (command != TelnetProtocol.IAC)
                {
                    // IP is Telnet's protocol-level equivalent of an input interrupt.
                    if (command == TelnetProtocol.IP)
                    {
                        // Once over the line limit, keep draining to a line ending instead of accepting input.
                        if (!tooLong)
                        {
                            // Display the interrupt marker only when echo is permitted and negotiated.
                            if (CanEcho(echo))
                            {
                                await WriteAsync("^C\r\n", cancellationToken);
                            }

                            return new TelnetReadResult(string.Empty, Interrupt: true);
                        }
                    }
                    else if (command == TelnetProtocol.EC && !tooLong)
                    {
                        // EC erases one input byte; editing must not make an oversized line acceptable again.
                        await RemoveLastByteAsync(line, CanEcho(echo), cancellationToken);
                    }
                    else if (command == TelnetProtocol.EL && !tooLong)
                    {
                        // EL erases the input line, unless it has already been rejected for exceeding the limit.
                        // Remove each stored byte, including its visible echo, until the line is empty.
                        while (line.Count > 0)
                        {
                            await RemoveLastByteAsync(line, CanEcho(echo), cancellationToken);
                        }
                    }
                    else
                    {
                        await ConsumeCommandAsync(command, cancellationToken);
                    }

                    continue;
                }
            }

            // CR already ended the previous line; swallow its optional LF or NUL continuation.
            if (ConsumePendingCr(data))
            {
                continue;
            }

            // An oversized line must be fully drained so its suffix cannot become a new command.
            if (tooLong)
            {
                // Discard data, but still parse Telnet commands while finding the boundary.
                // Only CR or LF ends the rejected line; discard all other data bytes.
                if (data is not (CarriageReturn or LineFeed))
                {
                    continue;
                }

                // CR may be followed by LF or NUL, which belongs to this same line ending.
                _pendingCr = data == CarriageReturn;
                return new TelnetReadResult(null, TooLong: true);
            }

            var result = await ProcessByteAsync(data, line, CanEcho(echo), cancellationToken);
            // Stop collecting after overflow, but continue reading until the rejected line ends.
            if (result?.TooLong == true)
            {
                tooLong = true;
            }
            else if (result is not null)
            {
                // A completed line, interrupt, or EOF is ready to return; null means keep reading.
                return result;
            }
        }
    }

    private bool ConsumePendingCr(byte value)
    {
        // A continuation byte is special only immediately after a previously consumed CR.
        if (!_pendingCr)
        {
            return false;
        }

        _pendingCr = false;
        // Telnet CR-LF and CR-NUL pairs must not produce a second line or append NUL to input.
        return value is LineFeed or NullByte;
    }

    private async Task<TelnetReadResult?> ProcessByteAsync(
        byte value,
        List<byte> line,
        bool echo,
        CancellationToken cancellationToken)
    {
        // Accept either common line-ending byte; the CR continuation is consumed on the next read.
        if (value is CarriageReturn or LineFeed)
        {
            // Only CR can start a CR-LF or CR-NUL pair.
            _pendingCr = value == CarriageReturn;
            return CreateLineResult(line);
        }

        // Terminals send either BS or DEL for Backspace, so both edit the buffered input.
        if (value is Backspace or Delete)
        {
            await RemoveLastByteAsync(line, echo, cancellationToken);
            return null;
        }

        // Interpret Ctrl+C as an interrupt rather than passing the control byte to the shell emulator.
        if (value == EndOfText)
        {
            // Show the conventional Ctrl+C marker only when this input is allowed to echo.
            if (echo)
            {
                await WriteAsync("^C\r\n", cancellationToken);
            }

            return new TelnetReadResult(string.Empty, Interrupt: true);
        }

        // Interpret Ctrl+D as end of input so the session can handle it without treating it as text.
        if (value == EndOfTransmission)
        {
            return new TelnetReadResult(string.Empty, EndOfFile: true);
        }

        // ASCII 0-31 are control codes. Earlier branches handle supported controls;
        // ignore the rest, while preserving Tab as valid whitespace in command input.
        if (value < Space && value != HorizontalTab)
        {
            return null;
        }

        // Reject the next data byte once the buffer is full, keeping captured input bounded.
        if (line.Count == _maxLineLength)
        {
            return new TelnetReadResult(null, TooLong: true);
        }

        line.Add(value);
        // Reflect accepted input only when echo is allowed and negotiated by the caller.
        if (echo)
        {
            // Escape a literal IAC by doubling it so the client does not parse it as a command.
            await _write(value == TelnetProtocol.IAC
                ? new[] { value, value } : new[] { value }, cancellationToken);
        }

        return null;
    }

    private static TelnetReadResult CreateLineResult(List<byte> line) =>
        new(Encoding.Latin1.GetString(line.ToArray()));

    private async Task RemoveLastByteAsync(
        List<byte> line,
        bool echo,
        CancellationToken cancellationToken)
    {
        // Backspace at the start of a line has nothing to remove or erase on screen.
        if (line.Count == 0)
        {
            return;
        }

        line.RemoveAt(line.Count - 1);
        // Move left, overwrite with a space, then move left again to visually erase the echoed byte.
        if (echo)
        {
            await WriteAsync("\b \b", cancellationToken);
        }
    }

    private async Task ConsumeCommandAsync(byte command, CancellationToken cancellationToken)
    {
        // WILL/WONT/DO/DONT carry an option byte and need the negotiation state machine.
        if (IsNegotiationCommand(command))
        {
            await RespondToNegotiationAsync(command, cancellationToken);
            return;
        }

        // Skip subnegotiation through IAC SE so option payload bytes cannot become command input.
        if (command == TelnetProtocol.SB)
        {
            await ConsumeSubnegotiationAsync(cancellationToken);
            return;
        }

        // AYT asks whether the server is responsive; provide a short visible acknowledgment.
        if (command == TelnetProtocol.AYT)
        {
            await WriteAsync("\r\n[yes]\r\n", cancellationToken);
        }
    }

    // These four commands negotiate an option; other Telnet commands have different framing.
    private static bool IsNegotiationCommand(byte command) =>
        command is TelnetProtocol.WILL or TelnetProtocol.WONT or
            TelnetProtocol.DO or TelnetProtocol.DONT;

    private async Task RespondToNegotiationAsync(
        byte command,
        CancellationToken cancellationToken)
    {
        var option = await ReadRequiredByteAsync(cancellationToken);
        // DO/DONT concern our behavior; WILL/WONT concern the peer's behavior.
        var local = command is TelnetProtocol.DO or TelnetProtocol.DONT;
        var states = local ? _localOptions : _remoteOptions;
        var previous = states[option];
        // DO/WILL request enabling an option; DONT/WONT request disabling it.
        var enable = command is TelnetProtocol.DO or TelnetProtocol.WILL;
        // Client-side echo is deliberately rejected to avoid double echo/echo loops.
        var supported = option == TelnetProtocol.SUPPRESS_GO_AHEAD ||
                        local && option == TelnetProtocol.ECHO;
        byte? response = null;
        // Positive requests require either accepting a supported option or explicitly refusing it.
        if (enable)
        {
            // Refuse unsupported options in the correct direction instead of silently accepting them.
            if (!supported)
            {
                response = local ? TelnetProtocol.WONT : TelnetProtocol.DONT;
            }
            else
            {
                states[option] = OptionState.Enabled;
                // Reply to a new request, but do not echo acknowledgments or duplicate requests endlessly.
                if (previous == OptionState.Disabled)
                {
                    response = local ? TelnetProtocol.WILL : TelnetProtocol.DO;
                }
            }
        }
        else
        {
            states[option] = OptionState.Disabled;
            // A negative acknowledgment of our startup offer needs no reply.
            // Confirm disabling an active option; an already-disabled or merely offered option needs no reply.
            if (previous == OptionState.Enabled)
            {
                response = local ? TelnetProtocol.WONT : TelnetProtocol.DONT;
            }
        }

        // Send only necessary replies to avoid negotiation acknowledgment loops.
        if (response is not null)
        {
            await _write(new[] { TelnetProtocol.IAC, response.Value, option }, cancellationToken);
        }
    }

    private async Task ConsumeSubnegotiationAsync(CancellationToken cancellationToken)
    {
        var sawIac = false;
        while (true)
        {
            var value = await ReadRequiredByteAsync(cancellationToken);
            // Only IAC followed by SE terminates subnegotiation; a standalone SE is payload.
            if (sawIac && value == TelnetProtocol.SE)
            {
                return;
            }

            // Two IAC bytes escape one literal IAC, so that pair cannot prefix a terminating SE.
            sawIac = value == TelnetProtocol.IAC && !sawIac;
        }
    }

    private async Task<byte> ReadRequiredByteAsync(CancellationToken cancellationToken) =>
        await ReadByteAsync(cancellationToken) ??
        throw new EndOfStreamException("Truncated Telnet command or subnegotiation.");

    private async Task<byte?> ReadByteAsync(CancellationToken cancellationToken)
    {
        var read = await _stream.ReadAsync(_readBuffer, cancellationToken);
        // A zero-byte read into this nonempty buffer indicates the stream has ended.
        if (read == 0)
        {
            return null;
        }

        // Count every wire byte, including Telnet commands, to bound input even without a line ending.
        if (++_bytesRead > _maxPacketSize)
        {
            throw new InvalidDataException("Telnet input byte limit exceeded before a line boundary.");
        }

        return _readBuffer[0];
    }

    private Task WriteAsync(string value, CancellationToken cancellationToken) =>
        _write(Encoding.ASCII.GetBytes(value), cancellationToken);
}
