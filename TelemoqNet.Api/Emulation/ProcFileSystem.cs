using System.Globalization;

namespace TelemoqNet.Api.Emulation;

public static class ProcFileSystem
{
    // Each trusted provider reserves 4096 characters within the existing VFS total budget.
    // The tree has at most four PIDs plus one fixed self alias; attacker input cannot grow it.
    private const int MaxGeneratedLength = 4096;

    public static void Register(VirtualFileSystem fileSystem, SimulatedLinuxState state)
    {
        if (!state.ProcfsEnabled)
        {
            // Reserve the disabled namespace so shell commands cannot recreate it.
            fileSystem.ProtectDirectory("/proc");
            return;
        }

        AddDirectory("/proc");
        AddFile("/proc/version", () => state.Version);
        AddFile("/proc/cpuinfo", () => SimulatedLinuxState.CpuInfo);
        AddFile("/proc/meminfo", () => state.MemoryInfo);
        AddFile("/proc/uptime", state.ProcUptime);
        AddFile("/proc/cmdline", () => "console=ttyS0,115200 root=/dev/mtdblock3 rw\n");
        AddFile("/proc/loadavg", state.ProcLoadAverage);
        AddFile("/proc/mounts", () => state.Mounts);
        AddDirectory("/proc/net");
        AddFile("/proc/net/dev", state.NetworkDevices);
        AddFile("/proc/net/route", state.RouteTable);
        AddFile("/proc/net/tcp", state.TcpTable);

        foreach (var process in state.Processes)
        {
            AddProcess("/proc/" + process.Pid.ToString(CultureInfo.InvariantCulture), process);
        }

        // A fixed view of PID 42, not a general symlink mechanism or a real cat subprocess.
        AddProcess("/proc/self", state.Processes.Single(process => process.Pid == SimulatedLinuxState.ShellPid));
        fileSystem.ProtectDirectory("/proc");

        void AddDirectory(string path)
        {
            if (!fileSystem.AddDirectory(path))
            {
                throw new InvalidOperationException($"Cannot register simulated directory {path}.");
            }
        }

        void AddFile(string path, Func<string> read)
        {
            if (!fileSystem.AddGenerated(path, read, MaxGeneratedLength))
            {
                throw new InvalidOperationException($"Cannot register simulated file {path}.");
            }
        }

        void AddProcess(string path, SimulatedProcess process)
        {
            AddDirectory(path);
            AddFile(path + "/status", () => process.Status);
            AddFile(path + "/cmdline", () => process.Command + '\0');
            AddFile(path + "/comm", () => process.Name + '\n');
        }
    }
}
