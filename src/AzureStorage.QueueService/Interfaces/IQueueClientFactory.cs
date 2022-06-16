using Azure.Storage.Queues;
using JasonShave.AzureStorage.QueueService.Models;

namespace JasonShave.AzureStorage.QueueService.Interfaces;

internal interface IQueueClientFactory
{
    QueueClient GetQueueClient(string queueName);

    bool RegisterQueueClient(QueueClientSettings settings);
}