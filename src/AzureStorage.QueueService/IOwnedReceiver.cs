namespace AzureStorage.QueueService;

/// <summary>
/// The processor's receive path. Messages come back with their identity and raw body but without deserialization, so
/// the processor can arm lease renewal first and do the slow parts (downloading a body kept in Blob storage,
/// deserializing) inside the delivery boundary. The public <see cref="IQueueReceiver.ReceiveAsync{T}"/> contract is
/// unchanged; this is implemented by <see cref="StorageQueue"/> and used when the processor's queue supports it.
/// </summary>
internal interface IOwnedReceiver
{
    /// <summary>
    /// Receives up to <paramref name="maxMessages"/> messages. <see cref="QueueMessage{T}.Error"/> is set only for one
    /// that cannot be decoded or unwrapped. A failure is thrown without being logged: the processor logs it.
    /// </summary>
    ValueTask<IReadOnlyList<QueueMessage<T>>> ReceiveForProcessingAsync<T>(int maxMessages, TimeSpan visibilityTimeout, CancellationToken cancellationToken);

    /// <summary>Brings in a body kept outside the queue. Returns the problem, or null; never throws except for cancellation.</summary>
    ValueTask<DeserializationException?> ResolvePayloadAsync(MessageLease message, CancellationToken cancellationToken);

    /// <summary>Fills in <see cref="QueueMessage{T}.Body"/> or <see cref="QueueMessage{T}.Error"/>. Never throws.</summary>
    void Deserialize<T>(QueueMessage<T> message);
}
