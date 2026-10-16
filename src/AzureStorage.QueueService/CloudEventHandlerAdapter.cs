using Azure.Messaging;

namespace AzureStorage.QueueService;

/// <summary>
/// The processor handler behind <c>AddCloudEventProcessor&lt;TData, THandler&gt;()</c>. The processor receives and
/// settles the CloudEvent envelope like any other message, and reads its data as part of reading the body
/// (<see cref="ReadData(QueueMessage{CloudEvent}, IQueueMessageSerializer)"/>, its after-deserialize step); this hands the event and its data to the application's
/// handler. Created per message, in the message's scope.
/// </summary>
internal sealed class CloudEventHandlerAdapter<TData, THandler> : IQueueMessageHandler<CloudEvent>
    where THandler : class, ICloudEventHandler<TData>
{
    private readonly THandler _handler;

    public CloudEventHandlerAdapter(THandler handler)
    {
        _handler = handler;
    }

    // Data that could not be read only reaches here with DeadLetterOnDeserializationFailure off: Error is set, and
    // the handler gets null data.
    public ValueTask HandleAsync(QueueMessage<CloudEvent> message, CancellationToken cancellationToken) =>
        _handler.HandleAsync(message, message.HandlerData is TData data ? data : default, cancellationToken);

    /// <summary>
    /// Reads the event's data as <typeparamref name="TData"/> into <see cref="QueueMessage{T}.HandlerData"/>, or sets
    /// <see cref="QueueMessage{T}.Error"/>. Run by the processor straight after the envelope deserialized, so data that
    /// cannot be read is an unreadable body to the processor: its options decide, and its metric and outcome record it.
    /// </summary>
    public static void ReadData(QueueMessage<CloudEvent> message, IQueueMessageSerializer serializer)
    {
        // A body of JSON null deserializes without error but is not an event either.
        if (message.Body is not { } cloudEvent)
        {
            message.SetBody(null, new DeserializationException("The queue message is JSON null, not a CloudEvent."));
            return;
        }

        var (data, error) = ReadData(cloudEvent, serializer);
        if (error is not null) message.SetBody(cloudEvent, error);
        else message.HandlerData = data;
    }

    private static (TData? Data, DeserializationException? Error) ReadData(CloudEvent cloudEvent, IQueueMessageSerializer serializer)
    {
        // No data at all is a valid event.
        if (cloudEvent.Data is null) return (default, null);

        // The raw bytes, untouched: the only reading that works for binary (data_base64) data, and an escape hatch
        // for any shape the serializer cannot read.
        if (typeof(TData) == typeof(BinaryData)) return ((TData)(object)cloudEvent.Data, null);

        try
        {
            return (serializer.Deserialize<TData>(cloudEvent.Data), null);
        }
        catch (Exception e)
        {
            var contentType = cloudEvent.DataContentType is { Length: > 0 } type ? $", content type '{type}'" : "";
            return (default, new DeserializationException(
                $"The data of CloudEvent '{cloudEvent.Id}' (type '{cloudEvent.Type}'{contentType}) could not be read as {typeof(TData).Name}: {e.Message}", e));
        }
    }
}
