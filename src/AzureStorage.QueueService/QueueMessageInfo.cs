namespace AzureStorage.QueueService;

/// <summary>
/// The service-side identity of a received message.
/// </summary>
/// <param name="MessageId">The service's id for the message.</param>
/// <param name="PopReceipt">The receipt that proves the receiver holds the message.</param>
/// <param name="DequeueCount">How many times the message has been received, including this time.</param>
/// <param name="InsertedOn">When the message was first put on the queue.</param>
/// <param name="ExpiresOn">When the service discards the message.</param>
/// <param name="NextVisibleOn">When the message becomes visible again unless it is settled or renewed.</param>
public sealed record QueueMessageInfo(
    string MessageId,
    string PopReceipt,
    long DequeueCount,
    DateTimeOffset? InsertedOn,
    DateTimeOffset? ExpiresOn,
    DateTimeOffset? NextVisibleOn)
{
    /// <summary>The queue the message came from.</summary>
    public string? QueueName { get; init; }

    /// <summary>Headers the sender attached.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Whether this is the message's last delivery before a processor dead-letters it; see <see cref="IQueueMessage.IsLastAttempt"/>.</summary>
    public bool IsLastAttempt { get; init; }
}
