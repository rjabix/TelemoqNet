namespace TelemoqNet.Api.Logging;

public sealed class HoneypotSession
{
    public int SchemaVersion { get; init; } = 2;

    public string Protocol { get; init; } = "telnet";

    public string DeviceProfile { get; init; } = "unknown";

    public Guid SessionId { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; set; }

    public string? RemoteEndpoint { get; init; }

    public string? Username { get; set; }

    public bool? AuthenticationSucceeded { get; set; }

    public string? AuthenticationFailureReason { get; set; }

    public string? DisconnectReason { get; set; }

    public List<LoginAttempt> LoginAttempts { get; } = [];

    public List<CommandEvent> Commands { get; } = [];

    public List<SessionEvent> Events { get; } = [];
}

public sealed record LoginAttempt(
    string Username,
    string? Password,
    DateTimeOffset Timestamp,
    bool Successful,
    string? FailureReason = null);

public sealed record CommandEvent(
    string Command,
    DateTimeOffset Timestamp,
    string? Response);

public sealed record SessionEvent(
    long Sequence,
    string Type,
    DateTimeOffset Timestamp,
    string? Detail = null);