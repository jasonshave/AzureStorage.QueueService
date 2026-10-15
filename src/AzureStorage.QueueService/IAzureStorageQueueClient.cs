using Azure.Storage.Queues.Models;

namespace AzureStorage.QueueService;

/// <summary>
/// A client bound to one Azure Storage queue. <see cref="AzureStorageQueueClient"/> is the only implementation;
/// depend on this interface when you want to substitute a fake in your own unit tests.
/// </summary>
public interface IAzureStorageQueueClient
{
    /// <summary>
    /// The name of the queue this client is bound to.
    /// </summary>
    string QueueName { get; }

    ValueTask CreateQueueIfNotExistsAsync(IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);

    ValueTask ClearMessagesAsync(CancellationToken cancellationToken = default);

    ValueTask<IEnumerable<TMessage>> PeekMessagesAsync<TMessage>(int numMessages, CancellationToken cancellationToken = default);

    IEnumerable<TMessage> PeekMessages<TMessage>(int numMessages, CancellationToken cancellationToken = default);

    /// <inheritdoc cref="AzureStorageQueueClient.ReceiveMessagesAsync{TMessage}"/>
    ValueTask ReceiveMessagesAsync<TMessage>(
        Func<TMessage?, IDictionary<string, string>?, ValueTask> handleMessage,
        Func<Exception, IDictionary<string, string>?, ValueTask> handleException,
        int numMessages = 1,
        CancellationToken cancellationToken = default)
        where TMessage : class;

    /// <inheritdoc cref="AzureStorageQueueClient.SendMessageAsync{TMessage}"/>
    ValueTask<SendReceipt> SendMessageAsync<TMessage>(TMessage message, TimeSpan? visibilityTimeout = null, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default);
}
