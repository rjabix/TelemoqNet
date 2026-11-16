using System.Globalization;
using TelemoqNet.Api.Configuration;

namespace TelemoqNet.Api.Emulation;

// All facts describe the fictional gateway. Never query the host for these values.
public sealed class SimulatedLinuxState
{
    public const string KernelVersion = "2.6.36";
    public const string KernelBuild = "#1 SMP Thu Jun 12 08:23:17 UTC 2014";
    public const string Architecture = "mips";
    public const string LoadAverages = "0.00 0.01 0.05";
    public const int MemoryTotalKb = 32768;
    public const int MemoryFreeKb = 18432;
    public const int ShellPid = 42;
    public const string LoopbackAddress = "127.0.0.1";
    public const string LanAddress = "192.168.1.1";
    public const string CpuInfo = "system type\t\t: MediaTek SoC\nprocessor\t\t: 0\n" +
                                  "cpu model\t\t: MIPS 24KEc V4.12\ncpu MHz\t\t: 580.000\n";
    private readonly SimulatedMount[] _mountPoints;
    private readonly TimeProvider _time;
    private long _bootTimestamp;

    public SimulatedLinuxState(HoneypotOptions options, TimeProvider time)
    {
        _time = time;
        _bootTimestamp = time.GetTimestamp();
        Hostname = options.Hostname;
        ProcfsEnabled = options.ProcfsEnabled;
        _mountPoints = new[]
        {
            new SimulatedMount("/dev/root", "/", "jffs2", "rw"),
            new SimulatedMount("proc", "/proc", "proc", "ro")
        }.Where(mount => mount.Target != "/proc" || ProcfsEnabled).ToArray();
        Processes = new[]
        {
            new SimulatedProcess(1, 0, "init", "init"),
            new SimulatedProcess(23, 1, "telnetd", "telnetd"),
            new SimulatedProcess(31, 1, "mqttd", "mqttd"),
            new SimulatedProcess(ShellPid, 23, "sh", "/bin/sh")
        }.Where(process => process.Pid != 31 || options.Mqtt.Enabled).ToArray();
        ListenerPorts = options.Mqtt.Enabled ? [23, options.Mqtt.Port] : [23];
    }

    public string Hostname { get; }

    public bool ProcfsEnabled { get; }

    public IReadOnlyList<SimulatedProcess> Processes { get; }

    public IReadOnlyList<int> ListenerPorts { get; }

    public TimeSpan Uptime => _time.GetElapsedTime(_bootTimestamp);

    public string Version => $"Linux version {KernelVersion} (build@{Hostname}) {KernelBuild}\n";

    public string Mounts => string.Join('\n', _mountPoints.Select(mount =>
        $"{mount.Source} {mount.Target} {mount.Type} {mount.Options} 0 0")) + "\n";

    public string MountListing() => string.Join('\n', _mountPoints.Select(mount =>
        $"{mount.Source} on {mount.Target} type {mount.Type} ({mount.Options})"));

    public string MemoryInfo => FormattableString.Invariant(
        $"MemTotal:          {MemoryTotalKb} kB\nMemFree:           {MemoryFreeKb} kB\nBuffers:               0 kB\nCached:                0 kB\nSwapTotal:             0 kB\nSwapFree:              0 kB\n");

    public string ProcUptime()
    {
        var seconds = Math.Max(0, Uptime.TotalSeconds);
        return FormattableString.Invariant($"{seconds:F2} {seconds * 0.9:F2}\n");
    }

    public string ProcLoadAverage() => FormattableString.Invariant($"{LoadAverages} 1/{Processes.Count} {ShellPid}\n");

    public string FormatUptime()
    {
        var elapsed = Uptime;
        return FormattableString.Invariant(
            $" {_time.GetUtcNow():HH:mm:ss} up {elapsed.Days} days, {elapsed.Hours}:{elapsed.Minutes:00}, load average: {LoadAverages.Replace(" ", ", ", StringComparison.Ordinal)}");
    }

    public void Reboot() => _bootTimestamp = _time.GetTimestamp();

    public string Date() => _time.GetUtcNow().ToString("ddd MMM d HH:mm:ss 'UTC' yyyy", CultureInfo.InvariantCulture);

    public string KernelMessages() => $"[    0.000000] Linux version {KernelVersion}" +
        (Uptime.TotalSeconds >= 1.234 ? "\n[    1.234000] eth0: link up" : string.Empty);

    public string ProcessListing() => "PID USER       COMMAND\n" + string.Join('\n',
        Processes.Select(process => FormattableString.Invariant($"{process.Pid,5} root       {process.Command}")));

    public string Free() => FormattableString.Invariant(
        $"              total        used        free\nMem:          {MemoryTotalKb}       {MemoryTotalKb - MemoryFreeKb}       {MemoryFreeKb}");

    public string Netstat() => "Active Internet connections\nProto Local Address           State\n" +
        string.Join('\n', ListenerPorts.Select(port => FormattableString.Invariant($"TCP   0.0.0.0:{port,-16}LISTEN")));

    public string NetworkInfo(bool ip) => ip
        ? $"1: lo: <LOOPBACK,UP> mtu 65536\n    inet {LoopbackAddress}/8 scope host lo\n2: eth0: <BROADCAST,UP> mtu 1500\n    inet {LanAddress}/24 scope global eth0"
        : $"eth0      Link encap:Ethernet  HWaddr 02:00:00:00:00:01\n          inet addr:{LanAddress}  Bcast:192.168.1.255  Mask:255.255.255.0\nlo        Link encap:Local Loopback  inet addr:{LoopbackAddress}";

    public string TcpTable() =>
        "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode\n" +
        string.Join('\n', ListenerPorts.Select((port, index) => FormattableString.Invariant(
            $"{index,4}: 00000000:{port:X4} 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 {100 + index} 1 00000000 100 0 0 10 0"))) + "\n";

    // The fake profile uses little-endian IPv4 words, independent of the host's byte order.
    public string RouteTable() =>
        "Iface\tDestination\tGateway\tFlags\tRefCnt\tUse\tMetric\tMask\tMTU\tWindow\tIRTT\n" +
        $"eth0\t{LanSubnetWord()}\t00000000\t0001\t0\t0\t0\t00FFFFFF\t0\t0\t0\n";

    private static string LanSubnetWord()
    {
        var octets = LanAddress.Split('.').Select(part => byte.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        return FormattableString.Invariant($"00{octets[2]:X2}{octets[1]:X2}{octets[0]:X2}");
    }

    public string NetworkDevices() =>
        "Inter-|   Receive                                                |  Transmit\n" +
        " face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed\n" +
        "    lo: 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n" +
        "  eth0: 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n";
}

public sealed record SimulatedProcess(int Pid, int ParentPid, string Name, string Command)
{
    public string Status => string.Format(CultureInfo.InvariantCulture,
        "Name:\t{0}\nState:\t{3}\nTgid:\t{1}\nPid:\t{1}\nPPid:\t{2}\nTracerPid:\t0\nUid:\t0\t0\t0\t0\nGid:\t0\t0\t0\t0\nThreads:\t1\n",
        Name, Pid, ParentPid, Pid == SimulatedLinuxState.ShellPid ? "R (running)" : "S (sleeping)");
}

internal sealed record SimulatedMount(string Source, string Target, string Type, string Options);
