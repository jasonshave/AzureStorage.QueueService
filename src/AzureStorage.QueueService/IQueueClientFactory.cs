namespace AzureStorage.QueueService;

public interface IQueueClientFactory
{
    AzureStorageQueueClient GetQueueClient(string clientName);
    AzureStorageQueueClient GetQueueClient();
}