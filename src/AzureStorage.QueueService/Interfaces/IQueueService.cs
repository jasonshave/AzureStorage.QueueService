namespace JasonShave.AzureStorage.QueueService.Interfaces;

public interface IQueueService
{
    Task<IEnumerable<TMessage>> PeekMessages<TMessage>(string queueName, int numMessages, CancellationToken cancellationToken = default);

    Task<int> GetMessageCount(string queueName, int numMessages, CancellationToken cancellationToken = default);

    Task ReceiveMessagesAsync<TMessage>(string queueName, Func<TMessage?, Task> handleMessage, Func<Exception, Task> handleException, CancellationToken cancellationToken = default)
        where TMessage : class;
}