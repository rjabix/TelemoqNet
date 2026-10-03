using System.Text.Json;
using Azure.Storage.Blobs;

namespace TelemoqNet.Api.Logging;

public sealed class AzureBlobSessionStore : ISessionStore
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<AzureBlobSessionStore> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public AzureBlobSessionStore(
        IConfiguration configuration,
        ILogger<AzureBlobSessionStore> logger)
    {
        _logger = logger;

        var connectionString =
            configuration["AzureStorage:ConnectionString"];

        var containerName =
            configuration["AzureStorage:Container"] ?? "honeypot-sessions";

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "AzureStorage:ConnectionString is not configured.");
        }

        var service = new BlobServiceClient(connectionString);

        _container = service.GetBlobContainerClient(containerName);
    }

    public async Task StoreAsync(
        HoneypotSession session,
        CancellationToken cancellationToken)
    {
        await _container.CreateIfNotExistsAsync(
            cancellationToken: cancellationToken);

        var date = session.StartedAt.UtcDateTime;

        var blobName =
            $"{date:yyyy/MM/dd}/{session.SessionId}.json";

        var blob = _container.GetBlobClient(blobName);

        var json = JsonSerializer.Serialize(
            session,
            JsonOptions);

        await blob.UploadAsync(
            BinaryData.FromString(json),
            overwrite: true,
            cancellationToken);

        _logger.LogInformation(
            "Stored honeypot session {SessionId} as {BlobName}",
            session.SessionId,
            blobName);
    }
}