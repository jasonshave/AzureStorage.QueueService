using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using JasonShave.AzureStorage.QueueService.Extensions;
using JasonShave.AzureStorage.QueueService.Interfaces;
using JasonShave.AzureStorage.QueueService.Models;
using Microsoft.Extensions.Logging;

namespace JasonShave.AzureStorage.QueueService.Services;

public sealed class AzureStorageQueueClient
{
    private readonly IMessageConverter _messageConverter;
    private readonly QueueClient _queueClient;
    private readonly ILogger<AzureStorageQueueClient> _logger;

    internal AzureStorageQueueClient(IMessageConverter messageConverter, QueueClient queueClient, ILogger<AzureStorageQueueClient> logger)
    {
        _messageConverter = messageConverter;
        _queueClient = queueClient;
        _logger = logger;
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
    public async ValueTask ReceiveMessagesAsync<TMessage>(Func<TMessage?, ValueTask> handleMessage, Func<Exception, ValueTask> handleException, CancellationToken cancellationToken = default, int numMessages = 1)
        where TMessage : class
    {
        QueueMessage[] receivedMessages = await _queueClient.ReceiveMessagesAsync(numMessages, null, cancellationToken);

        if (receivedMessages.Any())
        {
            _logger.LogMessageCount(receivedMessages.Length);

            foreach (var queueMessage in receivedMessages)
            {
                await ProcessMessage(queueMessage);
            }

            async Task ProcessMessage(QueueMessage queueMessage)
            {
                try
                {
                    var convertedMessage = _messageConverter.Convert<TMessage>(queueMessage.MessageText);
                    await handleMessage(convertedMessage);

                    _logger.LogProcessedMessage(queueMessage.MessageId);
                    await _queueClient.DeleteMessageAsync(queueMessage.MessageId, queueMessage.PopReceipt, cancellationToken);
                }
                catch (Exception e)
                {
                    await handleException(e);
                }
            }
        }
    }

    /// <summary>
    /// Sends a message of a specified type and handles serialization of the message to the correct format.
    /// </summary>
    /// <typeparam name="TMessage"></typeparam>
    /// <param name="message"></param>
    /// <param name="cancellationToken"></param>
    /// <returns><see cref="SendResponse"/></returns>
    /// <exception cref="Exception"></exception>
    public async ValueTask<SendResponse> SendMessageAsync<TMessage>(TMessage message, CancellationToken cancellationToken = default)
    {
        try
        {
            BinaryData binaryMessage = _messageConverter.Convert(message);
            SendReceipt response = await _queueClient.SendMessageAsync(binaryMessage, null, null, cancellationToken);

            return new SendResponse(response.PopReceipt, response.MessageId);
        }
        catch (Exception e)
        {
            _logger.LogSendError(e.Message);
            throw;
        }
    }
}