using TelemoqNet.Api.Configuration;

namespace TelemoqNet.Api.Emulation;

public sealed class CommandContext(
    HoneypotOptions options,
    VirtualFileSystem fileSystem,
    Func<string> uptime,
    Action reboot,
    Func<string> currentDirectory,
    Func<string, string> resolvePath,
    Func<string, bool> changeDirectory)
{
    public HoneypotOptions Options { get; } = options;
    public VirtualFileSystem FileSystem { get; } = fileSystem;
    public Func<string> Uptime { get; } = uptime;
    public Action Reboot { get; } = reboot;
    public string CurrentDirectory => currentDirectory();
    public Func<string, string> ResolvePath { get; } = resolvePath;
    public Func<string, bool> ChangeDirectory { get; } = changeDirectory;
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
        if (redirectIndex >= 0)
        {
            if (redirectIndex == arguments.Length - 1)
                return Task.FromResult<string?>("sh: syntax error: missing file operand");

            if (redirectIndex != arguments.Length - 2)
                return Task.FromResult<string?>("sh: syntax error near unexpected token");

            var content = string.Join(' ', arguments[..redirectIndex]) + "\n";
            var path = context.ResolvePath(arguments[^1]);
            return Task.FromResult<string?>(
                context.FileSystem.Append(path, content)
                    ? string.Empty
                    : $"sh: {arguments[^1]}: Is a directory");
        }

        return Task.FromResult<string?>(string.Join(' ', arguments));
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

        if (context.FileSystem.Contains(path) &&
            !context.FileSystem.IsDirectory(path))
        {
            return Task.FromResult<string?>(
                $"sh: cd: {target}: Not a directory");
        }

        return Task.FromResult<string?>(
            context.ChangeDirectory(path)
                ? string.Empty
                : $"sh: cd: {target}: No such file or directory");
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
            return Task.FromResult<string?>("cat: usage: cat FILE");

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
            return Task.FromResult<string?>("touch: missing file operand");

        foreach (var path in arguments)
        {
            if (!context.FileSystem.Touch(context.ResolvePath(path)))
                return Task.FromResult<string?>(
                    $"touch: cannot touch '{path}': Is a directory");
        }

        return Task.FromResult<string?>(string.Empty);
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
            return Task.FromResult<string?>("mkdir: missing operand");

        foreach (var path in paths)
        {
            if (!context.FileSystem.AddDirectory(context.ResolvePath(path)))
                return Task.FromResult<string?>(
                    $"mkdir: cannot create directory '{path}': File exists");
        }

        return Task.FromResult<string?>(string.Empty);
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
                ? "Linux router 2.6.36 #1 SMP Thu Jun 12 08:23:17 UTC 2014 mips GNU/Linux"
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
            return Task.FromResult<string?>(
                "BusyBox v1.19.4 (built-in shell commands)");
        }

        var applet = arguments[0];
        var appletArguments = arguments.Skip(1).ToArray();
        var response = applet switch
        {
            "busybox" =>
                "BusyBox v1.19.4 (built-in shell commands)",
            "echo" =>
                string.Join(' ', appletArguments),
            "cat" when appletArguments.Length == 1 &&
                       context.FileSystem.TryRead(
                           context.ResolvePath(appletArguments[0]),
                           out var contents) =>
                contents.TrimEnd('\r', '\n'),
            "uname" =>
                "Linux router 2.6.36 #1 SMP Thu Jun 12 08:23:17 UTC 2014 mips GNU/Linux",
            "true" or "false" or "sh" or "ls" or "mkdir" or "touch" =>
                string.Empty,
            _ =>
                $"{applet}: applet not found"
        };

        return Task.FromResult<string?>(response);
    }
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