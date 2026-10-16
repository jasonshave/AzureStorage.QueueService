using System.Diagnostics;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging;

namespace AzureStorage.QueueService;

/// <summary>
/// A client over an <see cref="InMemoryQueue"/>: what every <see cref="IQueueClientFactory.GetQueue()"/> hands out
/// once <c>AddInMemoryQueues</c> is registered. Mirrors <see cref="StorageQueue"/>, including the processor's
/// <see cref="IOwnedReceiver"/> path and the trace-context envelope, so a processor under test takes the same route
/// it takes in production.
/// </summary>
internal sealed class InMemoryStorageQueue : IStorageQueue, IQueueMessageOperations, IOwnedReceiver
{
    private static readonly TimeSpan ServiceDefaultVisibility = TimeSpan.FromSeconds(30);

    private readonly InMemoryQueue _queue;
    private readonly Lazy<InMemoryQueue> _poisonQueue;
    private readonly IQueueMessageSerializer _serializer;
    private readonly ILogger<InMemoryStorageQueue> _logger;
    private readonly TimeSpan? _defaultVisibilityTimeout;
    private readonly bool _propagateTraceContext;

    public InMemoryStorageQueue(
        InMemoryQueue queue,
        Func<InMemoryQueue> poisonQueue,
        IQueueMessageSerializer serializer,
        ILoggerFactory loggerFactory,
        TimeSpan? defaultVisibilityTimeout,
        bool propagateTraceContext)
    {
        _queue = queue;
        _poisonQueue = new Lazy<InMemoryQueue>(poisonQueue);
        _serializer = serializer;
        _logger = loggerFactory.CreateLogger<InMemoryStorageQueue>();
        _defaultVisibilityTimeout = defaultVisibilityTimeout;
        _propagateTraceContext = propagateTraceContext;
    }

    public string QueueName => _queue.Name;

    public ValueTask<SendReceipt> SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // No send span of its own, so the caller's current span is the one carried, as StorageQueue does when its
        // send span is turned off.
        var body = StorageQueueCommon.Encode(_serializer, message, _propagateTraceContext, Activity.Current, out _);
        return new(_queue.Send(body, options?.VisibilityTimeout, StorageQueueCommon.ServiceTimeToLive(options?.TimeToLive)));
    }

    public ValueTask<IReadOnlyList<QueueMessage<T>>> ReceiveAsync<T>(int maxMessages = 1, TimeSpan? visibilityTimeout = null, CancellationToken cancellationToken = default)
    {
        var messages = ReceiveUndeserialized<T>(maxMessages, visibilityTimeout, cancellationToken);
        foreach (var message in messages) Deserialize(message);
        return new(messages);
    }

    ValueTask<QueueMessage<T>?> IOwnedReceiver.ReceiveUndeserializedAsync<T>(TimeSpan visibilityTimeout, CancellationToken cancellationToken) =>
        new(ReceiveUndeserialized<T>(1, visibilityTimeout, cancellationToken).FirstOrDefault());

    public void Deserialize<T>(QueueMessage<T> message) => StorageQueueCommon.Deserialize(_serializer, message);

    private List<QueueMessage<T>> ReceiveUndeserialized<T>(int maxMessages, TimeSpan? visibilityTimeout, CancellationToken cancellationToken)
    {
        StorageQueueCommon.ValidateMaxMessages(maxMessages);
        cancellationToken.ThrowIfCancellationRequested();

        return _queue.Receive(maxMessages, visibilityTimeout ?? _defaultVisibilityTimeout ?? ServiceDefaultVisibility)
            .Select(m => StorageQueueCommon.Received<T>(new QueueMessageInfo(m.MessageId, m.PopReceipt, m.DequeueCount, m.InsertedOn, m.ExpiresOn, m.NextVisibleOn), m.Body, this))
            .ToList();
    }

    public ValueTask<IReadOnlyList<PeekedQueueMessage<T>>> PeekAsync<T>(int maxMessages = 1, CancellationToken cancellationToken = default)
    {
        StorageQueueCommon.ValidateMaxMessages(maxMessages);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<PeekedQueueMessage<T>> peeked = _queue.Peek(maxMessages)
            .Select(m =>
            {
                var raw = StorageQueueCommon.Peeked(m.Body);
                var (body, error) = StorageQueueCommon.TryDeserialize<T>(_serializer, raw);
                return new PeekedQueueMessage<T>(m.MessageId, m.DequeueCount, m.InsertedOn, m.ExpiresOn, raw, body, error);
            })
            .ToList();
        return new(peeked);
    }

    public ValueTask CreateIfNotExistsAsync(IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _queue.CreateIfNotExists(metadata);
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _queue.Clear();
        return ValueTask.CompletedTask;
    }

    public ValueTask<QueueProperties> GetPropertiesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(_queue.GetProperties());
    }

    ValueTask IQueueMessageOperations.DeleteAsync(string messageId, string popReceipt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _queue.Delete(messageId, popReceipt);
        return ValueTask.CompletedTask;
    }

    ValueTask<VisibilityReceipt> IQueueMessageOperations.UpdateVisibilityAsync(string messageId, string popReceipt, TimeSpan visibilityTimeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(_queue.UpdateVisibility(messageId, popReceipt, visibilityTimeout));
    }

    ValueTask IQueueMessageOperations.DeadLetterAsync(string messageId, string popReceipt, long dequeueCount, BinaryData rawBody, string? reason, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var poison = _poisonQueue.Value;

        // The same two steps, in the same order, as StorageQueue: copy the body as it arrived (envelope and all), then
        // delete with the receipt. A stale receipt therefore leaves a duplicate in the poison queue here too, which is
        // the at-least-once trade.
        poison.Send(rawBody, visibilityTimeout: null, timeToLive: null);
        _queue.Delete(messageId, popReceipt);

        _logger.LogDeadLettered(QueueName, messageId, poison.Name, dequeueCount, reason ?? "(none)");
        return ValueTask.CompletedTask;
    }
}
