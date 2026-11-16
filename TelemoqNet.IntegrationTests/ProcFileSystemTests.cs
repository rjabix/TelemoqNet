using System.Globalization;
using Microsoft.Extensions.Options;
using TelemoqNet.Api.Configuration;
using TelemoqNet.Api.Emulation;
using Xunit;

namespace TelemoqNet.IntegrationTests;

public sealed class ProcFileSystemTests
{
    [Theory]
    [InlineData("/proc/version", "Linux version 2.6.36 (build@hg-200)")]
    [InlineData("/proc/cpuinfo", "MIPS 24KEc")]
    [InlineData("/proc/meminfo", "MemTotal:          32768 kB")]
    [InlineData("/proc/uptime", "0.00 0.00")]
    [InlineData("/proc/cmdline", "root=/dev/mtdblock3")]
    [InlineData("/proc/loadavg", "0.00 0.01 0.05 1/3 42")]
    [InlineData("/proc/mounts", "proc /proc proc ro 0 0")]
    [InlineData("/proc/net/dev", "eth0:")]
    [InlineData("/proc/net/route", "eth0\t0001A8C0\t00000000")]
    [InlineData("/proc/net/tcp", "00000000:0017 00000000:0000 0A")]
    public async Task SystemFiles_ExposeOnlyFakeProfileData(string path, string expected)
    {
        Assert.Contains(expected, await Run(Device(), "cat " + path));
    }

