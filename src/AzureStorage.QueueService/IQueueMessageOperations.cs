namespace AzureStorage.QueueService;

/// <summary>
/// The queue operations a <see cref="QueueMessage{T}"/> needs. The library implements this over the SDK client;
/// a test that stubs <see cref="IQueueReceiver"/> supplies its own.
/// </summary>
public interface IQueueMessageOperations
{
    ValueTask DeleteAsync(string messageId, string popReceipt, CancellationToken cancellationToken);

    /// <summary>
    /// Sets a new visibility timeout, measured from now. Zero makes the message visible immediately.
    /// </summary>
    ValueTask<VisibilityReceipt> UpdateVisibilityAsync(string messageId, string popReceipt, TimeSpan visibilityTimeout, CancellationToken cancellationToken);

    /// <summary>
    /// Copies the raw body to the poison queue, then deletes the original.
    /// </summary>
    ValueTask DeadLetterAsync(string messageId, string popReceipt, long dequeueCount, BinaryData rawBody, string? reason, CancellationToken cancellationToken);
}

/// <summary>
/// What the service hands back after a visibility change: the new pop receipt and when the message becomes visible.
/// </summary>
public sealed record VisibilityReceipt(string PopReceipt, DateTimeOffset NextVisibleOn);
