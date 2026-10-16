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
/// A message received from a queue, with its identity and the operations that settle it. The message stays
/// hidden from other consumers until its visibility timeout passes or it is completed, abandoned or dead-lettered.
/// </summary>
/// <remarks>
/// Delivery is at-least-once: a lease can lapse before completion, and a completion can fail after the work is
/// done. Operations are serialized, so a renewal and a completion cannot race on the pop receipt, and every
/// operation uses the latest receipt. Once settled, further operations throw <see cref="InvalidOperationException"/>.
/// </remarks>
public sealed class QueueMessage<T>
{
    private readonly IQueueMessageOperations _operations;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Public so a test that stubs <see cref="IQueueReceiver"/> can build messages; the library passes its real operations.
    /// </summary>
    public QueueMessage(QueueMessageInfo info, BinaryData rawBody, T? body, DeserializationException? error, IQueueMessageOperations operations)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(rawBody);
        ArgumentNullException.ThrowIfNull(operations);

        MessageId = info.MessageId;
        PopReceipt = info.PopReceipt;
        DequeueCount = info.DequeueCount;
        InsertedOn = info.InsertedOn;
        ExpiresOn = info.ExpiresOn;
        NextVisibleOn = info.NextVisibleOn;
        RawBody = rawBody;
        Body = body;
        Error = error;
        _operations = operations;
    }

    public string MessageId { get; }

    /// <summary>Changes after <see cref="RenewLockAsync"/> and <see cref="AbandonAsync"/>.</summary>
    public string PopReceipt { get; private set; }

    /// <summary>How many times the message has been received, including this time.</summary>
    public long DequeueCount { get; }

    public DateTimeOffset? InsertedOn { get; }

    public DateTimeOffset? ExpiresOn { get; }

    public DateTimeOffset? NextVisibleOn { get; private set; }

    public BinaryData RawBody { get; }

    /// <summary>Null when deserialization failed (see <see cref="Error"/>) or when the body was JSON <c>null</c>.</summary>
    public T? Body { get; private set; }

    /// <summary>Set when the body could not be decoded or deserialized. The message is still returned so it can be dead-lettered.</summary>
    public DeserializationException? Error { get; private set; }

    // The processor receives raw and deserializes only after lease renewal is armed, so a slow serializer cannot
    // let the lease lapse before the message is protected.
    internal void SetBody(T? body, DeserializationException? error)
    {
        Body = body;
        Error = error;
    }

    /// <summary>How the message was settled, if it has been.</summary>
    public MessageSettlement Settlement { get; private set; }

    /// <summary>True once the message has been completed, abandoned or dead-lettered.</summary>
    public bool IsSettled => Settlement != MessageSettlement.None;

    /// <summary>Deletes the message from the queue.</summary>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfSettled();
            await _operations.DeleteAsync(MessageId, PopReceipt, cancellationToken);
            Settlement = MessageSettlement.Completed;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Gives the message back to the queue: visible again immediately, or after <paramref name="visibilityTimeout"/>.
    /// </summary>
    public async ValueTask AbandonAsync(TimeSpan? visibilityTimeout = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfSettled();
            var receipt = await _operations.UpdateVisibilityAsync(MessageId, PopReceipt, visibilityTimeout ?? TimeSpan.Zero, cancellationToken);
            PopReceipt = receipt.PopReceipt;
            NextVisibleOn = receipt.NextVisibleOn;
            Settlement = MessageSettlement.Abandoned;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Keeps the message hidden for another <paramref name="visibilityTimeout"/>, measured from now.</summary>
    public async ValueTask RenewLockAsync(TimeSpan visibilityTimeout, CancellationToken cancellationToken = default)
    {
        if (visibilityTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(visibilityTimeout), "A renewal must be longer than zero.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfSettled();
            var receipt = await _operations.UpdateVisibilityAsync(MessageId, PopReceipt, visibilityTimeout, cancellationToken);
            PopReceipt = receipt.PopReceipt;
            NextVisibleOn = receipt.NextVisibleOn;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Copies the raw body to the poison queue and deletes the message from this one.</summary>
    public async ValueTask DeadLetterAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfSettled();
            await _operations.DeadLetterAsync(MessageId, PopReceipt, DequeueCount, RawBody, reason, cancellationToken);
            Settlement = MessageSettlement.DeadLettered;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ThrowIfSettled()
    {
        if (IsSettled) throw new InvalidOperationException($"Message {MessageId} has already been settled ({Settlement}).");
    }
}
