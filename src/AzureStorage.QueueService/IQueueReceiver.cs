namespace AzureStorage.QueueService;

/// <summary>
/// Receives messages from one queue. The caller settles each message (<see cref="QueueMessage{T}.CompleteAsync"/>
/// and friends) and owns the polling loop, backoff and error handling. For a loop the library runs for you, register
/// a processor instead.
/// </summary>
public interface IQueueReceiver : INamedQueue
{
    /// <summary>
    /// Receives up to <paramref name="maxMessages"/> messages (1 to 32) and hides them from other consumers for
    /// <paramref name="visibilityTimeout"/>, which defaults to <c>QueueClientSettings.VisibilityTimeout</c> and then to
    /// the service default of 30 seconds. A message whose body cannot be deserialized is still returned, with
    /// <see cref="QueueMessage{T}.Error"/> set, so it can be dead-lettered rather than block the queue.
    /// </summary>
    /// <exception cref="Azure.RequestFailedException">The service rejected the receive.</exception>
    ValueTask<IReadOnlyList<QueueMessage<T>>> ReceiveAsync<T>(int maxMessages = 1, TimeSpan? visibilityTimeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Looks at up to <paramref name="maxMessages"/> messages without receiving them.
    /// </summary>
    ValueTask<IReadOnlyList<PeekedQueueMessage<T>>> PeekAsync<T>(int maxMessages = 1, CancellationToken cancellationToken = default);
}
