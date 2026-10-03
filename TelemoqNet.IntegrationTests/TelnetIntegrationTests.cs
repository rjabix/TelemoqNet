using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TelemoqNet.Api.Configuration;
using TelemoqNet.Api.Emulation;
using TelemoqNet.Api.Logging;
using TelemoqNet.Api.Telnet;
using Xunit;

namespace TelemoqNet.IntegrationTests;

public sealed class TelnetIntegrationTests
{
    [Fact]
    public async Task LoginAndCommands_AreCapturedAndLogged()
    {
        await using var fixture = await TelnetFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();

        await client.ReadUntilAsync("login: ");
        await client.SendLineAsync("root", useCrNul: true);
        await client.ReadUntilAsync("Password: ");
        await client.SendLineAsync("root", useCrNul: true);
        await client.ReadUntilAsync("# ");

        await client.SendLineAsync("whoami", useCrNul: true);
        var response = await client.ReadUntilAsync("root\r\n# ");
        Assert.Contains("root", response);

        await client.SendLineAsync("exit", useCrNul: true);
        await fixture.WaitForSessionAsync();

        var session = Assert.Single(fixture.Sessions);
        Assert.True(session.AuthenticationSucceeded);
        Assert.Contains(session.Commands, command => command.Command == "whoami");
        Assert.Contains(
            fixture.Logs,
            log => log.Contains("command: whoami", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BusyBoxProbe_ReturnsTerseAppletNotFoundResponse()
    {
        await using var fixture = await TelnetFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();

        await client.LoginAsync();
        await client.SendLineAsync("/bin/busybox ECCHI", useCrNul: true);

        var response = await client.ReadUntilAsync("ECCHI: applet not found\r\n# ");
        Assert.Contains("ECCHI: applet not found", response);
        Assert.DoesNotContain("simulated", response, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VirtualFilesystemAndWorkingDirectory_AreSessionLocal()
    {
        await using var fixture = await TelnetFixture.StartAsync();
        await using var first = await fixture.ConnectAsync();
        await using var second = await fixture.ConnectAsync();

        await first.LoginAsync();
        await second.LoginAsync();

        await first.SendLineAsync("mkdir evidence", useCrNul: true);
        await first.ReadUntilAsync("# ");
        await first.SendLineAsync("cd evidence", useCrNul: true);
        await first.ReadUntilAsync("# ");
        await first.SendLineAsync("echo marker >> note.txt", useCrNul: true);
        await first.ReadUntilAsync("# ");
        await first.SendLineAsync("cat note.txt", useCrNul: true);
        var firstResponse = await first.ReadUntilAsync("marker\r\n# ");

        await second.SendLineAsync("pwd", useCrNul: true);
        var secondResponse = await second.ReadUntilAsync("/root\r\n# ");
        await second.SendLineAsync("cat /root/evidence/note.txt", useCrNul: true);
        var missingResponse =
            await second.ReadUntilAsync("No such file or directory\r\n# ");

        Assert.Contains("marker", firstResponse);
        Assert.Contains("/root", secondResponse);
        Assert.Contains("No such file or directory", missingResponse);
    }

    [Fact]
    public async Task CdIntoFile_ReturnsNotADirectory()
    {
        await using var fixture = await TelnetFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();

        await client.LoginAsync();
        await client.SendLineAsync("cd /etc/hostname", useCrNul: true);

        var response = await client.ReadUntilAsync(
            "Not a directory\r\n# ");
        Assert.Contains("Not a directory", response);
    }

    [Fact]
    public async Task FailedAuthentication_IsCapturedWithoutLoggingPassword()
    {
        await using var fixture = await TelnetFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();

        await client.ReadUntilAsync("login: ");
        await client.SendLineAsync("attacker", useCrNul: false);
        await client.ReadUntilAsync("Password: ");
        await client.SendLineAsync("secret-password", useCrNul: false);

        var response = await client.ReadUntilAsync("Login incorrect");
        Assert.Contains("Login incorrect", response);

        var session = await fixture.WaitForSessionAsync();
        Assert.False(session.AuthenticationSucceeded);
        Assert.Equal("attacker", session.Username);
        Assert.DoesNotContain(
            fixture.Logs,
            log => log.Contains("secret-password", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OversizedInput_IsRejectedWithoutCrashingSession()
    {
        await using var fixture = await TelnetFixture.StartAsync(
            options => options.MaxLineLength = 16);
        await using var client = await fixture.ConnectAsync();

        await client.LoginAsync();
        await client.SendLineAsync(new string('x', 64), useCrNul: true);

        var response = await client.ReadUntilAsync("input line too long");
        Assert.Contains("input line too long", response);

        await client.SendLineAsync("true", useCrNul: true);
        Assert.Contains("# ", await client.ReadUntilAsync("# "));

        await client.SendLineAsync("exit", useCrNul: true);
        var session = await fixture.WaitForSessionAsync();
        Assert.Contains(
            session.Events,
            sessionEvent => sessionEvent.Type == "protocol.error"
                && sessionEvent.Detail == "line_too_long");
    }

    [Fact]
    public async Task ConcurrentSessions_DoNotShareCommandsOrState()
    {
        await using var fixture = await TelnetFixture.StartAsync();

        var clients = await Task.WhenAll(
            fixture.ConnectAsync(),
            fixture.ConnectAsync(),
            fixture.ConnectAsync());

        try
        {
            await Task.WhenAll(clients.Select(client => client.LoginAsync()));
            await Task.WhenAll(clients.Select((client, index) =>
                client.SendLineAsync($"echo client-{index}", useCrNul: true)));

            var responses = await Task.WhenAll(
                clients.Select((client, index) =>
                    client.ReadUntilAsync($"client-{index}\r\n# ")));

            Assert.All(responses, response => Assert.Contains("client-", response));
        }
        finally
        {
            foreach (var client in clients)
                await client.DisposeAsync();
        }
    }
}

internal sealed class TelnetFixture : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly Task _serverTask;
    private readonly int _port;
    private readonly ILoggerFactory _loggerFactory;

    private TelnetFixture(
        int port,
        Task serverTask,
        CapturingSessionStore sessionStore,
        CapturingLoggerProvider loggerProvider,
        ILoggerFactory loggerFactory,
        CancellationTokenSource stop)
    {
        _port = port;
        _serverTask = serverTask;
        _loggerFactory = loggerFactory;
        _stop = stop;
        Sessions = sessionStore.Sessions;
        Logs = loggerProvider.Messages;
    }

    public ConcurrentBag<HoneypotSession> Sessions { get; }
    public ConcurrentBag<string> Logs { get; }

    public static async Task<TelnetFixture> StartAsync(
        Action<HoneypotOptions>? configure = null)
    {
        var options = new HoneypotOptions
        {
            TelnetPort = GetFreePort(),
            CommandLatencyMilliseconds = 0,
            AuthenticationRetryDelayMilliseconds = 0
        };
        configure?.Invoke(options);

        var profileFactory = new TestProfileFactory(options);
        var sessionStore = new CapturingSessionStore();
        var loggerProvider = new CapturingLoggerProvider();
        var loggerFactory = LoggerFactory.Create(builder =>
            builder.AddProvider(loggerProvider));

        var server = new TelnetServer(
            Options.Create(options),
            profileFactory,
            sessionStore,
            loggerFactory.CreateLogger<TelnetServer>(),
            loggerFactory);

        var stop = new CancellationTokenSource();
        var serverTask = server.RunAsync(stop.Token);
        var fixture = new TelnetFixture(
            options.TelnetPort,
            serverTask,
            sessionStore,
            loggerProvider,
            loggerFactory,
            stop);
        return fixture;
    }

    public async Task<TelnetClient> ConnectAsync()
    {
        var client = new TelnetClient();
        await client.ConnectAsync(_port);
        return client;
    }

    public async Task<HoneypotSession> WaitForSessionAsync()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var session = Sessions.FirstOrDefault();
            if (session is not null)
                return session;
            await Task.Delay(10);
        }

        throw new TimeoutException("The Telnet session was not persisted.");
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            await _serverTask;
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
        _loggerFactory.Dispose();
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

}

internal sealed class TelnetClient : IAsyncDisposable
{
    private readonly TcpClient _client = new();
    private NetworkStream _stream = null!;

    public async Task ConnectAsync(int port)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                await _client.ConnectAsync(IPAddress.Loopback, port);
                break;
            }
            catch (SocketException) when (attempt < 99)
            {
                await Task.Delay(10);
            }
        }

        _stream = _client.GetStream();
    }

    public async Task LoginAsync()
    {
        await ReadUntilAsync("login: ");
        await SendLineAsync("root", useCrNul: true);
        await ReadUntilAsync("Password: ");
        await SendLineAsync("root", useCrNul: true);
        await ReadUntilAsync("# ");
    }

    public async Task SendLineAsync(string value, bool useCrNul)
    {
        var suffix = useCrNul ? "\r\0" : "\r\n";
        var bytes = Encoding.ASCII.GetBytes(value + suffix);
        await _stream.WriteAsync(bytes);
        await _stream.FlushAsync();
    }

    public async Task<string> ReadUntilAsync(
        string expected,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        var result = new StringBuilder();
        var buffer = new byte[256];
        while (!result.ToString().Contains(expected, StringComparison.Ordinal))
        {
            var read = await _stream.ReadAsync(buffer, timeout.Token);
            if (read == 0)
                throw new EndOfStreamException(
                    $"Connection closed before receiving '{expected}'.");
            result.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        return result.ToString();
    }

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class TestProfileFactory(HoneypotOptions options)
    : IDeviceProfileFactory
{
    public IDeviceEmulator Create(string profileName) =>
        new GenericLinuxDevice(Options.Create(options));
}

internal sealed class CapturingSessionStore : ISessionStore
{
    public ConcurrentBag<HoneypotSession> Sessions { get; } = [];

    public Task StoreAsync(
        HoneypotSession session,
        CancellationToken cancellationToken)
    {
        Sessions.Add(session);
        return Task.CompletedTask;
    }
}

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentBag<string> Messages { get; } = [];

    public ILogger CreateLogger(string categoryName) =>
        new CapturingLogger(Messages, categoryName);

    public void Dispose()
    {
    }
}

internal sealed class CapturingLogger(
    ConcurrentBag<string> messages,
    string categoryName) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull =>
        NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        messages.Add($"{categoryName}: {formatter(state, exception)}");
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
