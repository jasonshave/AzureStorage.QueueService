namespace AzureStorage.QueueService;

/// <summary>
/// The processor's receive path. A message comes back with its identity and raw body but without deserialization,
/// so the processor can arm lease renewal first and deserialize inside the delivery boundary. The public
/// <see cref="IQueueReceiver.ReceiveAsync{T}"/> contract is unchanged; this is implemented by <see cref="StorageQueue"/>
/// and used when the processor's queue supports it.
/// </summary>
internal interface IOwnedReceiver
{
    /// <summary>Receives one message, or null when the queue is empty. <see cref="QueueMessage{T}.Error"/> is set only for a decoding failure.</summary>
    ValueTask<QueueMessage<T>?> ReceiveUndeserializedAsync<T>(TimeSpan visibilityTimeout, CancellationToken cancellationToken);

    /// <summary>Fills in <see cref="QueueMessage{T}.Body"/> or <see cref="QueueMessage{T}.Error"/>. Never throws.</summary>
    void Deserialize<T>(QueueMessage<T> message);
}
