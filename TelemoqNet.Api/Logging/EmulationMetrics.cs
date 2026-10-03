using System.Diagnostics.Metrics;

namespace TelemoqNet.Api.Logging;

public static class EmulationMetrics
{
    private static readonly Meter Meter = new("TelemoqNet.Emulation");

    public static readonly Counter<long> Connections = Meter.CreateCounter<long>("telnet.connections");
    public static readonly Counter<long> RejectedConnections = Meter.CreateCounter<long>("telnet.connections.rejected");
    public static readonly Counter<long> AuthenticationAttempts = Meter.CreateCounter<long>("telnet.authentication.attempts");
    public static readonly Counter<long> Commands = Meter.CreateCounter<long>("telnet.commands");
    public static readonly Counter<long> ProtocolErrors = Meter.CreateCounter<long>("telnet.protocol.errors");
    public static readonly Counter<long> Timeouts = Meter.CreateCounter<long>("telnet.timeouts");
    public static readonly Counter<long> PersistenceFailures = Meter.CreateCounter<long>("telnet.persistence.failures");
}
