using Azure.Storage.Queues;

namespace AzureStorage.QueueService;

internal interface IQueueClientBuilder
{
    QueueClient CreateClient(QueueClientSettings settings);

    Task<QueueClient> CreateClientAsync(QueueClientSettings settings);
}