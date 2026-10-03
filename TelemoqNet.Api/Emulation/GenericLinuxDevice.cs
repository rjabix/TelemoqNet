using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Options;
using TelemoqNet.Api.Configuration;

namespace TelemoqNet.Api.Emulation;

public sealed class GenericLinuxDevice : IDeviceEmulator
{
    private readonly HoneypotOptions _options;
    private readonly VirtualFileSystem _fileSystem = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private readonly Dictionary<string, IDeviceCommand> _commands;
    private string _currentDirectory = "/root";

    public GenericLinuxDevice(IOptions<HoneypotOptions> options)
    {
        _options = options.Value;
        AddFiles();
        _commands = CreateCommands();
    }

    public string ProfileName => "GenericLinux";
    public string Banner => _options.Banner;
    public string Prompt => $"# ";
    public string CurrentDirectory => _currentDirectory;

    public bool Authenticate(string username, string password) =>
        _options.Credentials
            .Select(ParseCredential)
            .Any(c => c.Username == username && c.Password == password);

    public async Task<string?> ExecuteCommandAsync(string command, CancellationToken cancellationToken)
    {
        await Task.Delay(_options.CommandLatencyMilliseconds, cancellationToken);
        var text = command.Trim();
        if (text.Length == 0) return string.Empty;

        var args = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var name = Path.GetFileName(args[0]);
        if (name is "exit" or "logout")
            return null;

        if (!_commands.TryGetValue(name, out var handler))
            return Limit($"sh: {name}: not found");

        var result = await handler.ExecuteAsync(
            args.Skip(1).ToArray(),
            new CommandContext(
                _options,
                _fileSystem,
                FormatUptime,
                () => Reboot(),
                () => _currentDirectory,
                path => VirtualFileSystem.ResolvePath(_currentDirectory, path),
                ChangeDirectory),
            cancellationToken);
        return result is null ? null : Limit(result);
    }

    private string ListFiles(string[] args)
    {
        var showAll = false;
        var longFormat = false;
        var paths = new List<string>();

        foreach (var argument in args)
        {
            if (argument.StartsWith('-') && argument.Length > 1)
            {
                showAll |= argument.Contains('a');
                longFormat |= argument.Contains('l');
                continue;
            }

            paths.Add(argument);
        }

        var path = paths.FirstOrDefault() ?? _currentDirectory;
        var resolvedPath = VirtualFileSystem.ResolvePath(_currentDirectory, path);
        var entries = _fileSystem.ListEntries(resolvedPath)
            .Where(entry => showAll || !Path.GetFileName(entry.Path).StartsWith('.'))
            .ToArray();

        if (entries.Length == 0 && !_fileSystem.IsDirectory(resolvedPath))
            return $"ls: {path}: No such file or directory";

        if (!longFormat)
            return string.Join('\n', entries.Select(entry => Path.GetFileName(entry.Path)));

        return string.Join(
            '\n',
            entries.Select(entry =>
                $"{(entry.IsDirectory ? "drwxr-xr-x" : "-rw-r--r--")} " +
                $"1 root root {entry.Contents.Length,8} {entry.Path}"));
    }

