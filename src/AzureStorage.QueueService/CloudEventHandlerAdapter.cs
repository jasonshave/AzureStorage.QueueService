using Azure.Messaging;

namespace AzureStorage.QueueService;

/// <summary>
/// The processor handler behind <c>AddCloudEventProcessor&lt;TData, THandler&gt;()</c>. The processor receives and
/// settles the CloudEvent envelope like any other message; this reads its data and hands both to the application's
/// handler. Created per message, in the message's scope.
/// </summary>
internal sealed class CloudEventHandlerAdapter<TData, THandler> : IQueueMessageHandler<CloudEvent>
    where THandler : class, ICloudEventHandler<TData>
{
    private readonly THandler _handler;
    private readonly IQueueMessageSerializer _serializer;
    private readonly bool _deadLetterOnDeserializationFailure;

    public CloudEventHandlerAdapter(THandler handler, IQueueMessageSerializer serializer, bool deadLetterOnDeserializationFailure)
    {
        _handler = handler;
        _serializer = serializer;
        _deadLetterOnDeserializationFailure = deadLetterOnDeserializationFailure;
    }

    public async ValueTask HandleAsync(QueueMessage<CloudEvent> message, CancellationToken cancellationToken)
    {
        var cloudEvent = message.Body;
        TData? data = default;
        DeserializationException? error = null;

        // An envelope that failed to deserialize only reaches here with DeadLetterOnDeserializationFailure off; the
        // handler gets it as it is. A body of JSON null deserializes without error but is not an event either.
        if (message.Error is null)
        {
            if (cloudEvent is null) error = new DeserializationException("The queue message is JSON null, not a CloudEvent.");
            else (data, error) = ReadData(cloudEvent);
        }

        if (error is not null)
        {
            if (_deadLetterOnDeserializationFailure)
            {
                // The processor's own wording, so the poison queue reads the same whichever layer found the problem.
                // Not tied to the shutdown token, as in the processor: the decision is already made. If the move
                // fails, the processor abandons the message for retry as it does for any handler that throws.
                await message.DeadLetterAsync($"the body could not be read: {error.Message}", CancellationToken.None);
                return;
            }

            message.SetBody(cloudEvent, error);
        }

        await _handler.HandleAsync(message, data, cancellationToken);
    }

    private (TData? Data, DeserializationException? Error) ReadData(CloudEvent cloudEvent)
    {
        // No data at all is a valid event.
        if (cloudEvent.Data is null) return (default, null);

        // The raw bytes, untouched: the only reading that works for binary (data_base64) data, and an escape hatch
        // for any shape the serializer cannot read.
        if (typeof(TData) == typeof(BinaryData)) return ((TData)(object)cloudEvent.Data, null);

        try
        {
            return (_serializer.Deserialize<TData>(cloudEvent.Data), null);
        }
        catch (Exception e)
        {
            var contentType = cloudEvent.DataContentType is { Length: > 0 } type ? $", content type '{type}'" : "";
            return (default, new DeserializationException(
                $"The data of CloudEvent '{cloudEvent.Id}' (type '{cloudEvent.Type}'{contentType}) could not be read as {typeof(TData).Name}: {e.Message}", e));
        }
    }
}
