using System.Diagnostics;

namespace AzureStorage.QueueService;

/// <summary>
/// How a received message was settled.
/// </summary>
public enum MessageSettlement
{
    /// <summary>Still held; it becomes visible again when its lease lapses.</summary>
    None,

    /// <summary>Deleted from the queue.</summary>
    Completed,

    /// <summary>Given back to the queue to be delivered again.</summary>
    Abandoned,

    /// <summary>Moved to the poison queue.</summary>
    DeadLettered,
}

/// <summary>
/// A received message without its body's type: what middleware and other code that serves every message type sees.
/// Every <see cref="QueueMessage{T}"/> is one.
/// </summary>
public interface IQueueMessage
{
    /// <summary>The service's id for the message.</summary>
    string MessageId { get; }

    /// <summary>The queue the message was received from. Empty for a message built by hand without one.</summary>
    string QueueName { get; }

    /// <summary>The receipt that proves this receiver holds the message; changes after a renewal or an abandon.</summary>
    string PopReceipt { get; }

    /// <summary>How many times the message has been received, including this time.</summary>
    long DequeueCount { get; }

    /// <summary>When the message was first put on the queue.</summary>
    DateTimeOffset? InsertedOn { get; }

    /// <summary>When the service discards the message if nobody completes it.</summary>
    DateTimeOffset? ExpiresOn { get; }

    /// <summary>When the message becomes visible to other consumers unless it is settled or renewed first.</summary>
    DateTimeOffset? NextVisibleOn { get; }

    /// <summary>The serialized message, before deserialization. See <see cref="QueueMessage{T}.RawBody"/>.</summary>
    BinaryData RawBody { get; }

    /// <summary>The deserialized body, or null; see <see cref="QueueMessage{T}.Body"/>.</summary>
    object? Body { get; }

    /// <summary>Set when the body could not be read. The message is still returned so it can be dead-lettered.</summary>
    DeserializationException? Error { get; }

    /// <summary>Headers the sender attached with <see cref="SendOptions.Headers"/>; empty when there were none.</summary>
    IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>
    /// True when a processor will not retry this message if the handler fails: this is its last delivery before
    /// <see cref="QueueProcessorOptionsBase.MaxDequeueCount"/> moves it to the poison queue. Always false for a message
    /// received with <see cref="IQueueReceiver.ReceiveAsync{T}"/>, which has no retry limit of its own.
    /// </summary>
    bool IsLastAttempt { get; }

    /// <summary>How the message was settled, if it has been.</summary>
    MessageSettlement Settlement { get; }

    /// <summary>True once the message has been completed, abandoned or dead-lettered.</summary>
    bool IsSettled { get; }

    /// <summary>Deletes the message from the queue.</summary>
    ValueTask CompleteAsync(CancellationToken cancellationToken = default);

    /// <summary>Gives the message back to the queue: visible again immediately, or after <paramref name="visibilityTimeout"/> (up to 7 days).</summary>
    ValueTask AbandonAsync(TimeSpan? visibilityTimeout = null, CancellationToken cancellationToken = default);

    /// <summary>Keeps the message hidden for another <paramref name="visibilityTimeout"/> (more than zero, up to 7 days), measured from now.</summary>
    ValueTask RenewVisibilityAsync(TimeSpan visibilityTimeout, CancellationToken cancellationToken = default);

