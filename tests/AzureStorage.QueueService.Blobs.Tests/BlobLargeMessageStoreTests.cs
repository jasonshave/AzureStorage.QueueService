using Azure.Core;
using Azure.Storage.Blobs;
using AzureStorage.QueueService.Blobs;
using FluentAssertions;

namespace AzureStorage.QueueService.Blobs.Tests;

/// <summary>Checks that need no storage: which references the store accepts, and where Blob storage is derived from.</summary>
public class BlobLargeMessageStoreTests
{
    private static readonly BlobContainerClient Bodies = new(new Uri("https://acct.blob.core.windows.net/bodies"));

    [Theory(DisplayName = "Store: a reference that is not a blob in the store's own container is refused before any request")]
    [InlineData("not a uri")]
    [InlineData("file:///c:/bodies/orders/x")]
    [InlineData("https://evil.example.com/bodies/orders/x")]
    [InlineData("https://other.blob.core.windows.net/bodies/orders/x")]
    [InlineData("http://acct.blob.core.windows.net/bodies/orders/x")]
    [InlineData("https://acct.blob.core.windows.net:8443/bodies/orders/x")]
    [InlineData("https://acct.blob.core.windows.net/other/orders/x")]
    [InlineData("https://acct.blob.core.windows.net/bodies-evil/orders/x")]
    [InlineData("https://acct.blob.core.windows.net/bodies/../other/x")]
    [InlineData("https://acct.blob.core.windows.net/bodies/%2e%2e/other/x")]
    [InlineData("https://acct.blob.core.windows.net/bodies/orders/x?sv=2025-01-01&sig=abc")]
    [InlineData("https://acct.blob.core.windows.net/bodies/")]
    public async Task Refuses_References_Outside_Its_Container(string reference)
    {
        // No credential and an unreachable account: anything that got as far as a request would fail differently.
        var store = new BlobLargeMessageStore(Bodies);

        await store.Invoking(s => s.DownloadAsync(reference, CancellationToken.None).AsTask()).Should().ThrowAsync<ArgumentException>();
        await store.Invoking(s => s.DeleteAsync(reference, CancellationToken.None).AsTask()).Should().ThrowAsync<ArgumentException>();
        store.BlobFor("https://ACCT.blob.core.windows.net/bodies/orders/2026/10/16/abc").Name.Should().Be("orders/2026/10/16/abc", "its own blobs are still accepted");
    }

    [Theory(DisplayName = "Endpoints: the Blob endpoint is derived from the queue endpoint for Azure hosts and the emulator, and not guessed otherwise")]
    [InlineData("https://acct.queue.core.windows.net/orders", "https://acct.blob.core.windows.net/")]
    [InlineData("https://acct.queue.core.windows.net/", "https://acct.blob.core.windows.net/")]
    [InlineData("https://acct.queue.core.chinacloudapi.cn/orders", "https://acct.blob.core.chinacloudapi.cn/")]
    [InlineData("http://127.0.0.1:10001/devstoreaccount1/orders", "http://127.0.0.1:10000/devstoreaccount1/")]
    [InlineData("https://queues.contoso.com/orders", null)]
    public void Derives_Blob_Endpoint(string queueEndpoint, string? expected)
    {
        var derived = BlobEndpoints.ServiceUriFor(new Uri(queueEndpoint))?.AbsoluteUri;
        derived.Should().Be(expected);
    }

    [Fact(DisplayName = "Endpoints: a connection string is used as is, and an underivable endpoint names the client")]
    public void Service_Client_From_Settings()
    {
        BlobEndpoints.ServiceClientFor(new QueueClientSettings { ConnectionString = "UseDevelopmentStorage=true" }, "Queue client 'orders'")
            .Uri.AbsoluteUri.TrimEnd('/').Should().Be("http://127.0.0.1:10000/devstoreaccount1");

        var key = Convert.ToBase64String(new byte[64]);
        BlobEndpoints.ServiceClientFor(new QueueClientSettings { ConnectionString = $"DefaultEndpointsProtocol=https;AccountName=acct;AccountKey={key};EndpointSuffix=core.windows.net" }, "Queue client 'orders'")
            .Uri.Should().Be(new Uri("https://acct.blob.core.windows.net/"));

        var custom = new QueueClientSettings { EndpointUri = new Uri("https://queues.contoso.com/orders"), TokenCredential = new NoCredential() };
        FluentActions.Invoking(() => BlobEndpoints.ServiceClientFor(custom, "Queue client 'orders'"))
            .Should().Throw<InvalidOperationException>().WithMessage("Queue client 'orders'*queues.contoso.com*BlobContainerClient*");
    }

    private sealed class NoCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
