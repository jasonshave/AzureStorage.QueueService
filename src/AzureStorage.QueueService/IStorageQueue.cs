using Azure.Storage.Queues.Models;

namespace AzureStorage.QueueService;

/// <summary>
/// Everything for one queue: sending, receiving and administration. What <see cref="IQueueClientFactory.GetQueue()"/>
/// hands out. Inject <see cref="IQueueSender"/> or <see cref="IQueueReceiver"/> instead when a class needs only one side.
/// </summary>
public interface IStorageQueue : IQueueSender, IQueueReceiver
{
    /// <summary>Creates the queue, with <paramref name="metadata"/>, unless it already exists (an existing queue keeps its own metadata).</summary>
    ValueTask CreateIfNotExistsAsync(IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes every message on the queue, hidden ones included.</summary>
    ValueTask ClearAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The queue's metadata and approximate message count. The count includes messages that are hidden (being
    /// processed, or waiting for a retry), so it is not the number a receive would return.
    /// </summary>
    ValueTask<QueueProperties> GetPropertiesAsync(CancellationToken cancellationToken = default);
}
