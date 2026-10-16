using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using AzureStorage.QueueService.Telemetry;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace AzureStorage.QueueService;

/// <summary>Per-client behaviour of a <see cref="StorageQueue"/> that is not about the message format.</summary>
/// <param name="DefaultVisibilityTimeout">What a receive uses when the call does not say; null for the service default.</param>
/// <param name="CreateIfNotExists">Create the queue on the first operation.</param>
/// <param name="PoisonTimeToLive">How long a dead-lettered message lives on the poison queue; null for never.</param>
/// <param name="Time">The clock for the dead-letter timestamp.</param>
internal sealed record StorageQueueOptions(
    TimeSpan? DefaultVisibilityTimeout = null,
    bool CreateIfNotExists = false,
    TimeSpan? PoisonTimeToLive = null,
    TimeProvider? Time = null);

/// <summary>
/// One queue: sending, receiving, settling, dead-lettering and administration, over a raw transport (the Azure SDK, or
/// an in-memory queue in tests). Thread-safe and long-lived; the factory keeps one per registered name. Also implements
/// the operations a <see cref="QueueMessage{T}"/> uses to settle itself.
/// </summary>
internal sealed class StorageQueue : IStorageQueue, IQueueMessageOperations, IDeadLetterOperations, IOwnedReceiver
{
    /// <summary>The longest dead-letter reason kept with the message; the rest is in the log.</summary>
    private const int MaxReasonLength = 1024;

    private readonly IQueueTransport _transport;
    private readonly Func<IQueueTransport>? _poisonQueueFactory;
    private readonly ILogger<StorageQueue> _logger;
    private readonly QueueServiceTelemetrySettings _telemetry;
    private readonly QueueServiceMetrics _metrics;
    private readonly StorageQueueOptions _options;
    private readonly TimeProvider _time;
    private readonly string? _serverAddress;
    private readonly int _serverPort;
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private volatile bool _queueEnsured;
    private volatile bool _poisonQueueEnsured;
    private IQueueTransport? _poisonQueue;
    private DeadLetterQueue? _deadLetterQueue;

    public StorageQueue(
        IQueueTransport transport,
        Func<IQueueTransport>? poisonQueueFactory,
        MessageCodec codec,
        ILoggerFactory loggerFactory,
        QueueServiceTelemetrySettings telemetry,
        QueueServiceMetrics metrics,
        StorageQueueOptions options)
    {
        _transport = transport;
        _poisonQueueFactory = poisonQueueFactory;
        Codec = codec;
        _logger = loggerFactory.CreateLogger<StorageQueue>();
        _telemetry = telemetry;
        _metrics = metrics;
        _options = options;
        _time = options.Time ?? TimeProvider.System;
        _serverAddress = transport.Uri?.Host;
        _serverPort = transport.Uri?.Port ?? 0;
    }

    /// <summary>Over an SDK client, with the 3.0 knobs; for tests.</summary>
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
        : this(
            new SdkQueueTransport(client, base64Encoded: false),
            poisonQueueFactory is null ? null : () => new SdkQueueTransport(poisonQueueFactory(), base64Encoded: false),
            new MessageCodec(serializer, propagateTraceContext),
            loggerFactory,
            telemetry,
            metrics,
            new StorageQueueOptions(defaultVisibilityTimeout, createIfNotExists))
    {
    }

    public string QueueName => _transport.Name;

    internal MessageCodec Codec { get; }

    internal IQueueTransport Transport => _transport;

    /// <summary>This queue's poison queue, for inspection and replay.</summary>
    internal IDeadLetterQueue DeadLetterQueue => _deadLetterQueue ??= new DeadLetterQueue(this);

    // ------------------------------------------------------------------------------------------------------------
    // Send
    // ------------------------------------------------------------------------------------------------------------