    private void AddFiles()
    {
        var files = new[]
        {
            new VirtualFile("/", string.Empty, IsDirectory: true),
            new VirtualFile("/bin", string.Empty, IsDirectory: true),
            new VirtualFile("/dev", string.Empty, IsDirectory: true),
            new VirtualFile("/etc", string.Empty, IsDirectory: true),
            new VirtualFile("/etc/config", string.Empty, IsDirectory: true),
            new VirtualFile("/home", string.Empty, IsDirectory: true),
            new VirtualFile("/home/admin", string.Empty, IsDirectory: true),
            new VirtualFile("/proc", string.Empty, IsDirectory: true),
            new VirtualFile("/root", string.Empty, IsDirectory: true),
            new VirtualFile("/sbin", string.Empty, IsDirectory: true),
            new VirtualFile("/tmp", string.Empty, IsDirectory: true),
            new VirtualFile("/usr", string.Empty, IsDirectory: true),
            new VirtualFile("/usr/bin", string.Empty, IsDirectory: true),
            new VirtualFile("/var", string.Empty, IsDirectory: true),
            new VirtualFile("/var/log", string.Empty, IsDirectory: true),
            new VirtualFile("/etc/hostname", _options.Hostname),
            new VirtualFile("/etc/passwd",
                "root:x:0:0:root:/root:/bin/sh\nadmin:x:1000:1000:admin:/home/admin:/bin/sh\n"),
            new VirtualFile("/etc/group", "root:x:0:\nadmin:x:1000:\n"),
            new VirtualFile("/etc/os-release", "NAME=BusyBox\nVERSION=1.19.4\nID=embedded-linux\n"),
            new VirtualFile("/etc/issue", "BusyBox v1.19.4\\n\\l\n"),
            new VirtualFile("/etc/profile", "export PATH=/bin:/sbin:/usr/bin\n"),
            new VirtualFile("/etc/resolv.conf", "nameserver 192.168.1.1\n"),
            new VirtualFile("/etc/hosts", "127.0.0.1 localhost\n192.168.1.1 router\n"),
            new VirtualFile("/etc/mtab", "rootfs / rootfs rw 0 0\n"),
            new VirtualFile("/etc/config/network", "config interface 'lan'\n\toption ipaddr '192.168.1.1'\n"),
            new VirtualFile("/etc/config/system", "config system\n\toption hostname 'router'\n"),
            new VirtualFile("/proc/version", "Linux version 2.6.36 (build@router) #1 SMP\n"),
            new VirtualFile("/proc/cpuinfo", "system type\t\t: MediaTek SoC\nprocessor\t\t: 0\ncpu MHz\t\t: 580.000\n"),
            new VirtualFile("/proc/meminfo", "MemTotal:          32768 kB\nMemFree:           18432 kB\n"),
            new VirtualFile("/proc/uptime", "1036800.00 1030000.00\n"),
            new VirtualFile("/proc/cmdline", "console=ttyS0,115200 root=/dev/mtdblock3 rw\n"),
            new VirtualFile("/var/log/messages", "Jun 12 08:23:17 router daemon.info system started\n"),
            new VirtualFile("/var/log/auth.log", "Jun 12 08:23:17 router auth.info login service ready\n"),
            new VirtualFile("/tmp/.keep", string.Empty),
            new VirtualFile("/dev/null", string.Empty),
            new VirtualFile("/dev/console", string.Empty),
            new VirtualFile("/home/admin/.profile", "export PS1='$ '\n"),
            new VirtualFile("/root/.profile", "export PS1='# '\n"),
            new VirtualFile("/bin/busybox", string.Empty, IsExecutable: true),
            new VirtualFile("/bin/sh", string.Empty, IsExecutable: true),
            new VirtualFile("/sbin/ifconfig", string.Empty, IsExecutable: true),
            new VirtualFile("/usr/bin/wget", string.Empty, IsExecutable: true)
        };

        foreach (var file in files)
            _fileSystem.Add(file);
    }

    private bool ChangeDirectory(string path)
    {
        if (!_fileSystem.IsDirectory(path))
            return false;

        _currentDirectory = path;
        return true;
    }

