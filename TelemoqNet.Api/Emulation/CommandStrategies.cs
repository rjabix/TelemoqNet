namespace TelemoqNet.Api.Emulation;

public sealed class CommandContext(
    VirtualFileSystem fileSystem,
    Func<string> uptime,
    Action reboot,
    Func<string> currentDirectory,
    Func<string, string> resolvePath,
    Func<string, bool> changeDirectory,
    string hostname)
{
    public VirtualFileSystem FileSystem { get; } = fileSystem;
    public Func<string> Uptime { get; } = uptime;
    public Action Reboot { get; } = reboot;
    public string CurrentDirectory => currentDirectory();
    public Func<string, string> ResolvePath { get; } = resolvePath;
    public Func<string, bool> ChangeDirectory { get; } = changeDirectory;
    public string Hostname { get; } = hostname;
}

public sealed record DeviceCommandResult(string? Output, int ExitCode = 0, bool EmitsEmptyLine = false)
{
    public static implicit operator DeviceCommandResult(string? output) => new(output);
}

public interface IDeviceCommand
{
    string Name { get; }

    Task<DeviceCommandResult> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken);
}

public sealed class HelpCommand : IDeviceCommand
{
    public string Name => "help";

    public Task<DeviceCommandResult> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<DeviceCommandResult>(
            "Built-in commands: " +
            "help echo env pwd ls cat touch mkdir rm uname id whoami hostname " +
            "ifconfig ip ps netstat df free mount dmesg date uptime busybox " +
            "wget curl reboot clear true false exit logout");
}

public sealed class EchoCommand : IDeviceCommand
{
    public string Name => "echo";

    public Task<DeviceCommandResult> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(new DeviceCommandResult(Response(arguments), EmitsEmptyLine: true));

    internal static string Response(string[] arguments)
    {
        var escapes = false;
        var index = 0;
        while (index < arguments.Length && arguments[index] is "-e" or "-E")
        {
            escapes = arguments[index++] == "-e";
        }

        var text = string.Join(' ', arguments[index..]);
        if (!escapes)
        {
            return text;
        }

        var output = new System.Text.StringBuilder();
        for (index = 0; index < text.Length; index++)
        {
            var value = text[index];
            if (value != '\\' || index + 1 == text.Length)
            {
                output.Append(value);
                continue;
            }

            var escaped = text[++index];
            if (escaped is 'x' or '0')
            {
                var number = 0;
                var digits = 0;
                var maxDigits = escaped == 'x' ? 2 : 3;
                var radix = escaped == 'x' ? 16 : 8;
                while (digits < maxDigits && index + 1 < text.Length)
                {
                    var digit = "0123456789abcdef".IndexOf(char.ToLowerInvariant(text[index + 1]));
                    if (digit < 0 || digit >= radix)
                    {
                        break;
                    }

                    number = number * radix + digit;
                    digits++;
                    index++;
                }

                if (digits > 0 || escaped == '0')
                {
                    output.Append((char)(number & 0xff));
                }
                else
                {
                    output.Append("\\x");
                }
            }
            else
            {
                output.Append(escaped switch
                {
                    '\\' => "\\",
                    'a' => "\a",
                    'b' => "\b",
                    'e' => "\u001b",
                    'f' => "\f",
                    'n' => "\n",
                    'r' => "\r",
                    't' => "\t",
                    'v' => "\v",
                    _ => "\\" + escaped
                });
            }
        }

        return output.ToString();
    }
}

public sealed class EnvironmentCommand : IDeviceCommand
{
    public string Name => "env";

    public Task<DeviceCommandResult> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<DeviceCommandResult>(
            "HOME=/root\nPATH=/bin:/sbin:/usr/bin\nSHELL=/bin/sh\nUSER=root");
}

public sealed class ChangeDirectoryCommand : IDeviceCommand
{
    public string Name => "cd";

    public Task<DeviceCommandResult> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        var target = arguments.Length == 0 ? "/root" : arguments[0];
        var path = context.ResolvePath(target);

        return Task.FromResult<DeviceCommandResult>(ChangeDirectory(target, path, context));
    }

    private static DeviceCommandResult ChangeDirectory(string target, string path, CommandContext context)
    {
        if (context.FileSystem.Contains(path) && !context.FileSystem.IsDirectory(path))
        {
            return new DeviceCommandResult($"sh: cd: {target}: Not a directory", 1);
        }

        return context.ChangeDirectory(path)
            ? string.Empty
            : new DeviceCommandResult($"sh: cd: {target}: No such file or directory", 1);
    }
}

public sealed class FileCommand : IDeviceCommand
{
    public string Name => "cat";

    public Task<DeviceCommandResult> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        if (arguments.Length != 1)
        {
            return Task.FromResult(new DeviceCommandResult("cat: usage: cat FILE", 1));
        }

        var path = context.ResolvePath(arguments[0]);
        DeviceCommandResult response = context.FileSystem.TryRead(path, out var value)
            ? value.TrimEnd('\r', '\n')
            : new DeviceCommandResult($"cat: {arguments[0]}: No such file or directory", 1);

        return Task.FromResult<DeviceCommandResult>(response);
    }
}

public sealed class TouchCommand : IDeviceCommand
{
    public string Name => "touch";

    public Task<DeviceCommandResult> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        if (arguments.Length == 0)
        {
            return Task.FromResult(new DeviceCommandResult("touch: missing file operand", 1));
        }

