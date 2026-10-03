using System.Text;
using System.Text.Json;

namespace TelemoqNet.Api.Logging;

public sealed class LocalCsvSessionStore : ISessionStore
{
    private const string DefaultFilePath = "data/honeypot-sessions.csv";
    private static readonly SemaphoreSlim FileLock = new(1, 1);

    private readonly IConfiguration _configuration;
    private readonly ILogger<LocalCsvSessionStore> _logger;

    public LocalCsvSessionStore(
        IConfiguration configuration,
        ILogger<LocalCsvSessionStore> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StoreAsync(
        HoneypotSession session,
        CancellationToken cancellationToken)
    {
        var filePath =
            _configuration["LocalStorage:CsvPath"] ?? DefaultFilePath;

        var directory = Path.GetDirectoryName(filePath);

        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var row = string.Join(
            ',',
            Escape(session.SessionId.ToString()),
            Escape(session.SchemaVersion.ToString()),
            Escape(session.Protocol),
            Escape(session.DeviceProfile),
            Escape(session.StartedAt.ToString("O")),
            Escape(session.EndedAt?.ToString("O")),
            Escape(session.RemoteEndpoint),
            Escape(session.Username),
            Escape(session.AuthenticationSucceeded?.ToString()),
            Escape(session.AuthenticationFailureReason),
            Escape(session.DisconnectReason),
            Escape(JsonSerializer.Serialize(session.LoginAttempts)),
            Escape(JsonSerializer.Serialize(session.Commands)),
            Escape(JsonSerializer.Serialize(session.Events)));

        await FileLock.WaitAsync(cancellationToken);

        try
        {
            var writeHeader =
                !File.Exists(filePath) ||
                new FileInfo(filePath).Length == 0;

            await using var stream = new FileStream(
                filePath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);

            await using var writer = new StreamWriter(
                stream,
                Encoding.UTF8);

            if (writeHeader)
            {
                await writer.WriteLineAsync(
                    "SessionId,SchemaVersion,Protocol,DeviceProfile,StartedAt,EndedAt,RemoteEndpoint,Username,AuthenticationSucceeded,AuthenticationFailureReason,DisconnectReason,LoginAttempts,Commands,Events");
            }

            await writer.WriteLineAsync(row);
        }
        finally
        {
            FileLock.Release();
        }

        _logger.LogInformation(
            "Stored local honeypot session {SessionId} in {FilePath}",
            session.SessionId, filePath);
    }

    private static string Escape(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            text = "'" + text;
        return $"\"{text.Replace("\"", "\"\"")}\"";
    }
}
