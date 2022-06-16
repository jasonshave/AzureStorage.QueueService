using Azure.Storage.Queues.Models;
using JasonShave.AzureStorage.QueueService.Interfaces;
using Microsoft.Extensions.Logging;

namespace JasonShave.AzureStorage.QueueService.Services;

internal class AzureStorageQueueService : IQueueService
{
    private readonly IQueueClientFactory _queueClientFactory;
    private readonly IMessageConverter _queueMessageConverter;
    private readonly ILogger<AzureStorageQueueService> _logger;

    public AzureStorageQueueService(
        IQueueClientFactory queueClientFactory,
        IMessageConverter queueMessageConverter,
        ILogger<AzureStorageQueueService> logger)
    {
        _queueClientFactory = queueClientFactory;
        _queueMessageConverter = queueMessageConverter;
        _logger = logger;
    }

    public async Task<IEnumerable<TMessage>> PeekMessages<TMessage>(string queueName, int numMessages, CancellationToken cancellationToken = default)
    {
        var results = new List<TMessage>();
        PeekedMessage[] messages = await _queueClientFactory.GetQueueClient(queueName).PeekMessagesAsync(numMessages, cancellationToken);
        foreach (var message in messages)
        {
            var convertedMessage = _queueMessageConverter.Convert<TMessage>(message.Body);
            if (convertedMessage is not null) results.Add(convertedMessage);
        }

        return results;
    }

    public async Task<int> GetMessageCount(string queueName, int numMessages, CancellationToken cancellationToken = default)
    {
        var queueClient = _queueClientFactory.GetQueueClient(queueName);
        PeekedMessage[] result = await queueClient.PeekMessagesAsync(numMessages, cancellationToken);
        return result.Count();
    }

    public async Task ReceiveMessagesAsync<TMessage>(string queueName, Func<TMessage?, Task> handleMessage, Func<Exception, Task> handleException, CancellationToken cancellationToken = default)
        where TMessage : class
    {
        var queueClient = _queueClientFactory.GetQueueClient(queueName);
        QueueMessage[] receivedMessages = await queueClient.ReceiveMessagesAsync(cancellationToken);

        if (receivedMessages.Any())
        {
            _logger.LogInformation("Processing {0} storage queue message(s).", receivedMessages.Count());

            List<QueueMessage> messages = receivedMessages.ToList();

            async Task ProcessMessage(QueueMessage queueMessage)
            {
                var convertedMessage = _queueMessageConverter.Convert<TMessage>(queueMessage.Body);
                try
                {
                    await handleMessage(convertedMessage);

                    _logger.LogInformation("Removing queue message id: {0}", queueMessage.MessageId);
                    await queueClient.DeleteMessageAsync(queueMessage.MessageId, queueMessage.PopReceipt, cancellationToken);
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