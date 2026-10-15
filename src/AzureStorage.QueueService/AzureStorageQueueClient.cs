using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using AzureStorage.QueueService.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace AzureStorage.QueueService;

public class AzureStorageQueueClient : IAzureStorageQueueClient
{
    // The service default when no visibility timeout is supplied on receive.
    private static readonly TimeSpan DefaultVisibilityTimeout = TimeSpan.FromSeconds(30);

    private readonly IMessageConverter _messageConverter;
    private readonly QueueClient _queueClient;
    private readonly ILogger<AzureStorageQueueClient> _logger;
    private readonly QueueServiceTelemetrySettings _telemetrySettings;
    private readonly TimeSpan? _visibilityTimeout;
    private readonly bool _createIfNotExists;
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private volatile bool _queueEnsured;

    internal AzureStorageQueueClient(
        IMessageConverter messageConverter,
        QueueClient queueClient,
        ILoggerFactory loggerFactory,
        IOptions<QueueServiceTelemetrySettings> telemetrySettingsOptions,
        TimeSpan? visibilityTimeout = null,
        bool createIfNotExists = false)
    {
        _messageConverter = messageConverter;
        _queueClient = queueClient;
        _logger = loggerFactory.CreateLogger<AzureStorageQueueClient>();
        _telemetrySettings = telemetrySettingsOptions.Value;
        _visibilityTimeout = visibilityTimeout;
        _createIfNotExists = createIfNotExists;
    }

    public string QueueName => _queueClient.Name;

    public async ValueTask CreateQueueIfNotExistsAsync(IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
    {
        await _queueClient.CreateIfNotExistsAsync(metadata, cancellationToken);
        _queueEnsured = true;
    }

    public async ValueTask ClearMessagesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureQueueExistsAsync(cancellationToken);
        await _queueClient.ClearMessagesAsync(cancellationToken);
    }

    public async ValueTask<IEnumerable<TMessage>> PeekMessagesAsync<TMessage>(int numMessages, CancellationToken cancellationToken = default)
    {
        await EnsureQueueExistsAsync(cancellationToken);
        return (await _queueClient.PeekMessagesAsync(numMessages, cancellationToken)).Value.Convert<TMessage>(_messageConverter);
    }

    public IEnumerable<TMessage> PeekMessages<TMessage>(int numMessages, CancellationToken cancellationToken = default)
    {
        EnsureQueueExists(cancellationToken);
        return _queueClient.PeekMessages(numMessages, cancellationToken).Value.Convert<TMessage>(_messageConverter);
    }

    /// <summary>
    /// Receives up to <paramref name="numMessages"/> messages, deserializes each one and passes it to
    /// <paramref name="handleMessage"/>. A message is deleted from the queue only after its handler completes;
    /// if the handler throws, the message is left on the queue and becomes visible again once its visibility
    /// timeout expires.
    /// </summary>
    /// <remarks>
    /// This method does not throw for failures in the queue read, the message conversion, the handler, the delete,
    /// or <paramref name="handleException"/> itself. All of them are logged and passed to
    /// <paramref name="handleException"/> (with <c>null</c> metadata when the read itself failed), and processing
    /// continues with the next message in the batch. The only exception that escapes is
    /// <see cref="OperationCanceledException"/> for the caller's own <paramref name="cancellationToken"/>.
    /// </remarks>
    /// <typeparam name="TMessage">The type to deserialize each message into.</typeparam>
    /// <param name="handleMessage">Called once per message with the deserialized payload and the queue's metadata.</param>
    /// <param name="handleException">Called when receiving, deserializing, handling or deleting a message fails.</param>
    /// <param name="numMessages">How many messages to receive in one call (the service allows at most 32).</param>
    /// <param name="cancellationToken">Stops the receive and any further message processing. Messages still in the batch become visible again after their visibility timeout.</param>
    public async ValueTask ReceiveMessagesAsync<TMessage>(
        Func<TMessage?, IDictionary<string, string>?, ValueTask> handleMessage,
        Func<Exception, IDictionary<string, string>?, ValueTask> handleException,
        int numMessages = 1,
        CancellationToken cancellationToken = default)
        where TMessage : class
    {
        ArgumentNullException.ThrowIfNull(handleMessage);
        ArgumentNullException.ThrowIfNull(handleException);

        using Activity? activity = _telemetrySettings.CreateNewActivityOnMessageRetrieval
            ? QueueServiceDiagnostics.Activities.StartReadingQueueActivity(tags =>
            {
                tags.Add(QueueServiceDiagnostics.Names.QueueName, QueueName);
                tags.Add(QueueServiceDiagnostics.Names.QueueMessageCount, numMessages);
            })
            : null;

        QueueMessage[] receivedMessages;
        try
        {
            await EnsureQueueExistsAsync(cancellationToken);
            receivedMessages = await _queueClient.ReceiveMessagesAsync(numMessages, _visibilityTimeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed read (network, auth, missing queue, bad numMessages) used to escape straight into the
            // caller's polling loop and take the host down. It is now reported like any other failure.
            _logger.LogReceiveFailed(QueueName, ex);
            activity?.AddException(ex);
            await InvokeExceptionHandlerAsync(handleException, ex, metadata: null, messageId: null);
            return;
        }

        if (receivedMessages.Length == 0) return;

        _logger.LogMessagesReceived(QueueName, receivedMessages.Length);
        activity?.AddEvent(new ActivityEvent(QueueServiceDiagnostics.Events.QueueMessageReceived));

        var metadata = await TryGetMetadataAsync(cancellationToken);

        foreach (var queueMessage in receivedMessages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessMessageAsync(queueMessage, metadata, handleMessage, handleException, activity, cancellationToken);
        }
    }

    private async ValueTask ProcessMessageAsync<TMessage>(
        QueueMessage queueMessage,
        IDictionary<string, string>? metadata,
        Func<TMessage?, IDictionary<string, string>?, ValueTask> handleMessage,
        Func<Exception, IDictionary<string, string>?, ValueTask> handleException,
        Activity? activity,
        CancellationToken cancellationToken)
        where TMessage : class
    {
        var tags = new TagList
        {
            new(QueueServiceDiagnostics.Names.QueueName, QueueName),
        };

        QueueServiceDiagnostics.Metrics.MessagesReceived.Add(1, tags);

        var start = Stopwatch.GetTimestamp();
        var handled = false;
        var success = false;

        try
        {
            var convertedMessage = _messageConverter.Convert<TMessage>(queueMessage.MessageText);
            await handleMessage(convertedMessage, metadata);
            handled = true;

            // The handler's work is done, so the delete is deliberately not tied to the caller's token:
            // cancelling it during shutdown would only redeliver a message that was already processed.
            await _queueClient.DeleteMessageAsync(queueMessage.MessageId, queueMessage.PopReceipt, CancellationToken.None);

            _logger.LogMessageProcessed(QueueName, queueMessage.MessageId);
            activity?.AddEvent(new ActivityEvent(QueueServiceDiagnostics.Events.QueueMessageProcessed));
            QueueServiceDiagnostics.Metrics.MessagesProcessed.Add(1, tags);
            success = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (handled)
                _logger.LogDeleteFailed(QueueName, queueMessage.MessageId, _visibilityTimeout ?? DefaultVisibilityTimeout, ex);
            else
                _logger.LogHandlerFailed(QueueName, queueMessage.MessageId, queueMessage.DequeueCount, ex);

            QueueServiceDiagnostics.Metrics.MessageFailures.Add(1, tags);
            activity?.AddException(ex);
            await InvokeExceptionHandlerAsync(handleException, ex, metadata, queueMessage.MessageId);
        }
        finally
        {
            tags.Add(QueueServiceDiagnostics.Names.QueueProcessSuccess, success);
            QueueServiceDiagnostics.Metrics.MessageProcessingDuration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds, tags);
        }
    }