    private Dictionary<string, IDeviceCommand> CreateCommands()
    {
        var commands = new IDeviceCommand[]
        {
            new HelpCommand(),
            new EchoCommand(),
            new EnvironmentCommand(),
            new ChangeDirectoryCommand(),
            new FileCommand(),
            new TouchCommand(),
            new MakeDirectoryCommand(),
            new RemoveCommand(),
            new SystemInfoCommand(),
            new BusyBoxCommand(),
            new SimpleResponseCommand("pwd", (_, context) => context.CurrentDirectory),
            new SimpleResponseCommand("id", (_, _) => "uid=0(root) gid=0(root)"),
            new SimpleResponseCommand("whoami", (_, _) => "root"),
            new SimpleResponseCommand("hostname", (_, _) => _options.Hostname),
            new SimpleResponseCommand("ls", (arguments, _) => ListFiles(arguments)),
            new SimpleResponseCommand("ifconfig", (_, _) => NetworkInfo(["ifconfig"])),
            new SimpleResponseCommand("ip", (_, _) => NetworkInfo(["ip"])),
            new SimpleResponseCommand("ps",
                (_, _) => "PID USER       COMMAND\n    1 root       init\n   42 root       /bin/sh"),
            new SimpleResponseCommand("netstat",
                (_, _) =>
                    "Active Internet connections\nProto Local Address           State\nTCP   0.0.0.0:23              LISTEN"),
            new SimpleResponseCommand("df",
                (_, _) =>
                    "Filesystem           1K-blocks      Used Available Use% Mounted on\n/dev/root                 4096       768      3328  19% /"),
            new SimpleResponseCommand("free",
                (_, _) => "              total        used        free\nMem:          32768       14336       18432"),
            new SimpleResponseCommand("mount",
                (_, _) => "/dev/root on / type squashfs (ro)\nproc on /proc type proc (rw)"),
            new SimpleResponseCommand("dmesg",
                (_, _) => "[    0.000000] Linux version 2.6.36\n[    1.234000] eth0: link up"),
            new SimpleResponseCommand("date", (_, _) => DateTime.UtcNow.ToString("ddd MMM d HH:mm:ss 'UTC' yyyy")),
            new SimpleResponseCommand("uptime", (_, context) => context.Uptime()),
            new SimpleResponseCommand("wget", (_, _) => "Network access is simulated; no outbound request was made."),
            new SimpleResponseCommand("curl", (_, _) => "Network access is simulated; no outbound request was made."),
            new SimpleResponseCommand("reboot", (_, context) => RebootAndRespond(context)),
            new SimpleResponseCommand("clear", (_, _) => "\u001b[2J\u001b[H"),
            new SimpleResponseCommand("true", (_, _) => string.Empty),
            new SimpleResponseCommand("false", (_, _) => string.Empty)
        };

        return commands.ToDictionary(command => command.Name, StringComparer.Ordinal);
    }

    private string RebootAndRespond(CommandContext context)
    {
        context.Reboot();
        return "Restarting system.";
    }

    private string NetworkInfo(string[] args) => args.Length > 0 && args[0] == "ip"
        ? "1: lo: <LOOPBACK,UP> mtu 65536\n    inet 127.0.0.1/8 scope host lo\n2: eth0: <BROADCAST,UP> mtu 1500\n    inet 192.168.1.1/24 scope global eth0"
        : "eth0      Link encap:Ethernet  HWaddr 02:00:00:00:00:01\n          inet addr:192.168.1.1  Bcast:192.168.1.255  Mask:255.255.255.0\nlo        Link encap:Local Loopback  inet addr:127.0.0.1";

    private string FormatUptime()
    {
        var elapsed = _uptime.Elapsed;
        return
            $" {DateTime.UtcNow:HH:mm:ss} up {elapsed.Days} days, {elapsed.Hours}:{elapsed.Minutes:00}, load average: 0.00, 0.01, 0.05";
    }

    private string Reboot()
    {
        _uptime.Restart();
        return "Restarting system.";
    }

    private string Limit(string value) =>
        value.Length <= _options.MaxResponseLength
            ? "\r\n" + value
            : "\r\n" + value[.._options.MaxResponseLength] + "\r\n[output truncated]";

    private static (string Username, string Password) ParseCredential(string value)
    {
        var separator = value.IndexOf(':');
        return separator < 0
            ? (value, string.Empty)
            : (value[..separator], value[(separator + 1)..]);
    }
}