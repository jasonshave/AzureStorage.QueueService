using Azure.Storage.Queues.Models;

namespace AzureStorage.QueueService;

/// <summary>
/// Sends messages to one queue. Depend on this when a class only sends; it is the smallest thing to fake.
/// </summary>
public interface IQueueSender : INamedQueue
{
    /// <exception cref="Azure.RequestFailedException">The service rejected the send.</exception>
    ValueTask<SendReceipt> SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default);
}

/// <param name="VisibilityTimeout">How long the message stays hidden after it is sent; by default it is visible immediately.</param>
/// <param name="TimeToLive">How long the message lives before the service discards it; the service default is 7 days. Use <see cref="Timeout.InfiniteTimeSpan"/> to never expire.</param>
public sealed record SendOptions(TimeSpan? VisibilityTimeout = null, TimeSpan? TimeToLive = null);
