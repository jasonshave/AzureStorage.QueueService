using Azure.Storage.Queues.Models;
using AzureStorage.QueueService.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureStorage.QueueService;

/// <summary>
/// Implementation of a typed queue client that wraps the AzureStorageQueueClient
/// for strongly-typed message operations.
/// </summary>
/// <typeparam name="TMessage">The type of message this client handles</typeparam>
internal sealed class TypedQueueClient<TMessage> : ITypedQueueClient<TMessage> where TMessage : class
{
    private readonly AzureStorageQueueClient _queueClient;

    public TypedQueueClient(AzureStorageQueueClient queueClient)
    {
        _queueClient = queueClient;
    }

    public ValueTask CreateQueueIfNotExistsAsync(IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
        => _queueClient.CreateQueueIfNotExistsAsync(metadata, cancellationToken);

    public ValueTask ClearMessagesAsync(CancellationToken cancellationToken = default)
        => _queueClient.ClearMessagesAsync(cancellationToken);

    public ValueTask<IEnumerable<TMessage>> PeekMessagesAsync(int numMessages, CancellationToken cancellationToken = default)
        => _queueClient.PeekMessagesAsync<TMessage>(numMessages, cancellationToken);

    public IEnumerable<TMessage> PeekMessages(int numMessages, CancellationToken cancellationToken = default)
        => _queueClient.PeekMessages<TMessage>(numMessages, cancellationToken);

    public ValueTask ReceiveMessagesAsync(
        Func<TMessage?, IDictionary<string, string>?, ValueTask> handleMessage, 
        Func<Exception, IDictionary<string, string>?, ValueTask> handleException, 
        int numMessages = 1, 
        CancellationToken cancellationToken = default)
        => _queueClient.ReceiveMessagesAsync<TMessage>(handleMessage, handleException, numMessages, cancellationToken);

    public ValueTask<SendReceipt> SendMessageAsync(
        TMessage message, 
        TimeSpan? visibilityTimeout = null, 
        TimeSpan? timeToLive = null, 
        CancellationToken cancellationToken = default)
        => _queueClient.SendMessageAsync<TMessage>(message, visibilityTimeout, timeToLive, cancellationToken);
}