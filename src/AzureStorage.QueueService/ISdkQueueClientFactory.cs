using Azure.Storage.Queues;

namespace AzureStorage.QueueService;

/// <summary>
/// Constructs the Azure SDK client for a set of settings. Internal seam so tests can hand the library a mock.
/// </summary>
internal interface ISdkQueueClientFactory
{
    /// <param name="clientDescription">Names the client (for example "Queue client 'orders'") so configuration errors can say which one is at fault.</param>
    QueueClient CreateClient(string clientDescription, QueueClientSettings settings);
}
