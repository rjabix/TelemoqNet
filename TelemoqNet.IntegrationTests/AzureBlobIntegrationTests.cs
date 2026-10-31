extern alias AzureIdentity;

using System.Text.Json;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TelemoqNet.Api.Logging;
using Xunit;
using Xunit.Abstractions;

namespace TelemoqNet.IntegrationTests;

public sealed class AzureBlobIntegrationTests
{
    private readonly ITestOutputHelper _testOutputHelper;

    public AzureBlobIntegrationTests(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper;
    }

    [Fact]
    [Trait("Category", "AzureBlob")]
    public async Task StoreAsync_PersistsCompleteSessionAsBlobJson()
    {
        var blobUri = Environment.GetEnvironmentVariable("AZURE_STORAGE_BLOB_URI");
        if (string.IsNullOrWhiteSpace(blobUri))
        {
            _testOutputHelper.WriteLine(
                "Azure Blob integration test not run; set AZURE_STORAGE_BLOB_URI " +
                "to a pre-provisioned blob service endpoint.");
            return;
        }

        const string containerName = "integration-sessions";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureStorage:AccountUrl"] = blobUri,
                ["AzureStorage:Container"] = containerName
            })
            .Build();
        var store = new AzureBlobSessionStore(
            configuration,
            NullLogger<AzureBlobSessionStore>.Instance);
        var blobContainer = new BlobContainerClient(
            new Uri(blobUri),
            new AzureIdentity::Azure.Identity.DefaultAzureCredential());
        var startedAt = DateTimeOffset.UtcNow;
        var session = new HoneypotSession
        {
            SessionId = Guid.NewGuid(),
            StartedAt = startedAt,
            EndedAt = startedAt.AddSeconds(3),
            Protocol = "mqtt",
            DeviceProfile = "SmartHome",
            RemoteEndpoint = "127.0.0.1:49152",
            Username = "sensor",
            AuthenticationSucceeded = true,
            DisconnectReason = "client_disconnect"
        };
        session.LoginAttempts.Add(new(
            "sensor",
            null,
            startedAt,
            true));
        session.Commands.Add(new(
            "publish",
            startedAt.AddMilliseconds(10),
            "payload captured"));
        session.Events.Add(new(
            1,
            "connection.opened",
            startedAt,
            session.RemoteEndpoint));
        session.Events.Add(new(
            2,
            "mqtt.publish",
            startedAt.AddMilliseconds(10),
            "topic=home/test,length=7,sampleBase64=cGF5bG9hZA=="));
        session.Events.Add(new(
            3,
            "connection.closed",
            session.EndedAt.Value,
            session.DisconnectReason));

        await store.StoreAsync(session, CancellationToken.None);

        Assert.True(await blobContainer.ExistsAsync());
        var blob = blobContainer.GetBlobClient(
            $"{startedAt.UtcDateTime:yyyy/MM/dd}/{session.SessionId}.json");
        Assert.True(await blob.ExistsAsync());

        var storedJson = await blob.DownloadContentAsync();
        using var document = JsonDocument.Parse(storedJson.Value.Content.ToString());
        var root = document.RootElement;
        Assert.Equal(session.SessionId.ToString(), root.GetProperty("SessionId").GetString());
        Assert.Equal("mqtt", root.GetProperty("Protocol").GetString());
        Assert.Equal("sensor", root.GetProperty("Username").GetString());
        Assert.True(root.GetProperty("AuthenticationSucceeded").GetBoolean());
        Assert.Equal(1, root.GetProperty("LoginAttempts").GetArrayLength());
        Assert.Equal(
            "payload captured",
            root.GetProperty("Commands")[0].GetProperty("Response").GetString());
        var events = root.GetProperty("Events");
        Assert.Equal(3, events.GetArrayLength());
        Assert.Equal("mqtt.publish", events[1].GetProperty("Type").GetString());
        Assert.Equal(
            "topic=home/test,length=7,sampleBase64=cGF5bG9hZA==",
            events[1].GetProperty("Detail").GetString());
    }
}
