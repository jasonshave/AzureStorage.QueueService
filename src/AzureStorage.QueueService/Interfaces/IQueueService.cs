namespace JasonShave.AzureStorage.QueueService.Interfaces;

public interface IQueueService
{
    Task<IEnumerable<TMessage>> PeekMessages<TMessage>(int numMessages, CancellationToken cancellationToken = default);

    Task<int> GetMessageCount(int numMessages, CancellationToken cancellationToken = default);

    Task ReceiveMessagesAsync<TMessage>(Func<TMessage?, Task> handleMessage, Func<Exception, Task> handleException, CancellationToken cancellationToken = default)
        where TMessage : class;
}