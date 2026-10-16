using Azure.Storage.Queues.Models;

namespace AzureStorage.QueueService;

/// <summary>
/// Sends messages to one queue. Depend on this when a class only sends; it is the smallest thing to fake.
/// </summary>
public interface IQueueSender : INamedQueue
{
    /// <summary>Serializes <paramref name="message"/> and puts it on the queue.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> asks for a delay or time to live the service would reject.</exception>
    /// <exception cref="QueueMessageTooLargeException">The message would be over the service's 64 KiB limit and the client has no large message store.</exception>
    /// <exception cref="Azure.RequestFailedException">The service rejected the send.</exception>
    ValueTask<SendReceipt> SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default);
}

/// <summary>How one message is sent.</summary>
/// <param name="VisibilityTimeout">
/// How long the message stays hidden after it is sent (0 to 7 days, and less than <paramref name="TimeToLive"/>); by
/// default it is visible immediately. <see cref="Delay"/> is another name for the same thing.
/// </param>
/// <param name="TimeToLive">How long the message lives before the service discards it (at least 1 second); the service default is 7 days. Use <see cref="Timeout.InfiniteTimeSpan"/> to never expire.</param>
public sealed record SendOptions(TimeSpan? VisibilityTimeout = null, TimeSpan? TimeToLive = null)
{
    /// <summary>How long to wait before the message can be received: <see cref="VisibilityTimeout"/> under the name scheduling code expects.</summary>
    public TimeSpan? Delay
    {
        get => VisibilityTimeout;
        init => VisibilityTimeout = value;
    }

    /// <summary>
    /// Name and value pairs that travel with the message, read back as <see cref="QueueMessage{T}.Headers"/>: a correlation
    /// id, a tenant, a content type. Headers put the message in the library's envelope, which only receivers on 3.1 or
    /// later read, so see the README before sending them to a queue older code also reads.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// The message's type, sent as the <see cref="QueueMessageHeaders.MessageType"/> header, which a routed processor
    /// uses to choose the handler. Overrides <see cref="QueueClientSettings.SendMessageType"/> for this message.
    /// </summary>
    public string? MessageType { get; init; }
}
