using Azure.Storage.Queues;

namespace AzureStorage.QueueService;

internal class QueueClientBuilder : IQueueClientBuilder
{
    public QueueClient CreateClient(QueueClientSettings settings)
    {
        var client = new QueueClient(settings.ConnectionString, settings.QueueName);

        if (settings.CreateIfNotExists) client.CreateIfNotExists();

        return client;
    }

    public async Task<QueueClient> CreateClientAsync(QueueClientSettings settings)
    {
        var client = new QueueClient(settings.ConnectionString, settings.QueueName);

        if (settings.CreateIfNotExists) await client.CreateIfNotExistsAsync();

        return client;
    }
}