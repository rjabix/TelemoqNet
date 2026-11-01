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
    ILoggerFactory loggerFactory,
    IOptions<HoneypotOptions>? deviceOptions = null) : IProtocolServer
{
    private readonly MqttOptions _options = options.Value;
    private readonly SemaphoreSlim _limiter = new(options.Value.MaxConnections);
    private readonly ConcurrentDictionary<string, int> _perIp = new();
    private readonly ConcurrentDictionary<Task, byte> _sessions = new();

    public string Name => "MQTT";
    public int BoundPort { get; private set; }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            await WaitWhileDisabledAsync(cancellationToken);
            return;
        }

        var listener = new TcpListener(IPAddress.Any, _options.Port);
        listener.Start();
        BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        logger.LogInformation("MQTT honeypot listening on port {Port}", BoundPort);
        var sysPublisher = new SysTopicPublisher(
            hub,
            _options,
            loggerFactory.CreateLogger<SysTopicPublisher>(),
            deviceOptions?.Value.Hostname ?? Emulation.DeviceIdentity.Hostname);
        var sysTask = sysPublisher.RunAsync(cancellationToken);
        try
        {
            await AcceptClientsAsync(listener, cancellationToken);
        }
        finally
        {
            listener.Stop();
            await Task.WhenAll(_sessions.Keys);
            try
            {
                await sysTask;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private static Task WaitWhileDisabledAsync(CancellationToken cancellationToken) =>
        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

    private async Task AcceptClientsAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await AcceptClientAsync(listener, cancellationToken);
            if (client is not null)
            {
                await AdmitClientAsync(client, cancellationToken);
            }
        }
    }

    private async Task<TcpClient?> AcceptClientAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        try
        {
            return await listener.AcceptTcpClientAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MQTT accept failed");
            await Task.Delay(100, cancellationToken);
            return null;
        }
    }

    private async Task AdmitClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var key = GetRemoteAddress(client);
        var slotAcquired = await _limiter.WaitAsync(0, cancellationToken);
        if (!slotAcquired || !TryAcquireIp(key))
        {
            RejectClient(client, key, slotAcquired);
            return;
        }

        TrackClient(client, key, cancellationToken);
    }

    private static string GetRemoteAddress(TcpClient client) =>
        (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";

    private void RejectClient(TcpClient client, string key, bool slotAcquired)
    {
        logger.LogWarning(
            "Rejected MQTT connection from {RemoteAddress}; global or per-IP limit reached",
            key);
        client.Dispose();
        if (slotAcquired)
        {
            _limiter.Release();
        }
    }

    private void TrackClient(TcpClient client, string key, CancellationToken cancellationToken)
    {
        var task = HandleClientAsync(client, key, cancellationToken);
        _sessions.TryAdd(task, 0);
        _ = task.ContinueWith(
            _ => ReleaseClient(task, key),
            TaskScheduler.Default);
    }

    private void ReleaseClient(Task task, string key)
    {
        _sessions.TryRemove(task, out byte _);
        _perIp.AddOrUpdate(key, 0, (_, count) => Math.Max(0, count - 1));
        _limiter.Release();
    }

    private async Task HandleClientAsync(TcpClient client, string ip, CancellationToken cancellationToken)
    {
        await using var stream = client.GetStream();
        var remoteEndpoint = client.Client.RemoteEndPoint?.ToString() ?? ip;
        await using var session = new MqttSession(stream, remoteEndpoint, sessionStore, hub, Options.Create(_options),
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
            if (!_perIp.TryGetValue(key, out var current))
            {
                if (_perIp.TryAdd(key, 1))
                {
                    return true;
                }

                continue;
            }

            if (current >= _options.MaxConnectionsPerIp)
            {
                return false;
            }

            if (_perIp.TryUpdate(key, current + 1, current))
            {
                return true;
            }
        }
    }
}
