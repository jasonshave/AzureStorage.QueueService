using System.Diagnostics;
using System.IO.Compression;

namespace AzureStorage.QueueService;

/// <summary>Names of headers the library itself reads or writes. Any other name is the application's.</summary>
public static class QueueMessageHeaders
{
    /// <summary>
    /// The message's type, which a routed processor uses to choose the handler. Set by <see cref="SendOptions.MessageType"/>,
    /// or on every send by <see cref="QueueClientSettings.SendMessageType"/>.
    /// </summary>
    public const string MessageType = "message-type";

    /// <summary>A conventional name for a correlation id; the library does not read it.</summary>
    public const string CorrelationId = "correlation-id";

    /// <summary>A conventional name for the body's content type; the library does not read it.</summary>
    public const string ContentType = "content-type";
}

/// <summary>A message ready to send: the bytes for the queue, and the stored body to delete if the send fails.</summary>
internal readonly record struct EncodedMessage(BinaryData Wire, int SerializedSize, string? BlobReference);

/// <summary>
/// Turns one client's messages into what goes on the queue and back: serialization, the envelope (trace context,
/// headers), compression, the size limit and large bodies kept elsewhere. Shared by the SDK and in-memory queues
/// through <see cref="StorageQueue"/>, so a test runs exactly the production path.
/// </summary>
internal sealed class MessageCodec
{
    /// <summary>The service's limit on a message as it goes on the queue.</summary>
    public const int MaxMessageBytes = 64 * 1024;

    private readonly bool _propagateTraceContext;
    private readonly int? _compressionThreshold;
    private readonly bool _sendMessageType;
    private readonly int? _largeMessageThreshold;

    public MessageCodec(
        IQueueMessageSerializer serializer,
        bool propagateTraceContext = false,
        int? compressionThreshold = null,
        bool sendMessageType = false,
        ILargeMessageStore? largeMessageStore = null,
        int? largeMessageThreshold = null)
    {
        Serializer = serializer;
        _propagateTraceContext = propagateTraceContext;
        _compressionThreshold = compressionThreshold;
        _sendMessageType = sendMessageType;
        LargeMessageStore = largeMessageStore;
        _largeMessageThreshold = largeMessageThreshold;
    }

    public IQueueMessageSerializer Serializer { get; }

    public ILargeMessageStore? LargeMessageStore { get; }

    /// <summary>How big <paramref name="bytes"/> bytes are against the limit once the SDK has encoded them.</summary>
    public static int WireSize(int bytes, bool base64Encoded) => base64Encoded ? (int)Math.Min(int.MaxValue, (bytes + 2L) / 3 * 4) : bytes;

    // ------------------------------------------------------------------------------------------------------------
    // Sending
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Serializes <paramref name="message"/> and wraps it in an envelope when anything has to travel with it: the trace
    /// context of <paramref name="creation"/> (when propagation is on), headers, a compressed body, or a reference to a
    /// body kept in the large message store. Throws <see cref="QueueMessageTooLargeException"/> rather than send what the
    /// service would reject.
    /// </summary>
    public async ValueTask<EncodedMessage> EncodeAsync<T>(string queueName, bool base64Encoded, T message, SendOptions? options, Activity? creation, CancellationToken cancellationToken)
    {
        BinaryData payload = Serializer.Serialize(message);
        var serializedSize = payload.ToMemory().Length;

        var envelope = new MessageEnvelope();
        var needsEnvelope = false;

        if (_propagateTraceContext && creation is not null) needsEnvelope |= envelope.SetTraceContext(creation);

        if (Headers<T>(options) is { } headers)
        {
            envelope.Headers = headers;
            needsEnvelope = true;
        }

        var body = payload;
        // The library's own serializer always writes exactly one JSON value, so there is no need to parse it to find out.
        var knownJson = Serializer is JsonQueueMessageSerializer;
        if (_compressionThreshold is { } threshold && serializedSize >= threshold && Gzip(payload) is { } compressed)
        {
            body = compressed;
            envelope.Encoding = MessageEnvelope.GzipEncoding;
            needsEnvelope = true;
        }

        envelope.SetBody(body, knownJson && envelope.Encoding is null);

        string? blob = null;
        if (LargeMessageStore is { } store && _largeMessageThreshold is { } large && body.ToMemory().Length >= large)
        {
            blob = await store.UploadAsync(queueName, body, cancellationToken);
            envelope.BlobReference = blob;
            needsEnvelope = true;
        }

        var wire = needsEnvelope ? envelope.Write() : payload;
        var size = WireSize(wire.ToMemory().Length, base64Encoded);
        if (size <= MaxMessageBytes) return new EncodedMessage(wire, serializedSize, blob);

        if (LargeMessageStore is { } fallback && blob is null)
        {
            blob = await fallback.UploadAsync(queueName, body, cancellationToken);
            envelope.BlobReference = blob;
            wire = envelope.Write();
            size = WireSize(wire.ToMemory().Length, base64Encoded);
            if (size <= MaxMessageBytes) return new EncodedMessage(wire, serializedSize, blob);
        }

        var detail = blob is not null
            ? "Even with the body in the large message store, the headers and trace context are too big."
            : $"The serializer wrote {serializedSize:N0} bytes{(needsEnvelope ? ", and the library's envelope adds to that" : "")}{(base64Encoded ? "; Base64 encoding makes it a third bigger" : "")}. Send less, turn on compression with UseCompression(), or keep large bodies in Blob storage with UseBlobStorageForLargeMessages() from the AzureStorage.QueueService.Blobs package.";
        throw new QueueMessageTooLargeException(queueName, size, MaxMessageBytes, detail);
    }

