namespace AzureStorage.QueueService;

/// <summary>
/// Something bound to one queue. The common base of <see cref="IQueueSender"/> and <see cref="IQueueReceiver"/>,
/// so <see cref="IStorageQueue"/> inherits a single <see cref="QueueName"/>.
/// </summary>
public interface INamedQueue
{
    /// <summary>The name of the queue on the service.</summary>
    string QueueName { get; }
}
