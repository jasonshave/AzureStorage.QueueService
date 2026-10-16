namespace AzureStorage.QueueService;

/// <summary>
/// Handles a batch of messages for a processor registered with <c>AddBatchProcessor&lt;TMessage, THandler&gt;()</c>: up
/// to <see cref="QueueBatchProcessorOptions.MaxBatchSize"/> messages from one receive, in one call, so the handler can
/// write them to a database or call an API in bulk. A new instance is resolved from a new dependency-injection scope for
/// every batch.
/// </summary>
/// <remarks>
/// <para>
/// Every message in the batch is still its own message: its own lease renewal, dequeue count, retry delay and
/// settlement. Return normally and the processor completes every message you did not settle yourself. Throw and the
/// error policy (<c>DeadLetterOn&lt;TException&gt;()</c>, then <see cref="QueueProcessorOptionsBase.OnHandlerError"/>) is
/// applied to each message you had not settled, which otherwise is abandoned and retried after
/// <see cref="QueueProcessorOptionsBase.RetryDelay"/> for its own dequeue count. Settle individual messages with
/// <see cref="QueueMessage{T}.CompleteAsync"/>, <see cref="QueueMessage{T}.AbandonAsync"/> or
/// <see cref="QueueMessage{T}.DeadLetterAsync(string?, CancellationToken)"/> (for example, dead-letter one bad item and
/// return) and the processor leaves those alone.
/// </para>
/// <para>
/// Messages whose body cannot be read, and messages delivered more than
/// <see cref="QueueProcessorOptionsBase.MaxDequeueCount"/> times, are dead-lettered on their own and left out, so the
/// batch can be smaller than what was received; when none are left the handler is not called. With
/// <see cref="QueueProcessorOptionsBase.DeadLetterOnDeserializationFailure"/> off, an unreadable message stays in the
/// batch with <see cref="QueueMessage{T}.Body"/> null and <see cref="QueueMessage{T}.Error"/> set. Middleware does not
/// run for batches. The token is cancelled when the host shuts down.
/// </para>
/// </remarks>
public interface IQueueBatchHandler<TMessage>
{
    /// <summary>Handles one batch.</summary>
    /// <param name="messages">At least one message, each with its body read as <typeparamref name="TMessage"/>, its identity, and the operations that settle it.</param>
    /// <param name="cancellationToken">Cancelled when the host shuts down and the drain timeout has passed.</param>
    ValueTask HandleAsync(IReadOnlyList<QueueMessage<TMessage>> messages, CancellationToken cancellationToken);
}