    private async ValueTask InvokeExceptionHandlerAsync(
        Func<Exception, IDictionary<string, string>?, ValueTask> handleException,
        Exception exception,
        IDictionary<string, string>? metadata,
        string? messageId)
    {
        try
        {
            await handleException(exception, metadata);
        }
        catch (Exception handlerException)
        {
            // The caller's exception handler is the last line of defence; if it throws there is nowhere left
            // to report to but the log. Letting it escape would abandon the rest of the batch.
            _logger.LogExceptionHandlerFailed(QueueName, messageId ?? "(none)", handlerException);
        }
    }

    private async ValueTask<IDictionary<string, string>?> TryGetMetadataAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _queueClient.GetPropertiesAsync(cancellationToken);
            return response?.Value?.Metadata;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogMetadataFailed(QueueName, ex);
            return null;
        }
    }

    /// <summary>
    /// Sends a message of a specified type and handles serialization of the message to the correct format.
    /// </summary>
    /// <typeparam name="TMessage"></typeparam>
    /// <param name="message"></param>
    /// <param name="visibilityTimeout"></param>
    /// <param name="timeToLive"></param>
    /// <param name="cancellationToken"></param>
    /// <returns><see cref="SendReceipt"/></returns>
    /// <exception cref="Exception">Serialization and send failures are logged and rethrown.</exception>
    public async ValueTask<SendReceipt> SendMessageAsync<TMessage>(TMessage message, TimeSpan? visibilityTimeout = null, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
    {
        using Activity? activity = _telemetrySettings.CreateNewActivityOnMessageSend
            ? QueueServiceDiagnostics.Activities.StartSendingMessageActivity(tags =>
            {
                tags.Add(QueueServiceDiagnostics.Names.QueueName, QueueName);
            })
            : null;

        try
        {
            await EnsureQueueExistsAsync(cancellationToken);

            BinaryData binaryMessage = _messageConverter.Convert(message);
            SendReceipt response = await _queueClient.SendMessageAsync(binaryMessage, visibilityTimeout, timeToLive, cancellationToken);

            var tags = new TagList
            {
                new(QueueServiceDiagnostics.Names.QueueName, QueueName),
            };
            QueueServiceDiagnostics.Metrics.MessagesSent.Add(1, tags);
            QueueServiceDiagnostics.Metrics.MessageSize.Record(binaryMessage.ToMemory().Length, tags);

            return response;
        }
        catch (Exception e)
        {
            _logger.LogSendFailed(QueueName, e);
            activity?.AddException(e);
            throw;
        }
    }

    // Queue creation used to be a blocking network call made while resolving the client from DI, typically inside
    // a constructor during startup. It now happens on the first operation, once, and asynchronously.
    private async ValueTask EnsureQueueExistsAsync(CancellationToken cancellationToken)
    {
        if (!_createIfNotExists || _queueEnsured) return;

        await _createLock.WaitAsync(cancellationToken);
        try
        {
            if (_queueEnsured) return;
            await _queueClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            _queueEnsured = true;
        }
        finally
        {
            _createLock.Release();
        }
    }

    private void EnsureQueueExists(CancellationToken cancellationToken)
    {
        if (!_createIfNotExists || _queueEnsured) return;

        _createLock.Wait(cancellationToken);
        try
        {
            if (_queueEnsured) return;
            _queueClient.CreateIfNotExists(cancellationToken: cancellationToken);
            _queueEnsured = true;
        }
        finally
        {
            _createLock.Release();
        }
    }
}
