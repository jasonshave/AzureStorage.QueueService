namespace AzureStorage.QueueService;

/// <summary>
/// Keeps message bodies that are too big for a queue message somewhere else, leaving a reference on the queue (the
/// claim-check pattern). The <c>AzureStorage.QueueService.Blobs</c> package implements it over Blob storage; attach one
/// to a client with <c>UseLargeMessageStore</c> or that package's <c>UseBlobStorageForLargeMessages</c>.
/// </summary>
/// <remarks>
/// A client with a store sends a body to it when the message would otherwise be over the queue's 64 KiB limit (or over
/// <see cref="QueueClientSettings.LargeMessageThreshold"/>), and reads it back, inside the delivery and after lease
/// renewal has started, when a message refers to one. The body is deleted once the message is completed, and kept when
/// it is dead-lettered so a replay still has it. Every client that reads the queue needs the same store.
/// </remarks>
public interface ILargeMessageStore
{
    /// <summary>Stores <paramref name="body"/> for a message going to <paramref name="queueName"/> and returns the reference to put on the queue.</summary>
    ValueTask<string> UploadAsync(string queueName, BinaryData body, CancellationToken cancellationToken);

    /// <summary>Reads back a body stored by <see cref="UploadAsync"/>.</summary>
    ValueTask<BinaryData> DownloadAsync(string reference, CancellationToken cancellationToken);

    /// <summary>Deletes a stored body. Deleting one that is already gone is not an error.</summary>
    ValueTask DeleteAsync(string reference, CancellationToken cancellationToken);
}

/// <summary>
/// Thrown before sending a message the service would reject for its size: over 64 KiB as it would go on the queue,
/// after any envelope and Base64 encoding. Attach an <see cref="ILargeMessageStore"/> to send bodies that big.
/// </summary>
public sealed class QueueMessageTooLargeException : InvalidOperationException
{
    /// <summary>Creates the exception for a message of <paramref name="size"/> bytes against a limit of <paramref name="limit"/>.</summary>
    public QueueMessageTooLargeException(string queueName, int size, int limit, string detail)
        : base($"Queue '{queueName}': the message is {size:N0} bytes as it would go on the queue, over the service's limit of {limit:N0} bytes. {detail}")
    {
        QueueName = queueName;
        Size = size;
        Limit = limit;
    }

    /// <summary>The queue the message was for.</summary>
    public string QueueName { get; }

    /// <summary>The message's size as it would have gone on the queue, in bytes.</summary>
    public int Size { get; }

    /// <summary>The service's limit, in bytes.</summary>
    public int Limit { get; }
}
