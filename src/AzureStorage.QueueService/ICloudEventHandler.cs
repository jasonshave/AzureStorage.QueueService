using Azure.Messaging;

namespace AzureStorage.QueueService;

/// <summary>
/// Handles one CloudEvent for a processor registered with <c>AddCloudEventProcessor&lt;TData, THandler&gt;()</c>:
/// <c>data</c> is the event's <c>data</c> read as <typeparamref name="TData"/>, and
/// <see cref="QueueMessage{T}.Body"/> is the event itself, for its type, source, id, subject, time and extension
/// attributes. A new instance is resolved from a new dependency-injection scope for every message.
/// </summary>
/// <remarks>
/// Everything <see cref="IQueueMessageHandler{TMessage}"/> says about settlement, retries and cancellation applies:
/// return and the message is completed, throw and it is retried, or settle it yourself through the message. An
/// event with no data arrives with <c>data</c> null. With
/// <see cref="QueueProcessorOptionsBase.DeadLetterOnDeserializationFailure"/> off, an envelope or data that could not be
/// read arrives with <c>data</c> null and <see cref="QueueMessage{T}.Error"/> set.
/// </remarks>
/// <typeparam name="TData">
/// The type of the event's data, read with the client's serializer. Use <see cref="BinaryData"/> for the raw bytes,
/// for example for events whose data is binary (<c>data_base64</c>).
/// </typeparam>
public interface ICloudEventHandler<TData>
{
    /// <summary>Handles one event.</summary>
    /// <param name="message">The message; its <see cref="QueueMessage{T}.Body"/> is the event, for its type, source, id, subject and time.</param>
    /// <param name="data">The event's data read as <typeparamref name="TData"/>; null when the event has none.</param>
    /// <param name="cancellationToken">Cancelled when the host shuts down and the drain timeout has passed.</param>
    ValueTask HandleAsync(QueueMessage<CloudEvent> message, TData? data, CancellationToken cancellationToken);
}
