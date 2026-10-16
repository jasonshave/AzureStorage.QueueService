using Azure.Storage.Queues.Models;

namespace AzureStorage.QueueService;

/// <summary>
/// Everything for one queue: sending, receiving and administration. What <see cref="IQueueClientFactory.GetQueue()"/>
/// hands out. Inject <see cref="IQueueSender"/> or <see cref="IQueueReceiver"/> instead when a class needs only one side.
/// </summary>
public interface IStorageQueue : IQueueSender, IQueueReceiver
{
    ValueTask CreateIfNotExistsAsync(IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);

    ValueTask ClearAsync(CancellationToken cancellationToken = default);

    ValueTask<QueueProperties> GetPropertiesAsync(CancellationToken cancellationToken = default);
}
