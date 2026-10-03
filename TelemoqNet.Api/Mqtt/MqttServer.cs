using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using TelemoqNet.Api.Configuration;
using TelemoqNet.Api.Logging;
using TelemoqNet.Api.Mqtt.Broker;

namespace TelemoqNet.Api.Mqtt;

public sealed class MqttServer(
    IOptions<MqttOptions> options,
    BrokerHub hub,
    ISessionStore sessionStore,
    ILogger<MqttServer> logger,
    ILoggerFactory loggerFactory) : IProtocolServer
{
    private readonly MqttOptions _options = options.Value;
    private readonly SemaphoreSlim _limiter = new(options.Value.MaxConnections);
    private readonly ConcurrentDictionary<string, int> _perIp = new();
    private readonly ConcurrentBag<Task> _sessions = [];

    public string Name => "MQTT";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return;
        }

        var listener = new TcpListener(IPAddress.Any, _options.Port);
        listener.Start();
        logger.LogInformation("MQTT honeypot listening on port {Port}", _options.Port);
        var sysPublisher = new SysTopicPublisher(
            hub,
            _options,
            loggerFactory.CreateLogger<SysTopicPublisher>());
        var sysTask = sysPublisher.RunAsync(cancellationToken);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MQTT accept failed");
                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                var endpoint = client.Client.RemoteEndPoint as IPEndPoint;
                var key = endpoint?.Address.ToString() ?? "unknown";
                var slotAcquired = await _limiter.WaitAsync(0, cancellationToken);
                if (!slotAcquired || !TryAcquireIp(key))
                {
                    logger.LogWarning(
                        "Rejected MQTT connection from {RemoteAddress}; global or per-IP limit reached",
                        key);
                    client.Dispose();
                    if (slotAcquired) _limiter.Release();
                    continue;
                }

                var task = HandleClientAsync(client, key, cancellationToken);
                _sessions.Add(task);
                _ = task.ContinueWith(_ =>
                {
                    _perIp.AddOrUpdate(key, 0, (_, count) => Math.Max(0, count - 1));
                    _limiter.Release();
                }, TaskScheduler.Default);
            }
        }
        finally
        {
            listener.Stop();
            clientTasks = _sessions.ToArray();
            await Task.WhenAll(clientTasks);
            try
            {
                await sysTask;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, string ip, CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        await using var session = new MqttSession(stream, ip, sessionStore, hub, Options.Create(_options),
            loggerFactory.CreateLogger<MqttSession>());
        try
        {
            await session.RunAsync(cancellationToken);
        }
        finally
        {
            client.Dispose();
        }
    }

    private bool TryAcquireIp(string key)
    {
        while (true)
        {
            var current = _perIp.GetValueOrDefault(key);
            if (current >= _options.MaxConnectionsPerIp) return false;
            if (current == 0 ? _perIp.TryAdd(key, 1) : _perIp.TryUpdate(key, current + 1, current))
                return true;
        }
    }

    private Task[] clientTasks = [];
}