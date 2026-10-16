using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using AzureStorage.QueueService.Telemetry;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using SdkQueueMessage = Azure.Storage.Queues.Models.QueueMessage;

namespace AzureStorage.QueueService;

/// <summary>
/// One queue over one SDK client. Thread-safe and long-lived; the factory keeps one per registered name.
/// Also implements the operations a <see cref="QueueMessage{T}"/> uses to settle itself.
/// </summary>
internal sealed class StorageQueue : IStorageQueue, IQueueMessageOperations, IOwnedReceiver
{
    private readonly QueueClient _client;
    private readonly IQueueMessageSerializer _serializer;
    private readonly ILogger<StorageQueue> _logger;
    private readonly QueueServiceTelemetrySettings _telemetry;
    private readonly QueueServiceMetrics _metrics;
    private readonly bool _propagateTraceContext;
    private readonly string? _serverAddress;
    private readonly int _serverPort;
    private readonly TimeSpan? _defaultVisibilityTimeout;
    private readonly bool _createIfNotExists;
    private readonly Func<QueueClient>? _poisonQueueFactory;
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private volatile bool _queueEnsured;
    private volatile bool _poisonQueueEnsured;
    private QueueClient? _poisonQueue;

    public StorageQueue(
        QueueClient client,
        IQueueMessageSerializer serializer,
        ILoggerFactory loggerFactory,
        QueueServiceTelemetrySettings telemetry,
        QueueServiceMetrics metrics,
        TimeSpan? defaultVisibilityTimeout,
        bool createIfNotExists,
        Func<QueueClient>? poisonQueueFactory,
        bool propagateTraceContext)
    {
        _client = client;
        _serializer = serializer;
        _logger = loggerFactory.CreateLogger<StorageQueue>();
        _telemetry = telemetry;
        _metrics = metrics;
        _propagateTraceContext = propagateTraceContext;
        _serverAddress = client.Uri?.Host;
        _serverPort = client.Uri?.Port ?? 0;
        _defaultVisibilityTimeout = defaultVisibilityTimeout;
        _createIfNotExists = createIfNotExists;
        _poisonQueueFactory = poisonQueueFactory;
    }

    public string QueueName => _client.Name;

    // ------------------------------------------------------------------------------------------------------------
    // Send
    // ------------------------------------------------------------------------------------------------------------

