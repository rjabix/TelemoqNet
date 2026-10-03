using TelemoqNet.Api.Logging;

namespace TelemoqNet.Api.Emulation;

/// Extension point for a future MQTT transport. This boundary deliberately does
/// not parse packets or open sockets; transports must remain separate from
/// in-process device emulation.
/// A future listener should authenticate clients before dispatch, reject malformed
/// or oversized packets, treat retained messages as metadata rather than commands,
/// and record topic/client information in SessionEvent entries.
public interface IMqttSessionService
{
    Task<HoneypotSession> EmulateAsync(
        MqttSessionRequest request,
        IDeviceEmulator device,
        CancellationToken cancellationToken);
}

public sealed record MqttSessionRequest(
    string ClientId,
    string RemoteEndpoint,
    string Topic,
    ReadOnlyMemory<byte> Payload);
