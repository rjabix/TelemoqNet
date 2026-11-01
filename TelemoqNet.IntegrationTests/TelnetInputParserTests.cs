using TelemoqNet.Api.Telnet;
using Xunit;

namespace TelemoqNet.IntegrationTests;

public sealed class TelnetInputParserTests
{
    [Fact]
    public async Task StartupOffers_ConsumeAcknowledgmentsAndSuppressDuplicates()
    {
        var (parser, output) = Create([
            255, 253, 1, 255, 253, 1, 255, 253, 3, 255, 253, 3,
            255, 251, 3, 255, 251, 3, 255, 252, 3, 255, 252, 3,
            255, 254, 1, 255, 254, 1, (byte)'a', 13]);
        await parser.NegotiateAsync(default);
        Assert.Equal(new byte[] { 255, 251, 3, 255, 251, 1 }, output.ToArray());
        output.SetLength(0);

        Assert.Equal("a", (await parser.ReadLineAsync(true, default)).Line);
        Assert.Equal(new byte[] { 255, 253, 3, 255, 254, 3, 255, 252, 1 }, output.ToArray());
    }

    [Fact]
    public async Task EchoIsDirectional_AndChangesWithinTheSameLine()
    {
        var (parser, output) = Create([
            (byte)'a', 255, 251, 1, 255, 253, 1, (byte)'b',
            255, 254, 1, (byte)'c', 255, 253, 1, (byte)'d', 13]);
        Assert.Equal("abcd", (await parser.ReadLineAsync(true, default)).Line);
        Assert.Equal(new byte[]
        {
            255, 254, 1, 255, 251, 1, (byte)'b',
            255, 252, 1, 255, 251, 1, (byte)'d'
        }, output.ToArray());
    }

    [Fact]
    public async Task PasswordNeverEchoes_WhenEchoIsEnabled()
    {
        var (parser, output) = Create([255, 253, 1, (byte)'s', 13]);
        Assert.Equal("s", (await parser.ReadLineAsync(false, default)).Line);
        Assert.Equal(new byte[] { 255, 251, 1 }, output.ToArray());
    }

    [Fact]
    public async Task EscapedIac_IsPreservedAndEscapedWhenEchoed()
    {
        var (parser, output) = Create([255, 253, 1, 255, 255, 13]);
        Assert.Equal("\u00ff", (await parser.ReadLineAsync(true, default)).Line);
        Assert.Equal(new byte[] { 255, 251, 1, 255, 255 }, output.ToArray());
    }

    [Fact]
    public async Task SubnegotiationAndCommands_BetweenCrAndLf_PreserveLineBoundary()
    {
        var (parser, _) = Create([
            (byte)'a', 13, 255, 250, 31, 0, 255, 255, 0, 255, 240,
            255, 253, 3, 10, (byte)'b', 13]);
        Assert.Equal("a", (await parser.ReadLineAsync(false, default)).Line);
        Assert.Equal("b", (await parser.ReadLineAsync(false, default)).Line);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(0)]
    public async Task OversizedLine_IsFullyDrainedIncludingTelnetCommands(byte crSuffix)
    {
        var (parser, _) = Create([
            (byte)'a', (byte)'b', (byte)'c', 255, 250, 31, 13, 255, 240,
            (byte)'d', 13, crSuffix, (byte)'o', (byte)'k', 13], maxLine: 2);
        Assert.True((await parser.ReadLineAsync(false, default)).TooLong);
        Assert.Equal("ok", (await parser.ReadLineAsync(false, default)).Line);
    }

    [Theory]
    [InlineData(new byte[] { 255 })]
    [InlineData(new byte[] { 255, 253 })]
    [InlineData(new byte[] { 255, 250, 31, 255 })]
    public async Task TruncatedCommands_AreProtocolErrors(byte[] input)
    {
        var (parser, _) = Create(input);
        await Assert.ThrowsAsync<EndOfStreamException>(() => parser.ReadLineAsync(false, default));
    }

    [Theory]
    [InlineData(new byte[] { 97, 97, 97, 97, 97, 97 })]
    [InlineData(new byte[] { 255, 250, 31, 1, 1, 1 })]
    [InlineData(new byte[] { 255, 253, 3, 255, 253, 3 })]
    public async Task ByteLimit_IncludesNegotiationAndDraining(byte[] input)
    {
        var (parser, _) = Create(input, maxLine: 2, maxBytes: 5);
        await Assert.ThrowsAsync<InvalidDataException>(() => parser.ReadLineAsync(false, default));
    }

    [Fact]
    public async Task InterruptAndEditing_AffectOnlyTheCurrentInput()
    {
        var (parser, _) = Create([
            (byte)'a', (byte)'b', 255, 247, (byte)'c', 13,
            (byte)'d', 255, 248, (byte)'e', 13,
            (byte)'f', 255, 244, (byte)'g', 13]);
        Assert.Equal("ac", (await parser.ReadLineAsync(false, default)).Line);
        Assert.Equal("e", (await parser.ReadLineAsync(false, default)).Line);
        Assert.True((await parser.ReadLineAsync(false, default)).Interrupt);
        Assert.Equal("g", (await parser.ReadLineAsync(false, default)).Line);
    }

    [Fact]
    public async Task CancelledRead_PropagatesCancellation()
    {
        var (parser, _) = Create([(byte)'a']);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parser.ReadLineAsync(false, stop.Token));
    }

    private static (TelnetInputParser Parser, MemoryStream Output) Create(
        byte[] input, int maxLine = 32, int maxBytes = 256)
    {
        var output = new MemoryStream();
        var parser = new TelnetInputParser(new MemoryStream(input), maxLine, maxBytes,
            async (bytes, token) => await output.WriteAsync(bytes, token));
        return (parser, output);
    }
}