    public async ValueTask<SendReceipt> SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default)
    {
        using Activity? activity = _telemetry.CreateNewActivityOnMessageSend
            ? QueueServiceDiagnostics.Activities.StartSendingMessageActivity(SpanTags(QueueServiceDiagnostics.Messaging.Send))
            : null;

        try
        {
            await EnsureQueueExistsAsync(cancellationToken);

            // The send span when there is one, as the conventions expect of a producer; otherwise whatever the caller
            // is doing (an HTTP request, say). With neither there is no trace to continue and the message goes out plain.
            var body = StorageQueueCommon.Encode(_serializer, message, _propagateTraceContext, activity ?? Activity.Current, out var serializedSize);
            var timeToLive = StorageQueueCommon.ServiceTimeToLive(options?.TimeToLive);
            SendReceipt receipt = await _client.SendMessageAsync(body, options?.VisibilityTimeout, timeToLive, cancellationToken);

            activity?.SetTag(QueueServiceDiagnostics.Messaging.MessageId, receipt.MessageId);
            var tags = QueueServiceDiagnostics.MetricTags(QueueName);
            _metrics.MessagesSent.Add(1, tags);
            // The serializer's output, before any envelope, so the metric means what it meant in 3.0 whatever the setting.
            _metrics.MessageSize.Record(serializedSize, tags);

            return receipt;
        }
        catch (Exception e)
        {
            _logger.LogSendFailed(QueueName, e);
            activity.RecordFailure(e);
            throw;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Receive / peek
    // ------------------------------------------------------------------------------------------------------------

    public async ValueTask<IReadOnlyList<QueueMessage<T>>> ReceiveAsync<T>(int maxMessages = 1, TimeSpan? visibilityTimeout = null, CancellationToken cancellationToken = default)
    {
        var messages = await ReceiveUndeserializedAsync<T>(maxMessages, visibilityTimeout, cancellationToken);
        foreach (var message in messages) Deserialize(message);
        return messages;
    }

    // IOwnedReceiver: the processor's path. The message is handed over with its raw body so renewal can be armed
    // before deserialization runs; see IOwnedReceiver.
    async ValueTask<QueueMessage<T>?> IOwnedReceiver.ReceiveUndeserializedAsync<T>(TimeSpan visibilityTimeout, CancellationToken cancellationToken)
    {
        var messages = await ReceiveUndeserializedAsync<T>(1, visibilityTimeout, cancellationToken);
        return messages.Count == 0 ? null : messages[0];
    }

    public void Deserialize<T>(QueueMessage<T> message) => StorageQueueCommon.Deserialize(_serializer, message);

    private async ValueTask<List<QueueMessage<T>>> ReceiveUndeserializedAsync<T>(int maxMessages, TimeSpan? visibilityTimeout, CancellationToken cancellationToken)
    {
        StorageQueueCommon.ValidateMaxMessages(maxMessages);

        var tags = SpanTags(QueueServiceDiagnostics.Messaging.Receive);
        tags.Add(QueueServiceDiagnostics.Names.QueueMessageCount, maxMessages);
        using Activity? activity = _telemetry.CreateNewActivityOnMessageRetrieval
            ? QueueServiceDiagnostics.Activities.StartReadingQueueActivity(tags)
            : null;

        try
        {
            await EnsureQueueExistsAsync(cancellationToken);

            // Messages the SDK could not Base64-decode are raised as events during the call; collect them so they
            // come back as messages with Error set instead of being dropped from the batch.
            SdkQueueMessage[] received;
            var undecodable = DecodingFailures.Begin();
            try
            {
                received = await _client.ReceiveMessagesAsync(maxMessages, visibilityTimeout ?? _defaultVisibilityTimeout, cancellationToken);
            }
            finally
            {
                DecodingFailures.End();
            }

            var count = received.Length + undecodable.Count;
            // The conventions: a batch count for the batch form of the call, a message id for the single-message form.
            if (maxMessages > 1) activity?.SetTag(QueueServiceDiagnostics.Messaging.BatchMessageCount, count);
            var messages = new List<QueueMessage<T>>(count);
            if (count == 0) return messages;

            _logger.LogMessagesReceived(QueueName, count);
            activity?.AddEvent(new ActivityEvent(QueueServiceDiagnostics.Events.QueueMessageReceived));
            _metrics.MessagesReceived.Add(count, QueueServiceDiagnostics.MetricTags(QueueName));

            foreach (var m in received)
                messages.Add(StorageQueueCommon.Received<T>(Info(m), m.Body ?? BinaryData.FromString(m.MessageText ?? string.Empty), this));

            if (maxMessages == 1 && messages.Count == 1) activity?.SetTag(QueueServiceDiagnostics.Messaging.MessageId, messages[0].MessageId);

            foreach (var failure in undecodable)
            {
                if (failure.ReceivedMessage is not { } m) continue;
                _logger.LogDecodingFailed(QueueName, m.MessageId, m.DequeueCount);
                var raw = BinaryData.FromString(m.MessageText ?? string.Empty);
                var error = new DeserializationException($"Message {m.MessageId} is not valid Base64, so the SDK could not decode it.");
                messages.Add(new QueueMessage<T>(Info(m), raw, default, error, this));
            }

            return messages;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _logger.LogReceiveFailed(QueueName, e);
            activity.RecordFailure(e);
            throw;
        }
    }

    public async ValueTask<IReadOnlyList<PeekedQueueMessage<T>>> PeekAsync<T>(int maxMessages = 1, CancellationToken cancellationToken = default)
    {
        StorageQueueCommon.ValidateMaxMessages(maxMessages);
        await EnsureQueueExistsAsync(cancellationToken);

        PeekedMessage[] peeked;
        var undecodable = DecodingFailures.Begin();
        try
        {
            peeked = await _client.PeekMessagesAsync(maxMessages, cancellationToken);
        }
        finally
        {
            DecodingFailures.End();
        }

        var messages = new List<PeekedQueueMessage<T>>(peeked.Length + undecodable.Count);
        foreach (var m in peeked)
        {
            var raw = StorageQueueCommon.Peeked(m.Body ?? BinaryData.FromString(m.MessageText ?? string.Empty));
            var (body, error) = StorageQueueCommon.TryDeserialize<T>(_serializer, raw);
            messages.Add(new PeekedQueueMessage<T>(m.MessageId, m.DequeueCount, m.InsertedOn, m.ExpiresOn, raw, body, error));
        }

        foreach (var failure in undecodable)
        {
            if (failure.PeekedMessage is not { } m) continue;
            var raw = BinaryData.FromString(m.MessageText ?? string.Empty);
            var error = new DeserializationException($"Message {m.MessageId} is not valid Base64, so the SDK could not decode it.");
            messages.Add(new PeekedQueueMessage<T>(m.MessageId, m.DequeueCount, m.InsertedOn, m.ExpiresOn, raw, default, error));
        }

        return messages;
    }

    private static QueueMessageInfo Info(SdkQueueMessage m) =>
        new(m.MessageId, m.PopReceipt, m.DequeueCount, m.InsertedOn, m.ExpiresOn, m.NextVisibleOn);

    // ------------------------------------------------------------------------------------------------------------
    // Admin
    // ------------------------------------------------------------------------------------------------------------

    public async ValueTask CreateIfNotExistsAsync(IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        await _client.CreateIfNotExistsAsync(metadata, cancellationToken);
        _queueEnsured = true;
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        await EnsureQueueExistsAsync(cancellationToken);
        await _client.ClearMessagesAsync(cancellationToken);
    }

    public async ValueTask<QueueProperties> GetPropertiesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureQueueExistsAsync(cancellationToken);
        var response = await _client.GetPropertiesAsync(cancellationToken);
        return response.Value;
    }

    // ------------------------------------------------------------------------------------------------------------
    // IQueueMessageOperations — what a QueueMessage<T> calls to settle itself
    // ------------------------------------------------------------------------------------------------------------

    async ValueTask IQueueMessageOperations.DeleteAsync(string messageId, string popReceipt, CancellationToken cancellationToken)
    {
        await _client.DeleteMessageAsync(messageId, popReceipt, cancellationToken);
    }

    async ValueTask<VisibilityReceipt> IQueueMessageOperations.UpdateVisibilityAsync(string messageId, string popReceipt, TimeSpan visibilityTimeout, CancellationToken cancellationToken)
    {
        // No message text: only the visibility changes.
        UpdateReceipt receipt = await _client.UpdateMessageAsync(messageId, popReceipt, (string?)null, visibilityTimeout, cancellationToken);
        return new VisibilityReceipt(receipt.PopReceipt, receipt.NextVisibleOn);
    }

    async ValueTask IQueueMessageOperations.DeadLetterAsync(string messageId, string popReceipt, long dequeueCount, BinaryData rawBody, string? reason, CancellationToken cancellationToken)
    {
        if (_poisonQueueFactory is null)
            throw new InvalidOperationException($"Queue '{QueueName}' has no poison queue configured, so messages cannot be dead-lettered.");

        var poison = _poisonQueue ??= _poisonQueueFactory();

        // The body exactly as it arrived, trace-context envelope and all, so a poison message can be moved straight back
        // to replay it. The reason goes to the log; storage queue messages have no properties to carry it in.
        // Copy-then-delete is two calls: a lease that expires between them can leave a duplicate in the poison queue,
        // which is the at-least-once trade.
        if (!_poisonQueueEnsured)
        {
            await poison.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            _poisonQueueEnsured = true;
        }

        try
        {
            await poison.SendMessageAsync(rawBody, cancellationToken: cancellationToken);
        }
        catch (RequestFailedException e) when (e.ErrorCode == QueueErrorCode.QueueNotFound)
        {
            // Someone deleted the poison queue since it was created; recreate it on the next attempt.
            _poisonQueueEnsured = false;
            throw;
        }
        await _client.DeleteMessageAsync(messageId, popReceipt, cancellationToken);

        _logger.LogDeadLettered(QueueName, messageId, poison.Name, dequeueCount, reason ?? "(none)");
    }

    // ------------------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------------------

    // Spans only, and they also say which account they talked to; metrics keep their 3.0 tags (MetricTags).
    private TagList SpanTags(string operation)
    {
        var tags = QueueServiceDiagnostics.OperationTags(QueueName, operation);
        if (_serverAddress is null) return tags;
        tags.Add(QueueServiceDiagnostics.Messaging.ServerAddress, _serverAddress);
        tags.Add(QueueServiceDiagnostics.Messaging.ServerPort, _serverPort);
        return tags;
    }

    // Queue creation happens on the first operation, once, asynchronously — never while resolving from DI.
    private async ValueTask EnsureQueueExistsAsync(CancellationToken cancellationToken)
    {
        if (!_createIfNotExists || _queueEnsured) return;

        await _createLock.WaitAsync(cancellationToken);
        try
        {
            if (_queueEnsured) return;
            await _client.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            _queueEnsured = true;
        }
        finally
        {
            _createLock.Release();
        }
    }
}
