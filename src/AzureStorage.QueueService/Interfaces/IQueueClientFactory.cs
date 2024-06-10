using JasonShave.AzureStorage.QueueService.Services;

namespace JasonShave.AzureStorage.QueueService.Interfaces;

public interface IQueueClientFactory
{
    AzureStorageQueueClient GetQueueClient(string clientName);
    AzureStorageQueueClient GetQueueClient();
}