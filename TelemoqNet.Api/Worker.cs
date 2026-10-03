using TelemoqNet.Api.Telnet;

namespace TelemoqNet.Api;

public class Worker : BackgroundService
{
    private readonly TelnetServer _server;

    public Worker(TelnetServer server)
    {
        _server = server;
    }

    protected override async Task ExecuteAsync(
        CancellationToken stoppingToken)
    {
        await _server.RunAsync(stoppingToken);
    }
}