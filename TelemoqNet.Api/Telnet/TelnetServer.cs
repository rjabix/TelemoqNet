using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using TelemoqNet.Api.Configuration;
using TelemoqNet.Api.Emulation;
using TelemoqNet.Api.Logging;

namespace TelemoqNet.Api.Telnet;

public sealed class TelnetServer
{
    private readonly HoneypotOptions _options;
    private readonly IDeviceProfileFactory _profileFactory;
    private readonly ISessionStore _sessionStore;
    private readonly ILogger<TelnetServer> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly SemaphoreSlim _connectionLimiter;
    private readonly object _taskLock = new();
    private readonly HashSet<Task> _clientTasks = [];

    public TelnetServer(
        IOptions<HoneypotOptions> options,
        IDeviceProfileFactory profileFactory,
        ISessionStore sessionStore,
        ILogger<TelnetServer> logger,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _profileFactory = profileFactory;
        _sessionStore = sessionStore;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _connectionLimiter = new SemaphoreSlim(_options.MaxConnections);
    }

    public async Task RunAsync(
        CancellationToken cancellationToken)
    {
        var listener = new TcpListener(
            IPAddress.Any,
            _options.TelnetPort);

        listener.Start();

        _logger.LogInformation(
            "Telnet honeypot listening on port {TelnetPort}",
            _options.TelnetPort);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client =
                    await listener.AcceptTcpClientAsync(
                        cancellationToken);

                var task = HandleClientAsync(client, cancellationToken);
                lock (_taskLock) _clientTasks.Add(task);
                _ = task.ContinueWith(completed =>
                {
                    lock (_taskLock) _clientTasks.Remove(completed);
                }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Telnet listener stopping");
        }
        finally
        {
            listener.Stop();
            Task[] tasks;
            lock (_taskLock) tasks = _clientTasks.ToArray();
            await Task.WhenAll(tasks);
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        CancellationToken applicationCancellation)
    {
        var remoteEndpoint =
            client.Client.RemoteEndPoint?.ToString()
            ?? "unknown";

        if (!await _connectionLimiter.WaitAsync(
                0,
                applicationCancellation))
        {
            EmulationMetrics.RejectedConnections.Add(1);
            client.Close();
            return;
        }

        try
        {
            EmulationMetrics.Connections.Add(1);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(applicationCancellation);

            timeout.CancelAfter(
                TimeSpan.FromSeconds(
                    _options.SessionTimeoutSeconds));

            await using var stream =
                client.GetStream();

            var session = new TelnetSession(
                stream,
                remoteEndpoint,
                _profileFactory.Create(_options.Device),
                _sessionStore,
                _loggerFactory.CreateLogger<TelnetSession>(),
                _options);

            await session.RunAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (applicationCancellation.IsCancellationRequested)
        {
            _logger.LogDebug("Connection {RemoteEndpoint} cancelled", remoteEndpoint);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Error handling connection from {RemoteEndpoint}",
                remoteEndpoint);
        }
        finally
        {
            client.Dispose();
            _connectionLimiter.Release();
        }
    }
}