    /// <summary>Moves the message to the poison queue, recording <paramref name="reason"/> with it.</summary>
    ValueTask DeadLetterAsync(string? reason = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// A message received from a queue, with its identity and the operations that settle it. The message stays
/// hidden from other consumers until its visibility timeout passes or it is completed, abandoned or dead-lettered.
/// </summary>
/// <remarks>
/// Delivery is at-least-once: a lease can lapse before completion, and a completion can fail after the work is
/// done. Operations are serialized, so a renewal and a completion cannot race on the pop receipt, and every
/// operation uses the latest receipt. Once settled, further operations throw <see cref="InvalidOperationException"/>.
/// </remarks>
/// <typeparam name="T">The type the body is read as.</typeparam>
public sealed class QueueMessage<T> : IQueueMessage
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    /// <summary>
    /// Public so a test that stubs <see cref="IQueueReceiver"/> can build messages; the library passes its real operations.
    /// <see cref="QueueMessageInfo"/>'s optional properties set <see cref="QueueName"/>, <see cref="Headers"/> and
    /// <see cref="IsLastAttempt"/>.
    /// </summary>
    public QueueMessage(QueueMessageInfo info, BinaryData rawBody, T? body, DeserializationException? error, IQueueMessageOperations operations)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(rawBody);
        ArgumentNullException.ThrowIfNull(operations);

        Lease = new MessageLease(info, rawBody, operations);
        Body = body;
        Error = error;
    }

    // A second typed view of the same received message (a routed or wrapped handler's own type), sharing its receipt
    // and settlement, so whichever view settles it, the processor sees it settled.
    private QueueMessage(MessageLease lease, T? body, DeserializationException? error)
    {
        Lease = lease;
        Body = body;
        Error = error;
    }

    internal MessageLease Lease { get; }

    /// <inheritdoc/>
    public string MessageId => Lease.MessageId;

    /// <inheritdoc/>
    public string QueueName => Lease.QueueName;

    /// <summary>Changes after <see cref="RenewVisibilityAsync"/> and <see cref="AbandonAsync"/>.</summary>
    public string PopReceipt => Lease.PopReceipt;

    /// <summary>How many times the message has been received, including this time.</summary>
    public long DequeueCount => Lease.DequeueCount;

    /// <inheritdoc/>
    public DateTimeOffset? InsertedOn => Lease.InsertedOn;

    /// <inheritdoc/>
    public DateTimeOffset? ExpiresOn => Lease.ExpiresOn;

    /// <inheritdoc/>
    public DateTimeOffset? NextVisibleOn => Lease.NextVisibleOn;

    /// <summary>
    /// The serialized message, before deserialization: what the sender's serializer produced. When the sender wrapped
    /// it in the library's envelope (for trace context, headers, compression or a body kept in Blob storage), this is the
    /// message inside the envelope, decompressed and downloaded, so it is the same whichever way the message was sent.
    /// </summary>
    public BinaryData RawBody => Lease.RawBody;

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, string> Headers => Lease.Headers ?? NoHeaders;

    /// <inheritdoc/>
    public bool IsLastAttempt => Lease.IsLastAttempt;

    /// <summary>Null when deserialization failed (see <see cref="Error"/>) or when the body was JSON <c>null</c>.</summary>
    public T? Body { get; private set; }

    object? IQueueMessage.Body => Body;

    /// <summary>Set when the body could not be decoded or deserialized. The message is still returned so it can be dead-lettered.</summary>
    public DeserializationException? Error { get; private set; }

    /// <summary>How the message was settled, if it has been.</summary>
    public MessageSettlement Settlement => Lease.Settlement;

    /// <summary>True once the message has been completed, abandoned or dead-lettered.</summary>
    public bool IsSettled => Lease.Settlement != MessageSettlement.None;

    /// <summary>
    /// The body exactly as it arrived on the queue: the envelope when there was one, otherwise <see cref="RawBody"/>.
    /// Dead-lettering starts from this, so a message moved back from the poison queue replays with its trace context.
    /// </summary>
    internal BinaryData WireBody => Lease.WireBody;

    /// <summary>The sender's trace context, when it was carried in an envelope and parsed.</summary>
    internal ActivityContext? CreationContext => Lease.CreationContext;

    /// <summary>
    /// What a processor's read step took from the body for a wrapper handler: the CloudEvent adapter's typed data, or
    /// the route a routed processor chose.
    /// </summary>
    internal object? HandlerData { get; set; }

    // The processor receives raw and deserializes only after lease renewal is armed, so a slow serializer cannot
    // let the lease lapse before the message is protected.
    internal void SetBody(T? body, DeserializationException? error)
    {
        Body = body;
        Error = error;
    }