        return Task.FromResult<DeviceCommandResult>(TouchFiles(arguments, context));
    }

    private static DeviceCommandResult TouchFiles(string[] paths, CommandContext context)
    {
        foreach (var path in paths)
        {
            if (!context.FileSystem.Touch(context.ResolvePath(path)))
            {
                var error = context.FileSystem.IsDirectory(context.ResolvePath(path))
                    ? "Is a directory"
                    : "No space left on device";
                return new DeviceCommandResult($"touch: cannot touch '{path}': {error}", 1);
            }
        }

        return string.Empty;
    }
}

public sealed class MakeDirectoryCommand : IDeviceCommand
{
    public string Name => "mkdir";

    public Task<DeviceCommandResult> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        var paths = arguments.Where(argument => !argument.StartsWith('-')).ToArray();
        if (paths.Length == 0)
        {
            return Task.FromResult(new DeviceCommandResult("mkdir: missing operand", 1));
        }

        return Task.FromResult<DeviceCommandResult>(CreateDirectories(paths, context));
    }

    private static DeviceCommandResult CreateDirectories(string[] paths, CommandContext context)
    {
        foreach (var path in paths)
        {
            if (!context.FileSystem.AddDirectory(context.ResolvePath(path)))
            {
                var error = context.FileSystem.Contains(context.ResolvePath(path))
                    ? "File exists"
                    : "No space left on device";
                return new DeviceCommandResult($"mkdir: cannot create directory '{path}': {error}", 1);
            }
        }

        return string.Empty;
    }
}

public sealed class RemoveCommand : IDeviceCommand
{
    public string Name => "rm";

    public Task<DeviceCommandResult> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<DeviceCommandResult>(
            new DeviceCommandResult("rm: simulated device does not remove captured files", 1));
}

public sealed class SystemInfoCommand : IDeviceCommand
{
    public string Name => "uname";

    public Task<DeviceCommandResult> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<DeviceCommandResult>(
            arguments.SequenceEqual(["-a"])
                ? $"Linux {context.Hostname} 2.6.36 #1 SMP Thu Jun 12 08:23:17 UTC 2014 mips GNU/Linux"
                : "Linux");
}

public sealed class BusyBoxCommand(IReadOnlyDictionary<string, IDeviceCommand> commands) : IDeviceCommand
{
    public string Name => "busybox";

    public Task<DeviceCommandResult> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        if (arguments.Length == 0 || arguments[0] == "busybox")
        {
            return Task.FromResult<DeviceCommandResult>("BusyBox v1.19.4 (built-in shell commands)");
        }

        return commands.TryGetValue(arguments[0], out var command)
            ? command.ExecuteAsync(arguments[1..], context, cancellationToken)
            : Task.FromResult(new DeviceCommandResult($"{arguments[0]}: applet not found", 127));
    }
}

public sealed class DownloadCommand(string name) : IDeviceCommand
{
    public string Name => name;

    public Task<DeviceCommandResult> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(new DeviceCommandResult(Response(Name, arguments),
            PayloadDownloadAttemptParser.ParseArguments(Name, arguments).InformationOption is null ? 1 : 0));

    internal static string Response(string tool, string[] arguments)
    {
        var parsed = PayloadDownloadAttemptParser.ParseArguments(tool, arguments);
        if (parsed.InformationOption == "help")
        {
            return Help(tool);
        }

        if (parsed.InformationOption == "version")
        {
            return tool == "wget"
                ? "BusyBox v1.19.4 (multi-call binary)"
                : "curl 7.36.0 (mips-unknown-linux-gnu) libcurl/7.36.0\nProtocols: ftp http https tftp";
        }

        if (parsed.Urls.Count == 0)
        {
            return tool == "wget"
                ? "wget: missing URL\nUsage: wget [OPTIONS] URL..."
                : "curl: no URL specified!\ncurl: try 'curl --help' for more information";
        }

        return tool == "wget"
            ? "wget: can't connect to remote host: Network is unreachable"
            : "curl: (7) Failed to connect to host: Network is unreachable";
    }

    private static string Help(string tool) => tool == "wget"
        ? """
          BusyBox v1.19.4 (multi-call binary)
          Usage: wget [OPTIONS] URL...

          Retrieve files via HTTP or FTP
            -q              Quiet
            -O FILE         Save to FILE ('-' for stdout)
            -P DIR          Save to DIR
            -T SEC          Network timeout
            -U STR          Use STR as User-Agent
            --help          Show this help
          """
        : """
          Usage: curl [options...] <url>
           -d, --data DATA       HTTP POST data
           -f, --fail            Fail on HTTP errors
           -h, --help            Show this help
           -H, --header LINE     Pass a custom header
           -L, --location        Follow redirects
           -o, --output FILE     Write output to FILE
           -O, --remote-name     Use the remote file name
           -s, --silent          Silent mode
           -S, --show-error      Show errors in silent mode
           -V, --version         Show version information
               --url URL         URL to retrieve
           -x, --proxy URL       Use a proxy
          """;
}

public sealed class SimpleResponseCommand(
    string name,
    Func<string[], CommandContext, DeviceCommandResult> response) : IDeviceCommand
{
    public string Name => name;

    public Task<DeviceCommandResult> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<DeviceCommandResult>(response(arguments, context));
}
