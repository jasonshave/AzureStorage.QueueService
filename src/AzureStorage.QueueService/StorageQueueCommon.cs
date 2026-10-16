using System.Diagnostics;
using AzureStorage.QueueService.Telemetry;

namespace AzureStorage.QueueService;

/// <summary>
/// What <see cref="StorageQueue"/> and <see cref="InMemoryStorageQueue"/> share: the service's limits, the
/// trace-context envelope on the way out and in, and deserialization. One copy, so the in-memory queue a test runs
/// against cannot drift from the code that runs in production.
/// </summary>
internal static class StorageQueueCommon
{
    public const int ServiceMaxMessages = 32;

    /// <summary>The service's "never expires": -1 whole second.</summary>
    public static readonly TimeSpan InfiniteTimeToLive = TimeSpan.FromSeconds(-1);

    public static void ValidateMaxMessages(int maxMessages)
    {
        if (maxMessages is < 1 or > ServiceMaxMessages)
            throw new ArgumentOutOfRangeException(nameof(maxMessages), maxMessages, $"The service allows 1 to {ServiceMaxMessages} messages per receive.");
    }

    /// <summary>.NET's infinite sentinel is -1 ms; the service requires -1 whole second.</summary>
    public static TimeSpan? ServiceTimeToLive(TimeSpan? timeToLive) =>
        timeToLive == Timeout.InfiniteTimeSpan ? InfiniteTimeToLive : timeToLive;

    /// <summary>
    /// Serializes <paramref name="message"/> and, when <paramref name="propagateTraceContext"/> is on and there is a
    /// <paramref name="creation"/> span to carry, wraps it in the trace-context envelope. <paramref name="serializedSize"/>
    /// is the serializer's output alone, which is what the message size metric has always measured.
    /// </summary>
    public static BinaryData Encode<T>(IQueueMessageSerializer serializer, T message, bool propagateTraceContext, Activity? creation, out int serializedSize)
    {
        BinaryData body = serializer.Serialize(message);
        serializedSize = body.ToMemory().Length;
        if (!propagateTraceContext || creation is null) return body;

        // The library's own serializer always writes exactly one JSON value, so there is no need to parse it to find out.
        return TraceContextEnvelope.Wrap(body, creation, payloadIsJson: serializer is JsonQueueMessageSerializer) ?? body;
    }

    /// <summary>
    /// A received message, not yet deserialized. Unwrapped whatever the client's own setting, which governs sending
    /// only: a receiver reads what any sender put on the queue.
    /// </summary>
    public static QueueMessage<T> Received<T>(QueueMessageInfo info, BinaryData wire, IQueueMessageOperations operations)
    {
        if (!TraceContextEnvelope.TryUnwrap(wire, out var payload, out var creationContext))
            return new QueueMessage<T>(info, wire, default, null, operations);

        // The handler sees the message the sender serialized; dead-lettering still copies the whole envelope.
        return new QueueMessage<T>(info, payload, default, null, operations) { WireBody = wire, CreationContext = creationContext };
    }

    /// <summary>A peeked message's body: the payload inside an envelope, or the body as it is.</summary>
    public static BinaryData Peeked(BinaryData wire) =>
        TraceContextEnvelope.TryUnwrap(wire, out var payload, out _) ? payload : wire;

    public static void Deserialize<T>(IQueueMessageSerializer serializer, QueueMessage<T> message)
    {
        if (message.Error is not null) return; // a decoding failure; there is nothing to deserialize
        var (body, error) = TryDeserialize<T>(serializer, message.RawBody);
        message.SetBody(body, error);
    }

    public static (T? Body, DeserializationException? Error) TryDeserialize<T>(IQueueMessageSerializer serializer, BinaryData raw)
    {
        try
        {
            return (serializer.Deserialize<T>(raw), null);
        }
        catch (DeserializationException e)
        {
            return (default, e);
        }
        catch (Exception e)
        {
            // A custom serializer that throws something else still must not take the receive down.
            return (default, new DeserializationException($"Could not deserialize the queue message as {typeof(T).Name}: {e.Message}", e));
        }
    }
}
