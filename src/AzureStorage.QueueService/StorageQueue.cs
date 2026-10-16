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
    private const int ServiceMaxMessages = 32;

    private readonly QueueClient _client;
    private readonly IQueueMessageSerializer _serializer;
    private readonly ILogger<StorageQueue> _logger;
    private readonly QueueServiceTelemetrySettings _telemetry;
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
        TimeSpan? defaultVisibilityTimeout,
        bool createIfNotExists,
        Func<QueueClient>? poisonQueueFactory)
    {
        _client = client;
        _serializer = serializer;
        _logger = loggerFactory.CreateLogger<StorageQueue>();
        _telemetry = telemetry;
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
            ? QueueServiceDiagnostics.Activities.StartSendingMessageActivity(QueueTags())
            : null;

        try
        {
            await EnsureQueueExistsAsync(cancellationToken);

            BinaryData body = _serializer.Serialize(message);
            var timeToLive = options?.TimeToLive;
            // .NET's infinite sentinel is -1 ms; the service requires -1 whole second.
            if (timeToLive == Timeout.InfiniteTimeSpan) timeToLive = TimeSpan.FromSeconds(-1);
            SendReceipt receipt = await _client.SendMessageAsync(body, options?.VisibilityTimeout, timeToLive, cancellationToken);

            var tags = QueueTags();
            QueueServiceDiagnostics.Metrics.MessagesSent.Add(1, tags);
            QueueServiceDiagnostics.Metrics.MessageSize.Record(body.ToMemory().Length, tags);

            return receipt;
        }
        catch (Exception e)
        {
            _logger.LogSendFailed(QueueName, e);
            activity?.AddException(e);
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

    public void Deserialize<T>(QueueMessage<T> message)
    {
        if (message.Error is not null) return; // a decoding failure; there is nothing to deserialize
        var (body, error) = TryDeserialize<T>(message.RawBody);
        message.SetBody(body, error);
    }

    private async ValueTask<List<QueueMessage<T>>> ReceiveUndeserializedAsync<T>(int maxMessages, TimeSpan? visibilityTimeout, CancellationToken cancellationToken)
    {
        ValidateMaxMessages(maxMessages);

        var tags = QueueTags();
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
            var messages = new List<QueueMessage<T>>(count);
            if (count == 0) return messages;

            _logger.LogMessagesReceived(QueueName, count);
            activity?.AddEvent(new ActivityEvent(QueueServiceDiagnostics.Events.QueueMessageReceived));
            QueueServiceDiagnostics.Metrics.MessagesReceived.Add(count, QueueTags());

            foreach (var m in received)
            {
                var raw = m.Body ?? BinaryData.FromString(m.MessageText ?? string.Empty);
                messages.Add(new QueueMessage<T>(Info(m), raw, default, null, this));
            }

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
            activity?.AddException(e);
            throw;
        }
    }

    public async ValueTask<IReadOnlyList<PeekedQueueMessage<T>>> PeekAsync<T>(int maxMessages = 1, CancellationToken cancellationToken = default)
    {
        ValidateMaxMessages(maxMessages);
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
            var raw = m.Body ?? BinaryData.FromString(m.MessageText ?? string.Empty);
            var (body, error) = TryDeserialize<T>(raw);
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

    private (T? Body, DeserializationException? Error) TryDeserialize<T>(BinaryData raw)
    {
        try
        {
            return (_serializer.Deserialize<T>(raw), null);
        }
        catch (DeserializationException e)
        {
            return (default, e);
        }
        catch (Exception e)
        {
            // A custom serializer that throws something else still must not take the receive down.
            return (default, new DeserializationException($"Could not deserialize the queue message as {typeof(T).Name}: {e.Message}", e));
        }
    }

    private static void ValidateMaxMessages(int maxMessages)
    {
        if (maxMessages is < 1 or > ServiceMaxMessages)
            throw new ArgumentOutOfRangeException(nameof(maxMessages), maxMessages, $"The service allows 1 to {ServiceMaxMessages} messages per receive.");
    }

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

        // Raw body unchanged, so a poison message can be moved straight back to replay it. The reason goes to the log;
        // storage queue messages have no properties to carry it in. Copy-then-delete is two calls: a lease that expires
        // between them can leave a duplicate in the poison queue, which is the at-least-once trade.
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

    private TagList QueueTags() => new() { new(QueueServiceDiagnostics.Names.QueueName, QueueName) };

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