    /// <summary>A view of this message with a body of another type, sharing its receipt and settlement.</summary>
    internal QueueMessage<TOther> As<TOther>(TOther? body, DeserializationException? error) => new(Lease, body, error);

    /// <summary>Deletes the message from the queue.</summary>
    public ValueTask CompleteAsync(CancellationToken cancellationToken = default) => Lease.CompleteAsync(cancellationToken);

    /// <summary>
    /// Gives the message back to the queue: visible again immediately, or after <paramref name="visibilityTimeout"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="visibilityTimeout"/> is negative or longer than 7 days.</exception>
    public ValueTask AbandonAsync(TimeSpan? visibilityTimeout = null, CancellationToken cancellationToken = default) =>
        Lease.AbandonAsync(visibilityTimeout ?? TimeSpan.Zero, cancellationToken);

    /// <summary>Keeps the message hidden for another <paramref name="visibilityTimeout"/>, measured from now.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="visibilityTimeout"/> is not more than zero, or is longer than 7 days.</exception>
    public ValueTask RenewVisibilityAsync(TimeSpan visibilityTimeout, CancellationToken cancellationToken = default) =>
        Lease.RenewAsync(visibilityTimeout, cancellationToken);

    /// <summary>
    /// Copies the message to the poison queue and deletes it from this one. The copy keeps the body as it arrived and
    /// records <paramref name="reason"/>, the original message id and the dequeue count, which
    /// <see cref="IDeadLetterQueue"/> reads back.
    /// </summary>
    public ValueTask DeadLetterAsync(string? reason = null, CancellationToken cancellationToken = default) =>
        Lease.DeadLetterAsync(reason, DeadLetterCategories.Handler, cancellationToken);

    internal ValueTask DeadLetterAsync(string reason, string category, CancellationToken cancellationToken) =>
        Lease.DeadLetterAsync(reason, category, cancellationToken);
}

/// <summary>The bounded set of reasons the library records for a dead-lettered message, for metrics and replay filters.</summary>
public static class DeadLetterCategories
{
    /// <summary>Delivered more than <see cref="QueueProcessorOptionsBase.MaxDequeueCount"/> times.</summary>
    public const string MaxDequeueCount = "max_dequeue";

    /// <summary>The body could not be decoded, unwrapped, downloaded or deserialized.</summary>
    public const string Deserialization = "deserialization";

    /// <summary>The handler dead-lettered it, or threw an exception the processor's error policy dead-letters.</summary>
    public const string Handler = "handler";

    /// <summary>A routed processor had no handler for the message's type.</summary>
    public const string Unroutable = "unroutable";
}

