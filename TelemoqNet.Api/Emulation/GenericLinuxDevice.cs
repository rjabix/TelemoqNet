using System.IO;
using System.Text;
using Microsoft.Extensions.Options;
using TelemoqNet.Api.Configuration;

namespace TelemoqNet.Api.Emulation;

public sealed class GenericLinuxDevice : IDeviceEmulator
{
    private readonly HoneypotOptions _options;
    private readonly VirtualFileSystem _fileSystem = new();
    private readonly SimulatedLinuxState _state;
    private readonly Dictionary<string, IDeviceCommand> _commands;
    private string _currentDirectory = "/root";

    public GenericLinuxDevice(IOptions<HoneypotOptions> options, TimeProvider? timeProvider = null)
    {
        _options = options.Value;
        _state = new SimulatedLinuxState(_options, timeProvider ?? TimeProvider.System);
        AddFiles();
        ProcFileSystem.Register(_fileSystem, _state);
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

    public async Task<string?> ExecuteCommandAsync(string command, CancellationToken cancellationToken,
        Action<PayloadDownloadAttempt>? recordDownloadAttempt = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (command.Length > _options.MaxLineLength)
        {
            return Limit("sh: command limit exceeded");
        }

        if (recordDownloadAttempt is not null)
        {
            foreach (var attempt in PayloadDownloadAttemptParser.Find(command))
            {
                recordDownloadAttempt(attempt);
            }
        }

        await Task.Delay(_options.CommandLatencyMilliseconds, cancellationToken);
        if (!ShellCommandParser.TryParse(command, out var commands))
        {
            return Limit("sh: syntax error or command limit exceeded");
        }

        var output = new StringBuilder();
        var wroteOutput = false;
        var exitCode = 0;
        foreach (var statement in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (statement.RequiresSuccess && exitCode != 0)
            {
                continue;
            }

            var result = await ExecuteStatementAsync(statement, cancellationToken);
            if (result.Output is null)
            {
                return null;
            }

            exitCode = result.ExitCode;
            if ((result.Output.Length > 0 || result.EmitsEmptyLine) && output.Length <= _options.MaxResponseLength)
            {
                if (wroteOutput)
                {
                    output.Append('\n');
                }

                var remaining = Math.Max(0, _options.MaxResponseLength + 1 - output.Length);
                output.Append(result.Output.AsSpan(0, Math.Min(remaining, result.Output.Length)));
                wroteOutput = true;
            }
        }

        return commands.Count == 0 ? string.Empty : Limit(output.ToString());
    }

    private async Task<DeviceCommandResult> ExecuteStatementAsync(
        ShellCommand statement, CancellationToken cancellationToken)
    {
        string? destination = null;
        foreach (var redirect in statement.Redirects)
        {
            destination = VirtualFileSystem.ResolvePath(_currentDirectory, redirect.Path);
            if (redirect.Path.Length == 0)
            {
                return new DeviceCommandResult("sh: : No such file or directory", 1);
            }

            if (!_fileSystem.Write(destination, string.Empty, redirect.Append, out var error))
            {
                return new DeviceCommandResult($"sh: {redirect.Path}: {error}", 1);
            }
        }

        var result = await ExecuteArgumentsAsync(statement.Arguments, cancellationToken);
        if (destination is not null && result.ExitCode == 0 && result.Output is not null)
        {
            var contents = result.Output.Length == 0 && !result.EmitsEmptyLine ? string.Empty : result.Output + "\n";
            return _fileSystem.Write(destination, contents, append: true, out var error)
                ? new DeviceCommandResult(string.Empty)
                : new DeviceCommandResult($"sh: {statement.Redirects[^1].Path}: {error}", 1);
        }

        return result;
    }

    private async Task<DeviceCommandResult> ExecuteArgumentsAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            return string.Empty;
        }

        var name = Path.GetFileName(args[0]);
        if (name is "exit" or "logout")
        {
            return new DeviceCommandResult(null);
        }