    public async ValueTask<SendResult> SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default)
    {
        ValidateSendOptions(options);

        using Activity? activity = _telemetry.CreateNewActivityOnMessageSend
            ? QueueServiceDiagnostics.Activities.StartSendingMessageActivity(SpanTags(QueueServiceDiagnostics.Messaging.Send))
            : null;

        var start = Stopwatch.GetTimestamp();
        try
        {
            await EnsureQueueExistsAsync(cancellationToken);

            // The send span when there is one, as the conventions expect of a producer; otherwise whatever the caller
            // is doing (an HTTP request, say). With neither there is no trace to continue and the message goes out plain.
            var encoded = await Codec.EncodeAsync(QueueName, _transport.Base64Encoded, message, options, activity ?? Activity.Current, cancellationToken);
            SendResult receipt;
            try
            {
                receipt = await _transport.SendAsync(encoded.Wire, options?.VisibilityTimeout, ServiceTimeToLive(options?.TimeToLive), cancellationToken);
            }
            catch when (encoded.BlobReference is { } blob)
            {
                // Nothing on the queue refers to it.
                await DeleteStoredBodyAsync(blob);
                throw;
            }

            activity?.SetTag(QueueServiceDiagnostics.Messaging.MessageId, receipt.MessageId);
            var tags = QueueServiceDiagnostics.MetricTags(QueueName);
            _metrics.MessagesSent.Add(1, tags);
            // The serializer's output, before any envelope, so the metric means what it meant in 3.0 whatever the settings.
            _metrics.MessageSize.Record(encoded.SerializedSize, tags);
            RecordOperation(QueueServiceDiagnostics.Messaging.Send, QueueServiceDiagnostics.Messaging.Send, start);
            _metrics.SentMessages.Add(1, QueueServiceDiagnostics.MessagingTags(QueueName, QueueServiceDiagnostics.Messaging.Send, QueueServiceDiagnostics.Messaging.Send));

            return receipt;
        }
        catch (Exception e)
        {
            _logger.LogSendFailed(QueueName, e);
            activity.RecordFailure(e);
            RecordOperation(QueueServiceDiagnostics.Messaging.Send, QueueServiceDiagnostics.Messaging.Send, start, e);
            _metrics.SentMessages.Add(1, QueueServiceDiagnostics.MessagingTags(QueueName, QueueServiceDiagnostics.Messaging.Send, QueueServiceDiagnostics.Messaging.Send, e));
            throw;
        }
    }

    private static void ValidateSendOptions(SendOptions? options)
    {
        if (options is null) return;

        if (options.VisibilityTimeout is { } delay && (delay < TimeSpan.Zero || delay > QueueClientSettings.MaxVisibility))
            throw new ArgumentOutOfRangeException(nameof(options), delay, "SendOptions.VisibilityTimeout (Delay) must be 0 to 7 days.");

        // Never expiring, in .NET's terms or the service's.
        if (options.TimeToLive is not { } ttl || ttl == Timeout.InfiniteTimeSpan || ttl == InfiniteTimeToLive) return;

        if (ttl < TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(options), ttl, "SendOptions.TimeToLive must be at least 1 second, or Timeout.InfiniteTimeSpan for never.");

        if (options.VisibilityTimeout is { } visibility && visibility >= ttl)
            throw new ArgumentOutOfRangeException(nameof(options), visibility, $"SendOptions.VisibilityTimeout (Delay) of {visibility} is not less than the TimeToLive of {ttl}: the message would expire before anyone could receive it.");
    }

    /// <summary>The service's "never expires": -1 whole second.</summary>
    internal static readonly TimeSpan InfiniteTimeToLive = TimeSpan.FromSeconds(-1);

    /// <summary>.NET's infinite sentinel is -1 ms; the service requires -1 whole second.</summary>
    internal static TimeSpan? ServiceTimeToLive(TimeSpan? timeToLive) =>
        timeToLive == Timeout.InfiniteTimeSpan ? InfiniteTimeToLive : timeToLive;

    // ------------------------------------------------------------------------------------------------------------
    // Receive / peek
    // ------------------------------------------------------------------------------------------------------------

    public async ValueTask<IReadOnlyList<QueueMessage<T>>> ReceiveAsync<T>(int maxMessages = 1, TimeSpan? visibilityTimeout = null, CancellationToken cancellationToken = default)
    {
        if (visibilityTimeout is { } visibility && (visibility < QueueClientSettings.MinVisibility || visibility > QueueClientSettings.MaxVisibility))
            throw new ArgumentOutOfRangeException(nameof(visibilityTimeout), visibility, "The service accepts a visibility timeout of 1 second to 7 days.");

        var messages = await ReceiveCoreAsync<T>(maxMessages, visibilityTimeout ?? _options.DefaultVisibilityTimeout, logFailure: true, cancellationToken);
        foreach (var message in messages)
        {
            if (await ResolvePayloadAsync(message.Lease, cancellationToken) is { } error) message.SetBody(default, error);
            else Deserialize(message);
        }

        return messages;
    }

    // IOwnedReceiver: the processor's path; see IOwnedReceiver.
    ValueTask<IReadOnlyList<QueueMessage<T>>> IOwnedReceiver.ReceiveForProcessingAsync<T>(int maxMessages, TimeSpan visibilityTimeout, CancellationToken cancellationToken) =>
        ReceiveCoreAsync<T>(maxMessages, visibilityTimeout, logFailure: false, cancellationToken);

    public ValueTask<DeserializationException?> ResolvePayloadAsync(MessageLease message, CancellationToken cancellationToken) =>
        Codec.ResolvePayloadAsync(message, (reference, e) => _logger.LogLargeMessageDeleteFailed(QueueName, message.MessageId, reference, e), cancellationToken);

    public void Deserialize<T>(QueueMessage<T> message) => Codec.Deserialize(message);

    private async ValueTask<IReadOnlyList<QueueMessage<T>>> ReceiveCoreAsync<T>(int maxMessages, TimeSpan? visibilityTimeout, bool logFailure, CancellationToken cancellationToken)
    {
        ValidateMaxMessages(maxMessages);

        var tags = SpanTags(QueueServiceDiagnostics.Messaging.Receive);
        tags.Add(QueueServiceDiagnostics.Names.QueueMessageCount, maxMessages);
        using Activity? activity = _telemetry.CreateNewActivityOnMessageRetrieval
            ? QueueServiceDiagnostics.Activities.StartReadingQueueActivity(tags)
            : null;

        var start = Stopwatch.GetTimestamp();
        try
        {
            await EnsureQueueExistsAsync(cancellationToken);
            var received = await _transport.ReceiveAsync(maxMessages, visibilityTimeout, cancellationToken);

            var count = received.Count;
            RecordOperation(QueueServiceDiagnostics.Messaging.Receive, QueueServiceDiagnostics.Messaging.Receive, start);
            _metrics.ReceiveRequests.Add(1, ReceiveResultTags(count == 0 ? "empty" : "message"));

            // The conventions: a batch count for the batch form of the call, a message id for the single-message form.
            if (maxMessages > 1) activity?.SetTag(QueueServiceDiagnostics.Messaging.BatchMessageCount, count);
            var messages = new List<QueueMessage<T>>(count);
            if (count == 0) return messages;

            _logger.LogMessagesReceived(QueueName, count);
            activity?.AddEvent(new ActivityEvent(QueueServiceDiagnostics.Events.QueueMessageReceived));
            _metrics.MessagesReceived.Add(count, QueueServiceDiagnostics.MetricTags(QueueName));
            _metrics.ConsumedMessages.Add(count, QueueServiceDiagnostics.MessagingTags(QueueName, QueueServiceDiagnostics.Messaging.Receive, QueueServiceDiagnostics.Messaging.Receive));

            foreach (var m in received)
            {
                if (m.Undecodable) _logger.LogDecodingFailed(QueueName, m.Info.MessageId, m.Info.DequeueCount);
                messages.Add(MessageCodec.Received<T>(m, this));
            }

            if (maxMessages == 1 && messages.Count == 1) activity?.SetTag(QueueServiceDiagnostics.Messaging.MessageId, messages[0].MessageId);
            return messages;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _metrics.ReceiveRequests.Add(1, ReceiveResultTags("error"));
            RecordOperation(QueueServiceDiagnostics.Messaging.Receive, QueueServiceDiagnostics.Messaging.Receive, start, e);
            // The processor logs its own failures, once, with the backoff it is about to take.
            if (logFailure) _logger.LogReceiveFailed(QueueName, e);
            activity.RecordFailure(e);
            throw;
        }
    }

    public async ValueTask<IReadOnlyList<PeekedQueueMessage<T>>> PeekAsync<T>(int maxMessages = 1, CancellationToken cancellationToken = default)
    {
        ValidateMaxMessages(maxMessages);
        await EnsureQueueExistsAsync(cancellationToken);

        var peeked = await _transport.PeekAsync(maxMessages, cancellationToken);
        var messages = new List<PeekedQueueMessage<T>>(peeked.Count);
        foreach (var m in peeked)
        {
            var message = MessageCodec.Received<T>(m, this);
            if (await ResolvePayloadAsync(message.Lease, cancellationToken) is { } error) message.SetBody(default, error);
            else Deserialize(message);

            messages.Add(new PeekedQueueMessage<T>(m.Info.MessageId, m.Info.DequeueCount, m.Info.InsertedOn, m.Info.ExpiresOn, message.RawBody, message.Body, message.Error)
            {
                Headers = message.Headers,
            });
        }

        return messages;
    }

    internal static void ValidateMaxMessages(int maxMessages)
    {
        if (maxMessages is < 1 or > ServiceMaxMessages)
            throw new ArgumentOutOfRangeException(nameof(maxMessages), maxMessages, $"The service allows 1 to {ServiceMaxMessages} messages per receive.");
    }

    internal const int ServiceMaxMessages = 32;

    // ------------------------------------------------------------------------------------------------------------
    // Admin
    // ------------------------------------------------------------------------------------------------------------

    public async ValueTask CreateIfNotExistsAsync(IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        await _transport.CreateIfNotExistsAsync(metadata, cancellationToken);
        _queueEnsured = true;
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        await EnsureQueueExistsAsync(cancellationToken);
        await _transport.ClearAsync(cancellationToken);
    }

    public async ValueTask<QueueStatus> GetPropertiesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureQueueExistsAsync(cancellationToken);
        return await _transport.GetPropertiesAsync(cancellationToken);
    }

    // ------------------------------------------------------------------------------------------------------------
    // IQueueMessageOperations — what a QueueMessage<T> calls to settle itself
    // ------------------------------------------------------------------------------------------------------------

    async ValueTask IQueueMessageOperations.DeleteAsync(string messageId, string popReceipt, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            await _transport.DeleteAsync(messageId, popReceipt, cancellationToken);
            RecordOperation(QueueServiceDiagnostics.Messaging.Settle, "complete", start);
        }
        catch (Exception e)
        {
            RecordOperation(QueueServiceDiagnostics.Messaging.Settle, "complete", start, e);
            throw;
        }
    }

    async ValueTask<VisibilityReceipt> IQueueMessageOperations.UpdateVisibilityAsync(string messageId, string popReceipt, TimeSpan visibilityTimeout, CancellationToken cancellationToken)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            var receipt = await _transport.UpdateVisibilityAsync(messageId, popReceipt, visibilityTimeout, cancellationToken);
            RecordOperation(QueueServiceDiagnostics.Messaging.Settle, "update_visibility", start);
            return receipt;
        }
        catch (Exception e)
        {
            RecordOperation(QueueServiceDiagnostics.Messaging.Settle, "update_visibility", start, e);
            throw;
        }
    }

    ValueTask IQueueMessageOperations.DeadLetterAsync(string messageId, string popReceipt, long dequeueCount, BinaryData rawBody, string? reason, CancellationToken cancellationToken) =>
        DeadLetterCoreAsync(messageId, popReceipt, dequeueCount, rawBody, envelope: null, reason, DeadLetterCategories.Handler, cancellationToken);

    ValueTask IDeadLetterOperations.DeadLetterAsync(MessageLease message, string? reason, string category, CancellationToken cancellationToken) =>
        DeadLetterCoreAsync(message.MessageId, message.PopReceipt, message.DequeueCount, message.WireBody, message.Envelope, reason, category, cancellationToken);

    private async ValueTask DeadLetterCoreAsync(string messageId, string popReceipt, long dequeueCount, BinaryData wire, MessageEnvelope? envelope, string? reason, string category, CancellationToken cancellationToken)
    {
        var poison = PoisonQueue();
        var info = new DeadLetterInfo(Truncate(reason), category, messageId, dequeueCount, QueueName, _time.GetUtcNow());
        var body = DeadLetterBody(wire, envelope, info, poison.Base64Encoded);

        // Copy-then-delete is two calls: a lease that expires between them can leave a duplicate in the poison queue,
        // which is the at-least-once trade. A body kept in Blob storage stays there, so a replay still has it.
        await EnsurePoisonQueueExistsAsync(cancellationToken);
        try
        {
            await poison.SendAsync(body, visibilityTimeout: null, ServiceTimeToLive(_options.PoisonTimeToLive ?? Timeout.InfiniteTimeSpan), cancellationToken);
        }
        catch (RequestFailedException e) when (e.ErrorCode == QueueErrorCode.QueueNotFound)
        {
            // Someone deleted the poison queue since it was created; recreate it on the next attempt.
            _poisonQueueEnsured = false;
            throw;
        }

        await _transport.DeleteAsync(messageId, popReceipt, cancellationToken);

        _logger.LogDeadLettered(QueueName, messageId, poison.Name, dequeueCount, reason ?? "(none)");
        var tags = QueueServiceDiagnostics.MetricTags(QueueName);
        tags.Add(QueueServiceDiagnostics.Names.DeadLetterReason, category);
        _metrics.DeadLettered.Add(1, tags);
    }

    /// <summary>
    /// The copy for the poison queue: the message as it arrived, in an envelope that also records why and from where.
    /// What cannot be annotated (an envelope this version cannot read, or a copy that would be over the size limit) goes
    /// as it arrived: a copy without the reason beats no copy.
    /// </summary>
    private static BinaryData DeadLetterBody(BinaryData wire, MessageEnvelope? envelope, DeadLetterInfo info, bool base64Encoded)
    {
        if (envelope is null)
        {
            switch (MessageEnvelope.TryRead(wire, out var parsed, out _))
            {
                case EnvelopeKind.Unreadable:
                    return wire;
                case EnvelopeKind.Envelope:
                    envelope = parsed;
                    break;
            }
        }

        MessageEnvelope copy;
        if (envelope is null)
        {
            copy = new MessageEnvelope();
            copy.SetBody(wire);
        }
        else
        {
            copy = envelope.Clone();
        }

        copy.DeadLetter = info;
        var body = copy.Write();
        return MessageCodec.WireSize(body.ToMemory().Length, base64Encoded) <= MessageCodec.MaxMessageBytes ? body : wire;
    }

    private static string? Truncate(string? reason) =>
        reason is { Length: > MaxReasonLength } ? reason[..MaxReasonLength] + "…" : reason;

    // ------------------------------------------------------------------------------------------------------------
    // The poison queue, for DeadLetterQueue
    // ------------------------------------------------------------------------------------------------------------

    internal IQueueTransport PoisonQueue()
    {
        if (_poisonQueueFactory is null)
            throw new InvalidOperationException($"Queue '{QueueName}' has no poison queue configured, so messages cannot be dead-lettered.");

        return _poisonQueue ??= _poisonQueueFactory();
    }

    internal async ValueTask EnsurePoisonQueueExistsAsync(CancellationToken cancellationToken)
    {
        if (_poisonQueueEnsured) return;
        await PoisonQueue().CreateIfNotExistsAsync(null, cancellationToken);
        _poisonQueueEnsured = true;
    }

    /// <summary>Puts bytes on this queue as they are: a replayed message.</summary>
    internal async ValueTask SendWireAsync(BinaryData wire, CancellationToken cancellationToken)
    {
        await EnsureQueueExistsAsync(cancellationToken);
        await _transport.SendAsync(wire, visibilityTimeout: null, timeToLive: null, cancellationToken);
    }

    internal async ValueTask DeleteStoredBodyAsync(string reference)
    {
        if (Codec.LargeMessageStore is not { } store) return;
        try
        {
            await store.DeleteAsync(reference, CancellationToken.None);
        }
        catch (Exception e)
        {
            _logger.LogLargeMessageDeleteFailed(QueueName, null, reference, e);
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------------------

    // Spans only, and they also say which account they talked to; the library's own metrics keep their 3.0 tags.
    private TagList SpanTags(string operation)
    {
        var tags = QueueServiceDiagnostics.OperationTags(QueueName, operation);
        if (_serverAddress is null) return tags;
        tags.Add(QueueServiceDiagnostics.Messaging.ServerAddress, _serverAddress);
        tags.Add(QueueServiceDiagnostics.Messaging.ServerPort, _serverPort);
        return tags;
    }

    private TagList ReceiveResultTags(string result)
    {
        var tags = QueueServiceDiagnostics.MetricTags(QueueName);
        tags.Add(QueueServiceDiagnostics.Names.ReceiveResult, result);
        return tags;
    }

    private void RecordOperation(string operationType, string operationName, long start, Exception? error = null)
    {
        var tags = QueueServiceDiagnostics.MessagingTags(QueueName, operationType, operationName, error);
        if (_serverAddress is not null)
        {
            tags.Add(QueueServiceDiagnostics.Messaging.ServerAddress, _serverAddress);
            tags.Add(QueueServiceDiagnostics.Messaging.ServerPort, _serverPort);
        }
        _metrics.OperationDuration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds, tags);
    }

    // Queue creation happens on the first operation, once, asynchronously — never while resolving from DI.
    private async ValueTask EnsureQueueExistsAsync(CancellationToken cancellationToken)
    {
        if (!_options.CreateIfNotExists || _queueEnsured) return;

        await _createLock.WaitAsync(cancellationToken);
        try
        {
            if (_queueEnsured) return;
            await _transport.CreateIfNotExistsAsync(null, cancellationToken);
            _queueEnsured = true;
        }
        finally
        {
            _createLock.Release();
        }
    }
}
