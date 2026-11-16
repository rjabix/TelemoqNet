using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace TelemoqNet.IntegrationTests;

// These tests use the TCP listener, Telnet login/shell, and persisted sessions.
// Compare independently observable views instead of calling state formatters.
public sealed class DeviceStateIntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcfsConfiguration_ControlsDirectoryAndMountVisibility(bool enabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Honeypot:ProcfsEnabled"] = enabled.ToString()
        }).Build();
        await using var fixture = await TelnetFixture.StartAsync(options =>
            configuration.GetSection("Honeypot").Bind(options));
        await using var shell = await fixture.ConnectAsync();
        await shell.LoginAsync();
        Assert.Equal(enabled, (await Run(shell, "ls /")).Split('\n').Contains("proc"));
        Assert.Equal(enabled, (await Run(shell, "mount")).Contains(" on /proc ", StringComparison.Ordinal));
        Assert.Equal(enabled, (await Run(shell, "cat /etc/mtab")).Contains(" /proc ", StringComparison.Ordinal));
        if (enabled)
        {
            Assert.Equal("sh", await Run(shell, "cat /proc/self/comm"));
            Assert.Contains("42", await Run(shell, "ls /proc"));
            Assert.Equal("/proc", await Run(shell, "cd /proc; pwd"));
        }
        else
        {
            foreach (var command in new[] { "ls /proc", "cat /proc", "cat /proc/uptime", "cd /proc" })
            {
                Assert.Contains("No such file or directory", await Run(shell, command));
            }
        }

        foreach (var command in new[] { "mkdir /proc", "touch /proc", "echo x >/proc", "mkdir /tmp/../proc/new" })
        {
            Assert.Contains("Read-only file system", await Run(shell, command + " && echo UNEXPECTED"));
        }

        Assert.Equal(enabled, (await Run(shell, "ls /")).Split('\n').Contains("proc"));
        Assert.Contains("/bin/sh", await Run(shell, "ps"));
        Assert.Contains("32768", await Run(shell, "free"));
        Assert.Contains("load average:", await Run(shell, "uptime"));
        Assert.Equal("works", await Run(shell, "echo works >/tmp/file; cat /tmp/file"));
    }

    [Theory]
    [InlineData(true, 2883, "gateway-integration")]
    [InlineData(false, 1883, "second-gateway")]
    public async Task CommandsAndProcFiles_AgreeOverTelnet(bool mqttEnabled, int mqttPort, string hostname)
    {
        await using var fixture = await TelnetFixture.StartAsync(options =>
        {
            options.Hostname = hostname;
            options.Mqtt.Enabled = mqttEnabled;
            options.Mqtt.Port = mqttPort;
        });
        await using var shell = await fixture.ConnectAsync();
        await shell.LoginAsync();

        Assert.Equal(hostname, await Run(shell, "hostname"));
        Assert.Equal(hostname, await Run(shell, "cat /etc/hostname"));
        Assert.Equal(hostname, await Run(shell, "uname -n"));
        var release = await Run(shell, "uname -r");
        var build = await Run(shell, "uname -v");
        var kernel = await Run(shell, "cat /proc/version");
        Assert.StartsWith("Linux version " + release + " ", kernel);
        Assert.Contains("build@" + hostname, kernel);
        Assert.EndsWith(build, kernel);
        Assert.Contains(release, await Run(shell, "dmesg"));
        Assert.Contains(await Run(shell, "uname -m"), (await Run(shell, "cat /proc/cpuinfo")).ToLowerInvariant());
        Assert.Equal(await Run(shell, "uname -a"), await Run(shell, "busybox uname -a"));
        Assert.Contains(hostname, await Run(shell, "uname -snr"));

        var processes = (await Run(shell, "ps")).Split('\n').Skip(1)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(parts => parts[0], parts => parts[2]);
        var procPids = (await Run(shell, "ls /proc")).Split('\n')
            .Where(name => int.TryParse(name, out _)).OrderBy(name => name).ToArray();
        Assert.Equal(processes.Keys.OrderBy(name => name), procPids);
        Assert.Equal(mqttEnabled, processes.ContainsKey("31"));
        foreach (var (pid, command) in processes)
        {
            var status = Fields(await Run(shell, $"cat /proc/{pid}/status"));
            Assert.Equal(pid, status["Pid"]);
            Assert.Equal(command.Split('/').Last(), status["Name"]);
            Assert.Equal(command + '\0', await Run(shell, $"cat /proc/{pid}/cmdline"));
            Assert.Equal(status["Name"], await Run(shell, $"cat /proc/{pid}/comm"));
            Assert.Equal("0\t0\t0\t0", status["Uid"]);
            if (status["PPid"] != "0")
            {
                Assert.Contains(status["PPid"], processes.Keys);
            }
        }

        Assert.Equal(await Run(shell, "cat /proc/42/status"), await Run(shell, "busybox cat /proc/self/status"));
        var load = (await Run(shell, "cat /proc/loadavg")).Split(' ');
        Assert.Equal(processes.Count, int.Parse(load[3].Split('/')[1], CultureInfo.InvariantCulture));
        Assert.Contains(string.Join(", ", load.Take(3)), await Run(shell, "uptime"));

        var memory = Fields(await Run(shell, "cat /proc/meminfo"));
        var free = (await Run(shell, "free")).Split('\n')[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var totalKb = int.Parse(free[1], CultureInfo.InvariantCulture);
        var usedKb = int.Parse(free[2], CultureInfo.InvariantCulture);
        var freeKb = int.Parse(free[3], CultureInfo.InvariantCulture);
        Assert.Equal(totalKb, usedKb + freeKb);
        Assert.Equal(totalKb + " kB", memory["MemTotal"]);
        Assert.Equal(freeKb + " kB", memory["MemFree"]);

        var mounts = await Run(shell, "cat /proc/mounts");
        Assert.Equal(mounts, await Run(shell, "cat /etc/mtab"));
        var mountCommand = await Run(shell, "mount");
        foreach (var row in mounts.Split('\n').Select(line => line.Split(' ')))
        {
            Assert.Contains($"{row[0]} on {row[1]} type {row[2]} ({row[3]})", mountCommand);
        }

        // Root supports simulated writes; proc is explicitly read-only in all views.
        Assert.Contains("/dev/root / jffs2 rw ", mounts);
        Assert.Contains("proc /proc proc ro ", mounts);
        Assert.Contains("/dev/root", await Run(shell, "df"));
        Assert.Equal("root-write", await Run(shell, "echo root-write >/root/state; cat /root/state"));
        Assert.Equal("root", await Run(shell, "whoami"));
        Assert.Contains("uid=0(root)", await Run(shell, "id"));
        Assert.Contains("USER=root", await Run(shell, "env"));

        var sockets = (await Run(shell, "cat /proc/net/tcp")).Split('\n').Skip(1)
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        var procPorts = sockets.Select(parts => int.Parse(parts[1].Split(':')[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture)).Order().ToArray();
        Assert.All(sockets, parts => Assert.Equal("0A", parts[3]));
        var commandPorts = Regex.Matches(await Run(shell, "netstat"), @"0\.0\.0\.0:(\d+)")
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).Order().ToArray();
        Assert.Equal(commandPorts, procPorts);
        Assert.Equal(mqttEnabled ? new[] { 23, mqttPort }.Order() : new[] { 23 }.Order(), procPorts);
        var mqttConfig = await Run(shell, "cat /etc/config/mqtt");
        Assert.Contains($"enabled '{(mqttEnabled ? 1 : 0)}'", mqttConfig);
        Assert.Contains($"port '{mqttPort}'", mqttConfig);

        var ipOutput = await Run(shell, "ip addr");
        var ifconfig = await Run(shell, "ifconfig");
        var hosts = await Run(shell, "cat /etc/hosts");
        foreach (Match address in Regex.Matches(ipOutput, @"inet ([\d.]+)/"))
        {
            Assert.Contains(address.Groups[1].Value, ifconfig);
            Assert.Contains(address.Groups[1].Value, hosts);
        }

        var networkDevices = await Run(shell, "cat /proc/net/dev");
        Assert.Contains("lo:", networkDevices);
        Assert.Contains("eth0:", networkDevices);
        var route = (await Run(shell, "cat /proc/net/route")).Split('\n')[1].Split('\t');
        var word = route[1];
        var subnet = string.Join('.', Enumerable.Range(0, 4).Select(index =>
            int.Parse(word.Substring(6 - index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        var lanAddress = Regex.Match(ipOutput, @"inet ([\d.]+)/24").Groups[1].Value;
        Assert.Equal(lanAddress[..lanAddress.LastIndexOf('.')] + ".0", subnet);
        Assert.Contains(lanAddress, await Run(shell, "cat /etc/config/network"));
        Assert.Equal("eth0", route[0]);

        await shell.SendLineAsync("exit", useCrNul: true);
        var session = await fixture.WaitForSessionAsync();
        Assert.Contains(session.Commands, entry => entry.Command == "cat /proc/net/tcp" && entry.Response!.Contains("00000000:0017"));
        Assert.Contains(session.Commands, entry => entry.Command == "uname -r" && entry.Response!.Contains(release));
    }

    [Fact]
    public async Task ClockRebootAndKernelMessages_AgreeAcrossTwoTelnetSessions()
    {
        var clock = new ManualTimeProvider();
        await using var fixture = await TelnetFixture.StartAsync(timeProvider: clock);
        await using var first = await fixture.ConnectAsync();
        await first.LoginAsync();
        Assert.Equal("0.00 0.00", await Run(first, "cat /proc/uptime"));
        Assert.DoesNotContain("eth0: link up", await Run(first, "dmesg"));
        clock.Advance(TimeSpan.FromSeconds(40));
        await using var second = await fixture.ConnectAsync();
        await second.LoginAsync();
        clock.Advance(TimeSpan.FromSeconds(85));
        Assert.Equal("125.00 112.50", await Run(first, "cat /proc/uptime"));
        Assert.Equal("85.00 76.50", await Run(second, "cat /proc/uptime"));
        Assert.Contains("up 0 days, 0:02", await Run(first, "uptime"));
        Assert.Contains("up 0 days, 0:01", await Run(second, "uptime"));
        var date = await Run(first, "date");
        Assert.Equal("Mon Nov 16 12:02:05 UTC 2026", date);
        Assert.StartsWith("12:02:05", await Run(first, "uptime"));
        Assert.Contains("[    1.234000] eth0: link up", await Run(first, "dmesg"));
        await Run(first, "echo retained > /tmp/state");
        await Run(first, "reboot");
        Assert.Equal(date, await Run(first, "date"));
        Assert.Equal("0.00 0.00", await Run(first, "cat /proc/uptime"));
        Assert.DoesNotContain("eth0: link up", await Run(first, "dmesg"));
        Assert.Equal("85.00 76.50", await Run(second, "cat /proc/uptime"));
        Assert.Equal("retained", await Run(first, "cat /tmp/state"));
        Assert.Contains("No such file", await Run(second, "cat /tmp/state"));
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal("2.00 1.80", await Run(first, "cat /proc/uptime"));
        Assert.Contains("eth0: link up", await Run(first, "dmesg"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcErrorsAndEditing_AreConsistentAndPersisted(bool useCrNul)
    {
        await using var fixture = await TelnetFixture.StartAsync();
        await using var shell = await fixture.ConnectAsync();
        await shell.LoginAsync();
        var cases = new[]
        {
            ("cat /proc", "Is a directory"),
            ("busybox cat /proc/self", "Is a directory"),
            ("cat /proc/999/status", "No such file"),
            ("echo x >/tmp/../proc//self/comm", "Read-only file system"),
            ("echo x >>/proc/uptime", "Read-only file system"),
            (">/proc/meminfo", "Read-only file system"),
            ("busybox touch /proc/new", "Read-only file system"),
            ("busybox mkdir /proc/new", "Read-only file system")
        };
        foreach (var (command, expected) in cases)
        {
            var response = await Run(shell, command + " && echo UNEXPECTED; echo continued", useCrNul);
            Assert.Contains(expected, response);
            Assert.DoesNotContain("UNEXPECTED", response);
            Assert.EndsWith("continued", response);
        }

        Assert.Equal("sh", await Run(shell, "cd /proc/self; busybox cat ./comm", useCrNul));
        Assert.Equal("comm", await Run(shell, "ls ./comm", useCrNul));
        Assert.Contains("-r--r--r--", await Run(shell, "ls -l ./comm", useCrNul));
        Assert.DoesNotContain("new", await Run(shell, "ls /proc", useCrNul));
        Assert.DoesNotContain("\n/\n", "\n" + await Run(shell, "ls /", useCrNul) + "\n");
        var ordinaryFile = await Run(shell, "echo user-file >/tmp/new; ls /tmp/new; cat /tmp/new", useCrNul);
        Assert.Equal("new\nuser-file", ordinaryFile);
        await shell.SendLineAsync("exit", useCrNul);
        var session = await fixture.WaitForSessionAsync();
        foreach (var (command, expected) in cases)
        {
            var entry = Assert.Single(session.Commands, record => record.Command == command + " && echo UNEXPECTED; echo continued");
            Assert.Contains(expected, entry.Response);
            Assert.DoesNotContain("UNEXPECTED", entry.Response);
        }
    }

    [Fact]
    public async Task TruncatedProcResponse_RemainsBoundedOnWireAndInSession()
    {
        await using var fixture = await TelnetFixture.StartAsync(options => options.MaxResponseLength = 64);
        await using var shell = await fixture.ConnectAsync();
        await shell.LoginAsync();
        var response = await Run(shell, "cat /proc/cpuinfo");
        Assert.EndsWith("\n[output truncated]", response);
        Assert.Equal(64, response[..response.IndexOf("\n[output truncated]", StringComparison.Ordinal)].Length);
        Assert.Equal("alive", await Run(shell, "echo alive"));
        await shell.SendLineAsync("exit", useCrNul: true);
        var session = await fixture.WaitForSessionAsync();
        var command = Assert.Single(session.Commands, entry => entry.Command == "cat /proc/cpuinfo");
        Assert.Equal(response, Normalize(command.Response!));
    }

    [Fact]
    public async Task ProcSessionPersistenceFailure_DoesNotStopSubsequentReads()
    {
        await using var fixture = await TelnetFixture.StartAsync(failPersistence: true);
        await using (var first = await fixture.ConnectAsync())
        {
            await first.LoginAsync();
            Assert.Equal("sh", await Run(first, "cat /proc/self/comm"));
            await first.SendLineAsync("exit", useCrNul: true);
            Assert.Equal(0, await first.ReadByteOrEofAsync());
        }

        Assert.Contains(fixture.Logs, message => message.Contains("Failed to store session", StringComparison.Ordinal));
        await using var second = await fixture.ConnectAsync();
        await second.LoginAsync();
        Assert.Equal("sh", await Run(second, "cat /proc/self/comm"));
        Assert.Contains("unsupported option", await Run(second, "uname -x && echo UNEXPECTED"));
        Assert.DoesNotContain("UNEXPECTED", await Run(second, "uname -x && echo UNEXPECTED"));
    }

    private static Dictionary<string, string> Fields(string contents) => contents.Split('\n')
        .Select(line => line.Split(':', 2)).ToDictionary(parts => parts[0], parts => parts[1].Trim());

    private static async Task<string> Run(TelnetClient shell, string command, bool useCrNul = true)
    {
        await shell.SendLineAsync(command, useCrNul);
        var response = await shell.ReadUntilAsync("# ");
        return Normalize(response[..^2]);
    }

    private static string Normalize(string output) => output.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\r', '\n', ' ');

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }
}