        return _commands.TryGetValue(name, out var handler)
            ? await handler.ExecuteAsync(args[1..], CreateCommandContext(), cancellationToken)
            : new DeviceCommandResult($"sh: {name}: not found", 127);
    }

    private CommandContext CreateCommandContext() =>
        new(
            _fileSystem,
            _state.FormatUptime,
            _state.Reboot,
            () => _currentDirectory,
            path => VirtualFileSystem.ResolvePath(_currentDirectory, path),
            ChangeDirectory,
            _options.Hostname);

    private DeviceCommandResult ListFiles(string[] args)
    {
        var (showAll, longFormat, path) = ParseListArguments(args);
        var resolvedPath = VirtualFileSystem.ResolvePath(_currentDirectory, path);
        if (_fileSystem.TryGetFile(resolvedPath, out var file) && !file!.IsDirectory)
        {
            return FormatFileListing([file], longFormat);
        }

        var entries = _fileSystem.ListEntries(resolvedPath)
            .Where(entry => showAll || !Path.GetFileName(entry.Path).StartsWith('.'))
            .ToArray();

        if (entries.Length == 0 && !_fileSystem.IsDirectory(resolvedPath))
        {
            return new DeviceCommandResult($"ls: {path}: No such file or directory", 1);
        }

        return FormatFileListing(entries, longFormat);
    }

    private (bool ShowAll, bool LongFormat, string Path) ParseListArguments(string[] args)
    {
        var showAll = false;
        var longFormat = false;
        var paths = new List<string>();

        foreach (var argument in args)
        {
            if (!argument.StartsWith('-') || argument.Length <= 1)
            {
                paths.Add(argument);
                continue;
            }

            showAll |= argument.Contains('a');
            longFormat |= argument.Contains('l');
        }

        return (showAll, longFormat, paths.FirstOrDefault() ?? _currentDirectory);
    }

    private static string FormatFileListing(VirtualFile[] entries, bool longFormat) =>
        longFormat
            ? string.Join(
                '\n',
                entries.Select(entry =>
                    $"{FormatPermissions(entry)} " +
                    $"1 root root {entry.Contents.Length,8} {entry.Path}"))
            : string.Join('\n', entries.Select(entry => Path.GetFileName(entry.Path)));

    private static string FormatPermissions(VirtualFile file) =>
        (file.IsDirectory, file.IsReadOnly) switch
        {
            (true, true) => "dr-xr-xr-x",
            (true, false) => "drwxr-xr-x",
            (false, true) => "-r--r--r--",
            (false, false) => "-rw-r--r--"
        };

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
            new VirtualFile("/root", string.Empty, IsDirectory: true),
            new VirtualFile("/sbin", string.Empty, IsDirectory: true),
            new VirtualFile("/tmp", string.Empty, IsDirectory: true),
            new VirtualFile("/usr", string.Empty, IsDirectory: true),
            new VirtualFile("/usr/bin", string.Empty, IsDirectory: true),
            new VirtualFile("/var", string.Empty, IsDirectory: true),
            new VirtualFile("/var/log", string.Empty, IsDirectory: true),
            new VirtualFile("/etc/hostname", _options.Hostname),
            new VirtualFile("/etc/device-release", $"MODEL={DeviceIdentity.Model}\nFIRMWARE={DeviceIdentity.FirmwareVersion}\n"),
            new VirtualFile("/etc/passwd",
                "root:x:0:0:root:/root:/bin/sh\nadmin:x:1000:1000:admin:/home/admin:/bin/sh\n"),
            new VirtualFile("/etc/group", "root:x:0:\nadmin:x:1000:\n"),
            new VirtualFile("/etc/os-release", $"NAME=\"{DeviceIdentity.Model} Smart Home Gateway\"\nVERSION={DeviceIdentity.FirmwareVersion}\nID=embedded-linux\n"),
            new VirtualFile("/etc/issue", _options.Banner + "\n"),
            new VirtualFile("/etc/profile", "export PATH=/bin:/sbin:/usr/bin\n"),
            new VirtualFile("/etc/resolv.conf", $"nameserver {SimulatedLinuxState.LanAddress}\n"),
            new VirtualFile("/etc/hosts", $"{SimulatedLinuxState.LoopbackAddress} localhost\n{SimulatedLinuxState.LanAddress} {_options.Hostname}\n"),
            new VirtualFile("/etc/mtab", _state.Mounts),
            new VirtualFile("/etc/config/network", $"config interface 'lan'\n\toption ipaddr '{SimulatedLinuxState.LanAddress}'\n"),
            new VirtualFile("/etc/config/system", $"config system\n\toption hostname '{_options.Hostname}'\n\toption model '{DeviceIdentity.Model}'\n\toption firmware '{DeviceIdentity.FirmwareVersion}'\n"),
            new VirtualFile("/etc/config/mqtt", $"config mqtt\n\toption enabled '{(_options.Mqtt.Enabled ? 1 : 0)}'\n\toption port '{_options.Mqtt.Port}'\n\toption topic_prefix '{DeviceIdentity.MqttTopicPrefix}'\n"),
            new VirtualFile("/var/log/messages", $"Jun 12 08:23:17 {_options.Hostname} daemon.info {DeviceIdentity.Model} firmware {DeviceIdentity.FirmwareVersion} started\n"),
            new VirtualFile("/var/log/auth.log", $"Jun 12 08:23:17 {_options.Hostname} auth.info login service ready\n"),
            new VirtualFile("/tmp/.keep", string.Empty),
            new VirtualFile("/dev/null", string.Empty),
            new VirtualFile("/dev/console", string.Empty),
            new VirtualFile("/home/admin/.profile", "export PS1='$ '\n"),
            new VirtualFile("/root/.profile", "export PS1='# '\n"),
            new VirtualFile("/bin/busybox", string.Empty, IsExecutable: true),
            new VirtualFile("/bin/sh", string.Empty, IsExecutable: true),
            new VirtualFile("/sbin/ifconfig", string.Empty, IsExecutable: true),
            new VirtualFile("/usr/bin/wget", string.Empty, IsExecutable: true),
            new VirtualFile("/usr/bin/curl", string.Empty, IsExecutable: true)
        };

        foreach (var file in files)
        {
            _fileSystem.Add(file);
        }
    }

    private bool ChangeDirectory(string path)
    {
        if (!_fileSystem.IsDirectory(path))
        {
            return false;
        }

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
            new SimpleResponseCommand("pwd", (_, context) => context.CurrentDirectory),
            new SimpleResponseCommand("id", (_, _) => "uid=0(root) gid=0(root)"),
            new SimpleResponseCommand("whoami", (_, _) => "root"),
            new SimpleResponseCommand("hostname", (_, _) => _options.Hostname),
            new SimpleResponseCommand("ls", (arguments, _) => ListFiles(arguments)),
            new SimpleResponseCommand("ifconfig", (_, _) => _state.NetworkInfo(false)),
            new SimpleResponseCommand("ip", (_, _) => _state.NetworkInfo(true)),
            new SimpleResponseCommand("ps", (_, _) => _state.ProcessListing()),
            new SimpleResponseCommand("netstat", (_, _) => _state.Netstat()),
            new SimpleResponseCommand("df",
                (_, _) =>
                    "Filesystem           1K-blocks      Used Available Use% Mounted on\n/dev/root                 4096       768      3328  19% /"),
            new SimpleResponseCommand("free", (_, _) => _state.Free()),
            new SimpleResponseCommand("mount", (_, _) => _state.MountListing()),
            new SimpleResponseCommand("dmesg", (_, _) => _state.KernelMessages()),
            new SimpleResponseCommand("date", (_, _) => _state.Date()),
            new SimpleResponseCommand("uptime", (_, context) => context.Uptime()),
            new DownloadCommand("wget"),
            new DownloadCommand("curl"),
            new SimpleResponseCommand("reboot", (_, context) => RebootAndRespond(context)),
            new SimpleResponseCommand("clear", (_, _) => "\u001b[2J\u001b[H"),
            new SimpleResponseCommand("true", (_, _) => string.Empty),
            new SimpleResponseCommand("false", (_, _) => new DeviceCommandResult(string.Empty, 1)),
            new SimpleResponseCommand("su", (_, _) => string.Empty)
        };

        var strategies = commands.ToDictionary(command => command.Name, StringComparer.Ordinal);
        strategies.Add("busybox", new BusyBoxCommand(strategies));
        return strategies;
    }

    private string RebootAndRespond(CommandContext context)
    {
        context.Reboot();
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
