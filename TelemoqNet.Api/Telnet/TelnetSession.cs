using System.Text;
using TelemoqNet.Api.Configuration;
using TelemoqNet.Api.Emulation;
using TelemoqNet.Api.Logging;

namespace TelemoqNet.Api.Telnet;

public sealed class TelnetSession
{
    private readonly Stream _stream;
    private readonly IDeviceEmulator _device;
    private readonly ISessionStore _sessionStore;
    private readonly ILogger<TelnetSession> _logger;
    private readonly HoneypotOptions _options;
    private readonly TelnetInputParser _parser;
    private readonly HoneypotSession _session;
    private long _eventSequence;

    public TelnetSession(
        Stream stream,
        string remoteEndpoint,
        IDeviceEmulator device,
        ISessionStore sessionStore,
        ILogger<TelnetSession> logger,
        HoneypotOptions options)
    {
        _stream = stream;
        _device = device;
        _sessionStore = sessionStore;
        _logger = logger;
        _options = options;
        _parser = new TelnetInputParser(
            stream,
            options.MaxLineLength,
            options.MaxPacketSize,
            WriteBytesAsync);
        _session = new HoneypotSession
        {
            SessionId = Guid.NewGuid(),
            StartedAt = DateTimeOffset.UtcNow,
            RemoteEndpoint = remoteEndpoint,
            DeviceProfile = device.ProfileName
        };
        AddEvent("connection.opened", remoteEndpoint);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation(
                "Telnet session {SessionId} started from {RemoteEndpoint} using {DeviceProfile}",
                _session.SessionId, _session.RemoteEndpoint, _session.DeviceProfile);
            await SendAsync($"\r\n{_device.Banner}\r\n", cancellationToken);
            await NegotiateAsync(cancellationToken);

            if (!await AuthenticateAsync(cancellationToken))
            {
                _session.DisconnectReason = "authentication_failed";
                if (_options.CloseOnAuthenticationFailure)
                    await SendAsync("\r\nLogin incorrect\r\n", cancellationToken);
                return;
            }

            await SendAsync($"\r\nWelcome!\r\n{_device.Prompt}", cancellationToken);
            await ShellAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _session.DisconnectReason = "cancelled_or_timeout";
            EmulationMetrics.Timeouts.Add(1);
            _logger.LogInformation("Session {SessionId} timed out or was cancelled", _session.SessionId);
        }
        catch (Exception ex)
        {
            _session.DisconnectReason = "protocol_or_transport_error";
            AddEvent("connection.error", ex.GetType().Name);
            _logger.LogError(ex, "Error in Telnet session {SessionId}", _session.SessionId);
        }
        finally
        {
            AddEvent("connection.closed", _session.DisconnectReason);
            _session.EndedAt = DateTimeOffset.UtcNow;
            try
            {
                await _sessionStore.StoreAsync(_session, CancellationToken.None);
            }
            catch (Exception ex)
            {
                EmulationMetrics.PersistenceFailures.Add(1);
                _logger.LogError(ex, "Failed to store session {SessionId}", _session.SessionId);
            }
        }
    }

    private async Task NegotiateAsync(CancellationToken cancellationToken)
    {
        await WriteBytesAsync(new[]
        {
            TelnetProtocol.IAC, TelnetProtocol.WILL, TelnetProtocol.SUPPRESS_GO_AHEAD,
            TelnetProtocol.IAC, TelnetProtocol.WILL, TelnetProtocol.ECHO
        }, cancellationToken);
        AddEvent("protocol.negotiation", "echo,suppress-go-ahead");
    }

    private async Task<bool> AuthenticateAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= _options.MaxAuthenticationAttempts; attempt++)
        {
            await SendAsync("login: ", cancellationToken);
            var username = await _parser.ReadLineAsync(true, cancellationToken);
            if (username.EndOfStream) return false;
            if (username.TooLong)
            {
                _session.AuthenticationFailureReason = "line_too_long";
                await SendAsync("\r\nInput too long\r\n", cancellationToken);
                continue;
            }

            await SendAsync("\r\nPassword: ", cancellationToken);
            var password = await _parser.ReadLineAsync(false, cancellationToken);
            if (password.EndOfStream) return false;
            if (password.TooLong)
            {
                _session.AuthenticationFailureReason = "line_too_long";
                continue;
            }

            if (username.Interrupt || password.Interrupt || username.EndOfFile || password.EndOfFile)
            {
                _session.AuthenticationFailureReason = "client_interrupt";
                return false;
            }

            var success = _device.Authenticate(username.Line ?? string.Empty, password.Line ?? string.Empty);
            var reason = success ? null : "invalid_credentials";
            _session.Username = username.Line;
            _session.AuthenticationSucceeded = success;
            _session.AuthenticationFailureReason = reason;
            EmulationMetrics.AuthenticationAttempts.Add(1);
            _session.LoginAttempts.Add(new LoginAttempt(
                username.Line ?? string.Empty,
                _options.CapturePasswords ? password.Line : null,
                DateTimeOffset.UtcNow,
                success,
                reason));
            AddEvent("authentication.attempt", success ? "success" : reason);
            _logger.LogInformation(
                "Login attempt {SessionId}: user={Username}, success={Success}, attempt={Attempt}",
                _session.SessionId, username.Line, success, attempt);

            if (success) return true;
            if (attempt < _options.MaxAuthenticationAttempts)
                await Task.Delay(_options.AuthenticationRetryDelayMilliseconds, cancellationToken);
            if (_options.CloseOnAuthenticationFailure) break;
        }

        return false;
    }

    private async Task ShellAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await _parser.ReadLineAsync(true, cancellationToken);
            if (result.EndOfStream)
            {
                _session.DisconnectReason = "client_closed";
                break;
            }

            if (result.TooLong)
            {
                AddEvent("protocol.error", "line_too_long");
                EmulationMetrics.ProtocolErrors.Add(1);
                await SendAsync("\r\nsh: input line too long\r\n" + _device.Prompt, cancellationToken);
                continue;
            }

            if (result.Interrupt)
            {
                await SendAsync(_device.Prompt, cancellationToken);
                continue;
            }

            if (result.EndOfFile)
            {
                _session.DisconnectReason = "client_eof";
                break;
            }

            var command = (result.Line ?? string.Empty).Trim();
            if (command.Length == 0)
            {
                await SendAsync(_device.Prompt, cancellationToken);
                continue;
            }

            _logger.LogInformation("Session {SessionId} command: {Command}", _session.SessionId, command);
            AddEvent("command.received", command);
            EmulationMetrics.Commands.Add(1);
            var response = await _device.ExecuteCommandAsync(command, cancellationToken);
            _session.Commands.Add(new CommandEvent(command, DateTimeOffset.UtcNow, response));
            AddEvent("command.completed", response is null ? "session_closed" : "response");
            if (response is null)
            {
                _session.DisconnectReason = command is "exit" or "logout" ? "client_command" : "device_closed";
                break;
            }

            await SendAsync(
                NormalizeLineEndings(response) + _device.Prompt,
                cancellationToken);
        }
    }

    private void AddEvent(string type, string? detail) =>
        _session.Events.Add(new SessionEvent(++_eventSequence, type, DateTimeOffset.UtcNow, detail));

    private Task SendAsync(string value, CancellationToken cancellationToken) =>
        WriteBytesAsync(Encoding.ASCII.GetBytes(value), cancellationToken);

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n") +
        (value.EndsWith('\n') ? string.Empty : "\r\n");

    private async Task WriteBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await _stream.WriteAsync(bytes, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }
}