using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using JasonShave.AzureStorage.QueueService.Interfaces;
using Microsoft.Extensions.Logging;

namespace JasonShave.AzureStorage.QueueService.Services;

internal class AzureStorageQueueService : IQueueService
{
    private readonly QueueClient _queueClient;
    private readonly IMessageConverter _queueMessageConverter;
    private readonly ILogger<AzureStorageQueueService> _logger;

    public AzureStorageQueueService(
        IMessageConverter queueMessageConverter,
        QueueClient queueClient,
        ILogger<AzureStorageQueueService> logger)
    {
        _queueClient = queueClient;
        _queueClient.CreateIfNotExists();
        _queueMessageConverter = queueMessageConverter;
        _logger = logger;
    }

    public async Task<IEnumerable<TMessage>> PeekMessages<TMessage>(int numMessages, CancellationToken cancellationToken = default)
    {
        var results = new List<TMessage>();
        PeekedMessage[] messages = await _queueClient.PeekMessagesAsync(numMessages, cancellationToken);
        foreach (var message in messages)
        {
            var convertedMessage = _queueMessageConverter.Convert<TMessage>(message.MessageText);
            if (convertedMessage is not null) results.Add(convertedMessage);
        }

        return results;
    }

    public async Task ReceiveMessagesAsync<TMessage>(Func<TMessage?, Task> handleMessage, Func<Exception, Task> handleException, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        QueueMessage[] receivedMessages = await _queueClient.ReceiveMessagesAsync(cancellationToken);

        if (receivedMessages.Any())
        {
            _logger.LogInformation("Processing {0} storage queue message(s).", receivedMessages.Length);

            List<QueueMessage> messages = receivedMessages.ToList();

            async Task ProcessMessage(QueueMessage queueMessage)
            {
                var convertedMessage = _queueMessageConverter.Convert<TMessage>(queueMessage.MessageText);
                try
                {
                    await handleMessage(convertedMessage);

                    _logger.LogInformation("Removing queue message id: {0}", queueMessage.MessageId);
                    await _queueClient.DeleteMessageAsync(queueMessage.MessageId, queueMessage.PopReceipt, cancellationToken);
                }
                catch (Exception e)
                {
                    await handleException(e);
                }
            }

            foreach (var queueMessage in messages)
            {
                await ProcessMessage(queueMessage);
            }
        }
    }
}