using Azure.Storage.Queues;

namespace AzureStorage.QueueService;

internal interface IQueueClientBuilder
{
    /// <summary>
    /// Constructs the SDK client for the given settings. <paramref name="clientDescription"/> names the client
    /// (for example "Queue client 'orders'") so configuration errors can say which one is at fault.
    /// </summary>
    QueueClient CreateClient(string clientDescription, QueueClientSettings settings);
}