    [Fact]
    public async Task UptimeAndReboot_ShareClockWithoutChangingOtherSessionOrUserFiles()
    {
        var clock = new ManualTimeProvider();
        var first = Device(clock);
        var second = Device(clock);
        await Run(first, "echo kept > /tmp/note");
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pl-PL");
            clock.Advance(TimeSpan.FromSeconds(125));
            Assert.Equal("\r\n125.00 112.50", await Run(first, "cat /proc/uptime"));
            Assert.Contains("up 0 days, 0:02, load average: 0.00, 0.01, 0.05", await Run(first, "uptime"));
            await Run(first, "reboot");
            Assert.Equal("\r\n0.00 0.00", await Run(first, "cat /proc/uptime"));
            Assert.Equal("\r\n125.00 112.50", await Run(second, "cat /proc/uptime"));
            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.Equal("\r\n2.00 1.80", await Run(first, "cat /proc/uptime"));
            Assert.Equal("\r\nkept", await Run(first, "cat /tmp/note"));
            Assert.Contains("No such file", await Run(second, "cat /tmp/note"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Theory]
    [InlineData(true, 2883, "0B43", 4)]
    [InlineData(false, 1883, "075B", 3)]
    public async Task ProcessesAndListeners_MatchCommandsAndConfiguration(bool mqtt, int port, string hexPort, int count)
    {
        var device = Device(options: new HoneypotOptions
        {
            CommandLatencyMilliseconds = 0,
            Hostname = "gateway-test",
            Mqtt = new MqttOptions { Enabled = mqtt, Port = port }
        });
        var files = await Run(device, "ls /proc");
        var ps = await Run(device, "ps");
        foreach (var (pid, name) in new[] { (1, "init"), (23, "telnetd"), (42, "sh") })
        {
            Assert.Contains(pid.ToString(CultureInfo.InvariantCulture), files);
            var status = await Run(device, $"cat /proc/{pid}/status");
            Assert.Contains($"Name:\t{name}\n", status);
            Assert.Contains($"Pid:\t{pid}\n", status);
            Assert.Contains(name, ps);
            Assert.Equal("\r\n" + name, await Run(device, $"cat /proc/{pid}/comm"));
            Assert.Equal("\r\n" + (pid == 42 ? "/bin/sh" : name) + '\0', await Run(device, $"cat /proc/{pid}/cmdline"));
        }

        Assert.Equal(mqtt, ps.Contains("mqttd", StringComparison.Ordinal));
        Assert.Equal(mqtt, files.Split('\n').Contains("31"));
        Assert.Equal(mqtt, (await Run(device, "cat /proc/net/tcp")).Contains(":" + hexPort, StringComparison.Ordinal));
        Assert.Equal(mqtt, (await Run(device, "netstat")).Contains("0.0.0.0:" + port, StringComparison.Ordinal));
        Assert.Contains($"1/{count} 42", await Run(device, "cat /proc/loadavg"));
        Assert.Contains("Linux gateway-test 2.6.36", await Run(device, "uname -a"));
        Assert.Contains("build@gateway-test", await Run(device, "cat /proc/version"));
        Assert.Contains("32768       14336       18432", await Run(device, "free"));
        Assert.Contains("MemFree:           18432", await Run(device, "cat /proc/meminfo"));
        Assert.Contains("proc on /proc type proc (ro)", await Run(device, "mount"));
        Assert.Contains("192.168.1.1/24", await Run(device, "ip addr"));
        Assert.Contains("127.0.0.1", await Run(device, "ifconfig"));
        Assert.Contains("Linux version 2.6.36", await Run(device, "dmesg"));
        Assert.Equal(await Run(device, "cat /proc/mounts"), await Run(device, "cat /etc/mtab"));
        if (mqtt)
        {
            Assert.Contains("Name:\tmqttd", await Run(device, "cat /proc/31/status"));
            Assert.Equal("\r\nmqttd\0", await Run(device, "cat /proc/31/cmdline"));
            Assert.Equal("\r\nmqttd", await Run(device, "cat /proc/31/comm"));
        }
        else
        {
            Assert.Contains("No such file", await Run(device, "cat /proc/31/status"));
        }
    }

    [Fact]
    public async Task SelfView_ListsAndReadsShellProcessWithNulTerminatedCommandLine()
    {
        var device = Device();
        Assert.Equal(await Run(device, "cat /proc/42/status"), await Run(device, "cat /proc/self/status"));
        Assert.Equal("\r\n/bin/sh\0", await Run(device, "cat /proc/self/cmdline"));
        Assert.Equal("\r\ncmdline\ncomm\nstatus", await Run(device, "ls /proc/self"));
        Assert.Equal("\r\nsh", await Run(device, "cd /proc/self; cat ./comm"));
        Assert.Equal("\r\nsh", await Run(device, "cd /proc/42; cat ../self/comm"));
        Assert.Contains("-r--r--r--", await Run(device, "ls -l /proc/42"));
        Assert.DoesNotContain("-rw", await Run(device, "ls -l /proc/42"));
    }

    [Theory]
    [InlineData("cat /proc")]
    [InlineData("cat /proc/")]
    [InlineData("busybox cat /proc")]
    [InlineData("cat /proc/net")]
    public async Task ReadingDirectories_FailsAndSkipsAndChain(string command)
    {
        var response = await Run(Device(), command + " && echo unexpected; echo continued");
        Assert.Contains("Is a directory", response);
        Assert.DoesNotContain("unexpected", response);
        Assert.EndsWith("continued", response);
    }

    [Theory]
    [InlineData("echo changed > /proc/uptime")]
    [InlineData("echo changed >> /proc/uptime")]
    [InlineData("> /proc/uptime")]
    [InlineData("touch /proc/new")]
    [InlineData("touch /proc/uptime")]
    [InlineData("mkdir /proc/new")]
    [InlineData("echo changed > /proc/self/comm")]
    [InlineData("echo changed > /tmp/../proc//42/../uptime")]
    [InlineData("cd /proc/self; echo changed > ../uptime")]
    public async Task ProcMutations_FailWithoutChangingFiles(string command)
    {
        var device = Device();
        var response = await Run(device, command + " && echo unexpected");
        Assert.Contains("Read-only file system", response);
        Assert.DoesNotContain("unexpected", response);
        Assert.Equal("\r\n0.00 0.00", await Run(device, "cat /proc/uptime"));
        Assert.Equal("\r\nsh", await Run(device, "cat /proc/self/comm"));
        Assert.DoesNotContain("new", await Run(device, "ls /proc"));
        Assert.Equal("\r\nworks", await Run(device, "echo works >/tmp/note; cat /tmp/note"));
    }

    [Theory]
    [InlineData("cat /proc/999/status")]
    [InlineData("cat /proc/sys/kernel/hostname")]
    [InlineData("cd /proc/999")]
    [InlineData("ls /proc/999")]
    public async Task UnknownPaths_ReturnMissingPathErrors(string command)
    {
        Assert.Contains("No such file or directory", await Run(Device(), command));
    }

    [Fact]
    public void GeneratedFiles_ReserveCapacityAndRejectOversizedSnapshots()
    {
        var fs = new VirtualFileSystem();
        Assert.True(fs.AddDirectory("/"));
        var contents = "small";
        Assert.True(fs.AddGenerated("/generated", () => contents, 65536));
        for (var index = 0; index < 15; index++)
        {
            Assert.True(fs.Add(new VirtualFile($"/file{index}", new string('x', 65536))));
        }

        Assert.False(fs.Add(new VirtualFile("/overflow", "x")));
        Assert.False(fs.AddGenerated("/another", () => "x", 1));
        Assert.False(fs.Contains("/another"));
        Assert.True(fs.TryRead("/generated", out var snapshot));
        Assert.Equal("small", snapshot);
        var listing = fs.ListEntries("/").ToArray();
        contents = new string('x', 65537);
        Assert.False(fs.TryRead("/generated", out var oversized));
        Assert.Empty(oversized);
        Assert.Equal("small", listing.Single(file => file.Path == "/generated").Contents);
        contents = new string('x', 65536);
        Assert.True(fs.TryRead("/generated", out var largest));
        Assert.Equal(65536, largest.Length);
        Assert.False(fs.AddGenerated("/invalid", () => "", 65537));
    }

    [Fact]
    public void ProtectedNamespace_BlocksEveryMutationEntryPointAfterNormalization()
    {
        var fs = new VirtualFileSystem();
        ProcFileSystem.Register(fs, new SimulatedLinuxState(new HoneypotOptions(), new ManualTimeProvider()));
        foreach (var path in new[] { "/proc/uptime", "/tmp/../proc/new", "/proc//self/comm", "/proc/42/../new" })
        {
            Assert.False(fs.Write(path, "x", false, out var error));
            Assert.Equal("Read-only file system", error);
            Assert.False(fs.Append(path, "x"));
            Assert.False(fs.Touch(path));
            Assert.False(fs.AddDirectory(path));
            Assert.False(fs.Add(new VirtualFile(path, "replacement")));
            Assert.False(fs.AddGenerated(path, () => "replacement", 20));
        }

        Assert.True(fs.IsDirectory("/tmp/../proc/self/"));
        Assert.True(fs.Contains("/proc/self/../uptime"));
        Assert.True(fs.TryRead("/proc/uptime", out var value));
        Assert.Equal("0.00 0.00\n", value);
        Assert.False(fs.Contains("/proc/new"));
        Assert.False(fs.Add(new VirtualFile("/proc", "replacement")));
    }

    [Fact]
    public async Task ProcResponses_RespectResponseLimitAndCancellation()
    {
        var device = Device(options: new HoneypotOptions { CommandLatencyMilliseconds = 0, MaxResponseLength = 32 });
        var output = await Run(device, "cat /proc/cpuinfo");
        Assert.Contains("[output truncated]", output);
        Assert.Equal(32, output[2..output.IndexOf("\r\n[output truncated]", StringComparison.Ordinal)].Length);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => device.ExecuteCommandAsync("cat /proc/uptime", cancelled.Token));
        using var duringExecution = new CancellationTokenSource();
        var delayed = Device(options: new HoneypotOptions { CommandLatencyMilliseconds = 10000 });
        var pending = delayed.ExecuteCommandAsync("cat /proc/uptime", duringExecution.Token);
        duringExecution.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Telnet_ProcErrorsAndReadsAreCapturedAndSessionStateIsIsolated()
    {
        await using var fixture = await TelnetFixture.StartAsync();
        await using var first = await fixture.ConnectAsync();
        await using var second = await fixture.ConnectAsync();
        await first.LoginAsync();
        await second.LoginAsync();
        foreach (var (command, expected) in new[]
        {
            ("cat /proc", "cat: /proc: Is a directory"),
            ("cat /proc/self/status", "Pid:\t42"),
            ("echo x > /proc/self/comm", "Read-only file system"),
            ("echo private > /tmp/note", ""),
            ("reboot", "Restarting system.")
        })
        {
            await first.SendLineAsync(command, useCrNul: true);
            Assert.Contains(expected, await first.ReadUntilAsync("# "));
        }

        await second.SendLineAsync("cat /tmp/note", useCrNul: true);
        Assert.Contains("No such file", await second.ReadUntilAsync("# "));
        await second.SendLineAsync("cat /proc/self/comm", useCrNul: true);
        Assert.Contains("sh\r\n", await second.ReadUntilAsync("# "));
        await first.SendLineAsync("exit", useCrNul: true);
        var session = await fixture.WaitForSessionAsync();
        Assert.Contains(session.Commands, command => command.Command == "cat /proc" && command.Response!.Contains("Is a directory"));
        Assert.Contains(session.Commands, command => command.Command == "cat /proc/self/status" && command.Response!.Contains("Pid:\t42"));
        Assert.Contains(session.Commands, command => command.Command == "echo x > /proc/self/comm" && command.Response!.Contains("Read-only file system"));
    }

    private static GenericLinuxDevice Device(TimeProvider? clock = null, HoneypotOptions? options = null) =>
        new(Options.Create(options ?? new HoneypotOptions { CommandLatencyMilliseconds = 0 }), clock ?? new ManualTimeProvider());

    private static async Task<string> Run(GenericLinuxDevice device, string command) =>
        (await device.ExecuteCommandAsync(command, CancellationToken.None))!;

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }
}
