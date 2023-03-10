using Azure.Storage.Queues;
using JasonShave.AzureStorage.QueueService.Models;

namespace JasonShave.AzureStorage.QueueService.Interfaces;

public interface IQueueClientBuilder
{
    QueueClient CreateClient(QueueClientSettings settings);

    Task<QueueClient> CreateClientAsync(QueueClientSettings settings);
}