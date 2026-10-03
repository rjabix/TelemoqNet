using TelemoqNet.Api.Telnet;

namespace TelemoqNet.Api;

public class Worker : BackgroundService
{
    private readonly IEnumerable<IProtocolServer> _servers;

    public Worker(IEnumerable<IProtocolServer> servers)
    {
        _servers = servers;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var tasks = _servers.Select(server => RunServerAsync(server, stoppingToken)).ToArray();
        await Task.WhenAll(tasks);
    }

    private static async Task RunServerAsync(IProtocolServer server, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await server.RunAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch { await Task.Delay(TimeSpan.FromSeconds(1), token); }
        }
    }
}