using Azure.Storage.Queues.Models;

namespace AzureStorage.QueueService;

/// <summary>
/// Represents a typed queue client for a specific message type.
/// This interface provides strongly-typed operations for queue interactions.
/// </summary>
/// <typeparam name="TMessage">The type of message this client handles</typeparam>
public interface ITypedQueueClient<TMessage> where TMessage : class
{
    /// <summary>
    /// Creates the queue if it doesn't exist.
    /// </summary>
    /// <param name="metadata">Optional metadata for the queue</param>
    /// <param name="cancellationToken">Cancellation token</param>
    ValueTask CreateQueueIfNotExistsAsync(IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all messages from the queue.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    ValueTask ClearMessagesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Peeks at messages without removing them from the queue.
    /// </summary>
    /// <param name="numMessages">Number of messages to peek at</param>
    /// <param name="cancellationToken">Cancellation token</param>
    ValueTask<IEnumerable<TMessage>> PeekMessagesAsync(int numMessages, CancellationToken cancellationToken = default);

    /// <summary>
    /// Peeks at messages without removing them from the queue (synchronous).
    /// </summary>
    /// <param name="numMessages">Number of messages to peek at</param>
    /// <param name="cancellationToken">Cancellation token</param>
    IEnumerable<TMessage> PeekMessages(int numMessages, CancellationToken cancellationToken = default);

    /// <summary>
    /// Receives and processes messages from the queue.
    /// </summary>
    /// <param name="handleMessage">Delegate to handle each received message</param>
    /// <param name="handleException">Delegate to handle exceptions during processing</param>
    /// <param name="numMessages">Number of messages to receive</param>
    /// <param name="cancellationToken">Cancellation token</param>
    ValueTask ReceiveMessagesAsync(
        Func<TMessage?, IDictionary<string, string>?, ValueTask> handleMessage, 
        Func<Exception, IDictionary<string, string>?, ValueTask> handleException, 
        int numMessages = 1, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message to the queue.
    /// </summary>
    /// <param name="message">The message to send</param>
    /// <param name="visibilityTimeout">Optional visibility timeout</param>
    /// <param name="timeToLive">Optional time to live</param>
    /// <param name="cancellationToken">Cancellation token</param>
    ValueTask<SendReceipt> SendMessageAsync(
        TMessage message, 
        TimeSpan? visibilityTimeout = null, 
        TimeSpan? timeToLive = null, 
        CancellationToken cancellationToken = default);
}