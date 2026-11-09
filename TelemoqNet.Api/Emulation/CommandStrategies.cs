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

public interface IDeviceCommand
{
    string Name { get; }

    Task<string?> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken);
}

public sealed class HelpCommand : IDeviceCommand
{
    public string Name => "help";

    public Task<string?> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<string?>(
            "Built-in commands: " +
            "help echo env pwd ls cat touch mkdir rm uname id whoami hostname " +
            "ifconfig ip ps netstat df free mount dmesg date uptime busybox " +
            "wget curl reboot clear true false exit logout");
}

public sealed class EchoCommand : IDeviceCommand
{
    public string Name => "echo";

    public Task<string?> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        var redirectIndex = Array.IndexOf(arguments, ">>");
        return redirectIndex >= 0
            ? Task.FromResult<string?>(ExecuteRedirect(arguments, redirectIndex, context))
            : Task.FromResult<string?>(string.Join(' ', arguments));
    }

    private static string ExecuteRedirect(
        string[] arguments,
        int redirectIndex,
        CommandContext context)
    {
        if (redirectIndex == arguments.Length - 1)
        {
            return "sh: syntax error: missing file operand";
        }

        if (redirectIndex != arguments.Length - 2)
        {
            return "sh: syntax error near unexpected token";
        }

        var content = string.Join(' ', arguments[..redirectIndex]) + "\n";
        var path = context.ResolvePath(arguments[^1]);
        return context.FileSystem.Append(path, content)
            ? string.Empty
            : $"sh: {arguments[^1]}: Is a directory";
    }
}

public sealed class EnvironmentCommand : IDeviceCommand
{
    public string Name => "env";

    public Task<string?> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<string?>(
            "HOME=/root\nPATH=/bin:/sbin:/usr/bin\nSHELL=/bin/sh\nUSER=root");
}

public sealed class ChangeDirectoryCommand : IDeviceCommand
{
    public string Name => "cd";

    public Task<string?> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        var target = arguments.Length == 0 ? "/root" : arguments[0];
        var path = context.ResolvePath(target);

        return Task.FromResult<string?>(ChangeDirectory(target, path, context));
    }

    private static string ChangeDirectory(string target, string path, CommandContext context)
    {
        if (context.FileSystem.Contains(path) && !context.FileSystem.IsDirectory(path))
        {
            return $"sh: cd: {target}: Not a directory";
        }

        return context.ChangeDirectory(path)
            ? string.Empty
            : $"sh: cd: {target}: No such file or directory";
    }
}

public sealed class FileCommand : IDeviceCommand
{
    public string Name => "cat";

    public Task<string?> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        if (arguments.Length != 1)
        {
            return Task.FromResult<string?>("cat: usage: cat FILE");
        }

        var path = context.ResolvePath(arguments[0]);
        var response = context.FileSystem.TryRead(path, out var value)
            ? value.TrimEnd('\r', '\n')
            : $"cat: {arguments[0]}: No such file or directory";

        return Task.FromResult<string?>(response);
    }
}

public sealed class TouchCommand : IDeviceCommand
{
    public string Name => "touch";

    public Task<string?> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        if (arguments.Length == 0)
        {
            return Task.FromResult<string?>("touch: missing file operand");
        }

        return Task.FromResult<string?>(TouchFiles(arguments, context));
    }

    private static string TouchFiles(string[] paths, CommandContext context)
    {
        foreach (var path in paths)
        {
            if (!context.FileSystem.Touch(context.ResolvePath(path)))
            {
                return $"touch: cannot touch '{path}': Is a directory";
            }
        }

        return string.Empty;
    }
}

public sealed class MakeDirectoryCommand : IDeviceCommand
{
    public string Name => "mkdir";

    public Task<string?> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        var paths = arguments.Where(argument => !argument.StartsWith('-')).ToArray();
        if (paths.Length == 0)
        {
            return Task.FromResult<string?>("mkdir: missing operand");
        }

        return Task.FromResult<string?>(CreateDirectories(paths, context));
    }

    private static string CreateDirectories(string[] paths, CommandContext context)
    {
        foreach (var path in paths)
        {
            if (!context.FileSystem.AddDirectory(context.ResolvePath(path)))
            {
                return $"mkdir: cannot create directory '{path}': File exists";
            }
        }

        return string.Empty;
    }
}

public sealed class RemoveCommand : IDeviceCommand
{
    public string Name => "rm";

    public Task<string?> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<string?>(
            "rm: simulated device does not remove captured files");
}

public sealed class SystemInfoCommand : IDeviceCommand
{
    public string Name => "uname";

    public Task<string?> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<string?>(
            arguments.SequenceEqual(["-a"])
                ? $"Linux {context.Hostname} 2.6.36 #1 SMP Thu Jun 12 08:23:17 UTC 2014 mips GNU/Linux"
                : "Linux");
}

public sealed class BusyBoxCommand : IDeviceCommand
{
    public string Name => "busybox";

    public Task<string?> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken)
    {
        if (arguments.Length == 0)
        {
            return Task.FromResult<string?>("BusyBox v1.19.4 (built-in shell commands)");
        }

        var applet = arguments[0];
        var appletArguments = arguments.Skip(1).ToArray();
        return Task.FromResult<string?>(GetAppletResponse(applet, appletArguments, context));
    }

    private static string GetAppletResponse(
        string applet,
        string[] arguments,
        CommandContext context) =>
        applet switch
        {
            "busybox" => "BusyBox v1.19.4 (built-in shell commands)",
            "echo" => string.Join(' ', arguments),
            "cat" when TryReadFile(arguments, context, out var contents) =>
                contents.TrimEnd('\r', '\n'),
            "uname" => $"Linux {context.Hostname} 2.6.36 #1 SMP Thu Jun 12 08:23:17 UTC 2014 mips GNU/Linux",
            "wget" => DownloadCommand.Response("wget", arguments),
            "true" or "false" or "sh" or "ls" or "mkdir" or "touch" => string.Empty,
            _ => $"{applet}: applet not found"
        };

    private static bool TryReadFile(
        string[] arguments,
        CommandContext context,
        out string contents)
    {
        contents = string.Empty;
        return arguments.Length == 1 &&
               context.FileSystem.TryRead(context.ResolvePath(arguments[0]), out contents);
    }
}

public sealed class DownloadCommand(string name) : IDeviceCommand
{
    public string Name => name;

    public Task<string?> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<string?>(Response(Name, arguments));

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
    Func<string[], CommandContext, string> response) : IDeviceCommand
{
    public string Name => name;

    public Task<string?> ExecuteAsync(
        string[] arguments,
        CommandContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult<string?>(response(arguments, context));
}