    private IReadOnlyDictionary<string, string>? Headers<T>(SendOptions? options)
    {
        var messageType = options?.MessageType ?? (_sendMessageType ? typeof(T).Name : null);
        if (options?.Headers is not { Count: > 0 } given && messageType is null) return null;

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (options?.Headers is { } source)
        {
            foreach (var (name, value) in source)
            {
                if (string.IsNullOrEmpty(name)) throw new ArgumentException("A header name cannot be empty.", nameof(options));
                headers[name] = value ?? throw new ArgumentException($"Header '{name}' has no value.", nameof(options));
            }
        }

        if (messageType is not null) headers[QueueMessageHeaders.MessageType] = messageType;
        return headers;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Receiving
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A received message, not yet deserialized, with the envelope (if any) taken apart. Never throws: one bad message
    /// must not fail the batch it arrived in, so what cannot be read comes back with <see cref="QueueMessage{T}.Error"/>
    /// set. Unwrapped whatever this client's own settings, which govern sending only: a receiver reads what any sender
    /// put on the queue.
    /// </summary>
    public static QueueMessage<T> Received<T>(TransportMessage received, IQueueMessageOperations operations)
    {
        var info = received.Info;
        var wire = received.Body;

        if (received.Undecodable)
            return new QueueMessage<T>(info, wire, default, new DeserializationException($"Message {info.MessageId} is not valid Base64, so the SDK could not decode it."), operations);

        switch (MessageEnvelope.TryRead(wire, out var envelope, out var problem))
        {
            case EnvelopeKind.Plain:
                return new QueueMessage<T>(info, wire, default, null, operations);

            case EnvelopeKind.Unreadable:
                return new QueueMessage<T>(info, wire, default, new DeserializationException($"Message {info.MessageId} is wrapped in the library's envelope, but {problem}."), operations);
        }

        var message = new QueueMessage<T>(info, envelope!.Body ?? BinaryData.Empty, default, null, operations);
        var lease = message.Lease;
        lease.WireBody = wire;
        lease.Envelope = envelope;
        lease.CreationContext = envelope.CreationContext;
        lease.Headers = envelope.Headers ?? info.Headers;

        if (envelope.BlobReference is not null)
        {
            // Read inside the delivery, after lease renewal has started: a download can be slow.
            lease.PendingBlob = envelope.BlobReference;
            return message;
        }

        if (envelope.Encoding is not null)
        {
            var (body, error) = Decode(envelope.Body!, envelope.Encoding, info.MessageId);
            if (error is not null) message.SetBody(default, error);
            else lease.RawBody = body!;
        }

        return message;
    }

    /// <summary>
    /// Brings in a body kept in the large message store, if the message has one. Returns the problem, or null; never
    /// throws except for cancellation. A message whose body is gone or cannot be read is an unreadable message, to be
    /// dead-lettered; its stored body is left alone.
    /// </summary>
    public async ValueTask<DeserializationException?> ResolvePayloadAsync(MessageLease lease, Action<string, Exception>? onDeleteFailed, CancellationToken cancellationToken)
    {
        if (lease.PendingBlob is not { } reference) return null;

        if (LargeMessageStore is not { } store)
            return new DeserializationException($"Message {lease.MessageId}'s body is kept outside the queue ({reference}), and this client has no large message store to read it from. Attach the same store the sender uses, for example with UseBlobStorageForLargeMessages().");

        BinaryData stored;
        try
        {
            stored = await store.DownloadAsync(reference, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return new DeserializationException($"Message {lease.MessageId}'s body could not be read from the large message store ({reference}): {e.Message}", e);
        }

        var (body, error) = lease.Envelope?.Encoding is { } encoding ? Decode(stored, encoding, lease.MessageId) : (stored, null);
        if (error is not null) return error;

        lease.RawBody = body!;
        lease.PendingBlob = null;
        // Deleted once the message is gone from the queue for good; kept through retries and dead-lettering.
        lease.AfterComplete = async () =>
        {
            try
            {
                await store.DeleteAsync(reference, CancellationToken.None);
            }
            catch (Exception e)
            {
                onDeleteFailed?.Invoke(reference, e);
            }
        };
        return null;
    }

    /// <summary>Fills in <see cref="QueueMessage{T}.Body"/> or <see cref="QueueMessage{T}.Error"/>. Never throws.</summary>
    public void Deserialize<T>(QueueMessage<T> message)
    {
        if (message.Error is not null) return; // a decoding failure; there is nothing to deserialize
        var (body, error) = TryDeserialize<T>(message.RawBody);
        message.SetBody(body, error);
    }

    public (T? Body, DeserializationException? Error) TryDeserialize<T>(BinaryData raw) => TryDeserialize<T>(Serializer, raw);

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

    // ------------------------------------------------------------------------------------------------------------
    // Compression
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>The body gzipped, or null when that would not make the message smaller.</summary>
    private static BinaryData? Gzip(BinaryData payload)
    {
        var source = payload.ToMemory();
        using var output = new MemoryStream(source.Length / 2 + 64);
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(source.Span);

        // Compressed bytes travel Base64-encoded (body64), a third bigger, so compression has to beat that to be worth it.
        return output.Length * 4 / 3 < source.Length ? new BinaryData(output.GetBuffer().AsMemory(0, (int)output.Length)) : null;
    }

    private static (BinaryData? Body, DeserializationException? Error) Decode(BinaryData encoded, string encoding, string messageId)
    {
        try
        {
            using var input = encoded.ToStream();
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return (new BinaryData(output.GetBuffer().AsMemory(0, (int)output.Length)), null);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return (null, new DeserializationException($"Message {messageId}'s body is marked '{encoding}' but could not be decompressed: {e.Message}", e));
        }
    }
}
