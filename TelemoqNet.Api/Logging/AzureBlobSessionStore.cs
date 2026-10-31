extern alias AzureIdentity;

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

        var blobUriValue = configuration["AzureStorage:BlobUri"];

        var containerName =
            configuration["AzureStorage:Container"] ?? "honeypot-sessions";

        if (!Uri.TryCreate(blobUriValue, UriKind.Absolute, out var blobUri))
        {
            throw new InvalidOperationException(
                "AzureStorage:BlobUri must be configured as an absolute URI.");
        }

        var service = new BlobServiceClient(
            blobUri,
            new AzureIdentity::Azure.Identity.DefaultAzureCredential());

        _container = service.GetBlobContainerClient(containerName);
    }

    public async Task StoreAsync(
        HoneypotSession session,
        CancellationToken cancellationToken)
    {
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