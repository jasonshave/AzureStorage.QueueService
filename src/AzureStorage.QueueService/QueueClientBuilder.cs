using Azure.Storage.Queues;

namespace AzureStorage.QueueService;

internal class QueueClientBuilder : IQueueClientBuilder
{
    public QueueClient CreateClient(string clientDescription, QueueClientSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.ConnectionString))
        {
            if (string.IsNullOrEmpty(settings.QueueName))
                throw new ApplicationException($"{clientDescription} has a ConnectionString but no QueueName.");

            return new QueueClient(settings.ConnectionString, settings.QueueName, settings.ClientOptions);
        }

        if (settings.EndpointUri is not null && settings.TokenCredential is not null)
            return new QueueClient(settings.EndpointUri, settings.TokenCredential, settings.ClientOptions);

        throw new ApplicationException(
            $"{clientDescription} is not configured. Set ConnectionString and QueueName, " +
            "or set EndpointUri (including the queue name) and TokenCredential.");
    }
}
