using Azure.Storage.Queues;
using JasonShave.AzureStorage.QueueService.Interfaces;
using JasonShave.AzureStorage.QueueService.Models;

namespace JasonShave.AzureStorage.QueueService.Services;

internal class AzureQueueClientFactory : IQueueClientFactory
{
    private readonly IDictionary<string, QueueClient> _queueClientsByQueueName = new Dictionary<string, QueueClient>();

    public QueueClient GetQueueClient(string queueName)
    {
        if (_queueClientsByQueueName.TryGetValue(queueName, out var queueClient))
        {
            return queueClient;
        }

        throw new InvalidOperationException($"QueueClient not registered for queue: {queueName}.");
    }

    public bool RegisterQueueClient(QueueClientSettings settings)
    {
        QueueClient queueClient = new QueueClient(settings.ConnectionString, settings.QueueName);
        return _queueClientsByQueueName.TryAdd(settings.QueueName, queueClient);
    }
}