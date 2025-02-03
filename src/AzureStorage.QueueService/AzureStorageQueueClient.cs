using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using AzureStorage.QueueService.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace AzureStorage.QueueService;

public class AzureStorageQueueClient
{
    private readonly IMessageConverter _messageConverter;
    private readonly QueueClient _queueClient;
    private readonly ILogger<AzureStorageQueueClient> _logger;
    private readonly QueueServiceTelemetrySettings _telemetrySettings;

    internal AzureStorageQueueClient(
        IMessageConverter messageConverter, 
        QueueClient queueClient, 
        ILoggerFactory loggerFactory,
        IOptions<QueueServiceTelemetrySettings> telemetrySettingsOptions)
    {
        _messageConverter = messageConverter;
        _queueClient = queueClient;
        _logger = loggerFactory.CreateLogger<AzureStorageQueueClient>();
        _telemetrySettings = telemetrySettingsOptions.Value;
    }

    public async ValueTask CreateQueueIfNotExistsAsync(IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) =>
        await _queueClient.CreateIfNotExistsAsync(metadata, cancellationToken);

    public async ValueTask ClearMessagesAsync(CancellationToken cancellationToken = default) =>
        await _queueClient.ClearMessagesAsync(cancellationToken);

    public async ValueTask<IEnumerable<TMessage>> PeekMessagesAsync<TMessage>(int numMessages, CancellationToken cancellationToken = default) =>
        (await _queueClient.PeekMessagesAsync(numMessages, cancellationToken)).Value.Convert<TMessage>(_messageConverter);

    public IEnumerable<TMessage> PeekMessages<TMessage>(int numMessages, CancellationToken cancellationToken = default) =>
        _queueClient.PeekMessages(numMessages, cancellationToken).Value.Convert<TMessage>(_messageConverter);

    /// <summary>
    /// Receives a message of the type specified and deserializes the input using a JSON message converter.
    /// The message is processed using a function delegate and errors are processed using an exception handling delegate.
    /// </summary>
    /// <typeparam name="TMessage"></typeparam>
    /// <param name="handleMessage"></param>
    /// <param name="handleException"></param>
    /// <param name="cancellationToken"></param>
    /// <param name="numMessages"></param>
    /// <returns></returns>
    /// <exception cref="Exception"></exception>
    public async ValueTask ReceiveMessagesAsync<TMessage>(Func<TMessage?, IDictionary<string, string>?, ValueTask> handleMessage, Func<Exception, IDictionary<string, string>?, ValueTask> handleException, int numMessages = 1, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        using Activity? activity = _telemetrySettings.CreateNewActivityOnMessageRetrieval
            ? QueueServiceDiagnostics.Activities.StartReadingQueueActivity(tags =>
            {
                tags.Add(QueueServiceDiagnostics.Names.QueueName, _queueClient.Name);
                tags.Add(QueueServiceDiagnostics.Names.QueueMessageCount, numMessages);
            })
            : null;

        QueueMessage[] receivedMessages = await _queueClient.ReceiveMessagesAsync(numMessages, null, cancellationToken);

        if (receivedMessages.Any())
        {
            _logger.LogMessageCount(receivedMessages.Length);
            activity?.AddEvent(QueueServiceDiagnostics.Events.QueueMessageReceived);

            var queueProperties = await _queueClient.GetPropertiesAsync(cancellationToken);

            foreach (var queueMessage in receivedMessages)
            {
                // Tags common to all metrics for this message
                var tagList = new TagList()
                {
                    new(QueueServiceDiagnostics.Names.QueueName, _queueClient.Name),
                };        

                // 1) Increment "messages received" count
                QueueServiceDiagnostics.Metrics.MessagesReceived.Add(1, tagList);

                // 2) Measure how long it takes to process + handle failures
                await QueueServiceDiagnostics.Metrics.MessageProcessingDuration.ProcessWithMetricsAsync(
                    async () =>
                    {
                        var convertedMessage = _messageConverter.Convert<TMessage>(queueMessage.MessageText);
                        await handleMessage(convertedMessage, queueProperties?.Value.Metadata);

                        _logger.LogProcessedMessage(queueMessage.MessageId);
                        await _queueClient.DeleteMessageAsync(queueMessage.MessageId, queueMessage.PopReceipt, cancellationToken);

                        // 3) Add an "message processed" event for your Activity
                        activity?.AddEvent(QueueServiceDiagnostics.Events.QueueMessageProcessed);

                        // 4) Increment "messages processed" count
                        QueueServiceDiagnostics.Metrics.MessagesProcessed.Add(1, tagList);
                    },
                    tagList,
                    onFailure: async ex =>
                    {
                        // Custom error logging / handling
                        activity?.AddException(ex);
                        await handleException(ex, queueProperties?.Value.Metadata);
                    }
                );
            }
        }
    }

    /// <summary>
    /// Sends a message of a specified type and handles serialization of the message to the correct format.
    /// </summary>
    /// <typeparam name="TMessage"></typeparam>
    /// <param name="message"></param>
    /// <param name="cancellationToken"></param>
    /// <returns><see cref="SendReceipt"/></returns>
    /// <exception cref="Exception"></exception>
    public async ValueTask<SendReceipt> SendMessageAsync<TMessage>(TMessage message, TimeSpan? visibilityTimeout = null, TimeSpan? timeToLive = null, CancellationToken cancellationToken = default)
    {
        using Activity? activity = _telemetrySettings.CreateNewActivityOnMessageSend
            ? QueueServiceDiagnostics.Activities.StartSendingMessageActivity(tags =>
            {
                tags.Add(QueueServiceDiagnostics.Names.QueueName, _queueClient.Name);
            })
            : null;

        try
        {
            BinaryData binaryMessage = _messageConverter.Convert(message);
            SendReceipt response = await _queueClient.SendMessageAsync(binaryMessage, visibilityTimeout, timeToLive, cancellationToken);
            QueueServiceDiagnostics.Metrics.MessagesSent.Add(1, new TagList()
            {
                new(QueueServiceDiagnostics.Names.QueueName, _queueClient.Name),
            });

            return response;
        }
        catch (Exception e)
        {
            _logger.LogSendError(e.Message);
            throw;
        }
    }
}