/// <summary>
/// One received message's identity, receipt and settlement, shared by every typed view of it. All settlement goes
/// through here, one operation at a time.
/// </summary>
internal sealed class MessageLease
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public MessageLease(QueueMessageInfo info, BinaryData rawBody, IQueueMessageOperations operations)
    {
        MessageId = info.MessageId;
        PopReceipt = info.PopReceipt;
        DequeueCount = info.DequeueCount;
        InsertedOn = info.InsertedOn;
        ExpiresOn = info.ExpiresOn;
        NextVisibleOn = info.NextVisibleOn;
        QueueName = info.QueueName ?? string.Empty;
        Headers = info.Headers;
        IsLastAttempt = info.IsLastAttempt;
        RawBody = rawBody;
        Operations = operations;
    }

    public IQueueMessageOperations Operations { get; }

    public string MessageId { get; }

    public string QueueName { get; }

    public string PopReceipt { get; private set; }

    public long DequeueCount { get; }

    public DateTimeOffset? InsertedOn { get; }

    public DateTimeOffset? ExpiresOn { get; }

    public DateTimeOffset? NextVisibleOn { get; private set; }

    public MessageSettlement Settlement { get; private set; }

    public IReadOnlyDictionary<string, string>? Headers { get; set; }

    public bool IsLastAttempt { get; set; }

    /// <summary>The payload: set again once a body kept in Blob storage has been downloaded.</summary>
    public BinaryData RawBody { get; set; }

    /// <summary>The body exactly as it arrived on the queue; <see cref="RawBody"/> unless there was an envelope.</summary>
    public BinaryData WireBody
    {
        get => _wireBody ?? RawBody;
        set => _wireBody = value;
    }

    private BinaryData? _wireBody;

    /// <summary>The envelope the message arrived in, if it was one this version reads.</summary>
    public MessageEnvelope? Envelope { get; set; }

    public ActivityContext? CreationContext { get; set; }

    /// <summary>Where the body is when it was kept in Blob storage and has not been downloaded yet.</summary>
    public string? PendingBlob { get; set; }

    /// <summary>The span of the delivery that holds this message, for settlement events. Set by the processor.</summary>
    public Activity? DeliveryActivity { get; set; }

    /// <summary>Runs after a successful completion, best effort: deletes a body kept in Blob storage.</summary>
    public Func<ValueTask>? AfterComplete { get; set; }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfSettled();
            await Operations.DeleteAsync(MessageId, PopReceipt, cancellationToken);
            Settlement = MessageSettlement.Completed;
            DeliveryActivity?.AddEvent(new ActivityEvent("queue.message_completed"));
        }
        finally
        {
            _gate.Release();
        }

        if (AfterComplete is { } after) await after();
    }

    public async ValueTask AbandonAsync(TimeSpan visibilityTimeout, CancellationToken cancellationToken)
    {
        if (visibilityTimeout < TimeSpan.Zero || visibilityTimeout > QueueClientSettings.MaxVisibility)
            throw new ArgumentOutOfRangeException(nameof(visibilityTimeout), visibilityTimeout, "The service accepts 0 seconds to 7 days.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfSettled();
            var receipt = await Operations.UpdateVisibilityAsync(MessageId, PopReceipt, visibilityTimeout, cancellationToken);
            PopReceipt = receipt.PopReceipt;
            NextVisibleOn = receipt.NextVisibleOn;
            Settlement = MessageSettlement.Abandoned;
            DeliveryActivity?.AddEvent(new ActivityEvent("queue.message_abandoned"));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask RenewAsync(TimeSpan visibilityTimeout, CancellationToken cancellationToken)
    {
        if (visibilityTimeout <= TimeSpan.Zero || visibilityTimeout > QueueClientSettings.MaxVisibility)
            throw new ArgumentOutOfRangeException(nameof(visibilityTimeout), visibilityTimeout, "A renewal must be longer than zero and no longer than 7 days.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfSettled();
            var receipt = await Operations.UpdateVisibilityAsync(MessageId, PopReceipt, visibilityTimeout, cancellationToken);
            PopReceipt = receipt.PopReceipt;
            NextVisibleOn = receipt.NextVisibleOn;
            DeliveryActivity?.AddEvent(new ActivityEvent("queue.message_renewed"));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DeadLetterAsync(string? reason, string category, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfSettled();
            if (Operations is IDeadLetterOperations library)
                await library.DeadLetterAsync(this, reason, category, cancellationToken);
            else
                await Operations.DeadLetterAsync(MessageId, PopReceipt, DequeueCount, WireBody, reason, cancellationToken);
            Settlement = MessageSettlement.DeadLettered;
            DeliveryActivity?.AddEvent(new ActivityEvent("queue.message_dead_lettered", tags: new ActivityTagsCollection { ["queue.dead_letter_reason"] = category }));
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ThrowIfSettled()
    {
        if (Settlement != MessageSettlement.None) throw new InvalidOperationException($"Message {MessageId} has already been settled ({Settlement}).");
    }
}

/// <summary>
/// The library's own dead-letter path: it gets the whole lease, so the copy on the poison queue can record why and
/// from where, which the public <see cref="IQueueMessageOperations.DeadLetterAsync"/> has no parameters for.
/// </summary>
internal interface IDeadLetterOperations
{
    ValueTask DeadLetterAsync(MessageLease message, string? reason, string category, CancellationToken cancellationToken);
}
