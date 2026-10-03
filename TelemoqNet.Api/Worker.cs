using TelemoqNet.Api.Telnet;

namespace TelemoqNet.Api;

public class Worker : BackgroundService
{
    private readonly IEnumerable<IProtocolServer> _servers;
    private readonly ILogger<Worker> _logger;

    public Worker(IEnumerable<IProtocolServer> servers, ILogger<Worker> logger)
    {
        _servers = servers;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        var tasks = _servers.Select(server => RunServerAsync(server, stoppingToken)).ToArray();
        await Task.WhenAll(tasks);
    }

    private async Task RunServerAsync(IProtocolServer server, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try { await server.RunAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{ProtocolServer} stopped unexpectedly; restarting", server.Name);
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
        }
    }
}