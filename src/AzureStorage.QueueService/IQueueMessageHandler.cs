namespace AzureStorage.QueueService;

/// <summary>
/// Handles one message for a processor registered with <c>AddProcessor&lt;TMessage, THandler&gt;()</c>. A new instance
/// is resolved from a new dependency-injection scope for every message, so scoped services work as they do in a
/// controller.
/// </summary>
/// <remarks>
/// Return normally and the processor completes the message; throw and it is abandoned and retried later; or settle
/// it yourself with <see cref="QueueMessage{T}.CompleteAsync"/>, <see cref="QueueMessage{T}.AbandonAsync"/> or
/// <see cref="QueueMessage{T}.DeadLetterAsync"/> and the processor leaves it alone. With
/// <see cref="QueueProcessorOptions.DeadLetterOnDeserializationFailure"/> off, <see cref="QueueMessage{T}.Body"/>
/// can be null with <see cref="QueueMessage{T}.Error"/> set. The token is cancelled when the host shuts down.
/// </remarks>
public interface IQueueMessageHandler<TMessage>
{
    ValueTask HandleAsync(QueueMessage<TMessage> message, CancellationToken cancellationToken);
}
