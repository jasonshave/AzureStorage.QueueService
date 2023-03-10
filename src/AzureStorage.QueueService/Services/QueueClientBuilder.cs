using Azure.Storage.Queues;
using JasonShave.AzureStorage.QueueService.Interfaces;
using JasonShave.AzureStorage.QueueService.Models;

namespace JasonShave.AzureStorage.QueueService.Services;

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