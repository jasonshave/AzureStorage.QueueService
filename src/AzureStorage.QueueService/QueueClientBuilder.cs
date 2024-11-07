using Azure.Storage.Queues;

namespace AzureStorage.QueueService;

internal class QueueClientBuilder : IQueueClientBuilder
{
    public QueueClient CreateClient(QueueClientSettings settings)
    {
        var client = Create(settings);

        if (settings.CreateIfNotExists) client.CreateIfNotExists();

        return client;
    }

    public async Task<QueueClient> CreateClientAsync(QueueClientSettings settings)
    {
        var client = Create(settings);

        if (settings.CreateIfNotExists) await client.CreateIfNotExistsAsync();

        return client;
    }

    private static QueueClient Create(QueueClientSettings settings)
    {
        QueueClient? client = default;
        if (settings.TokenCredential is not null && settings.EndpointUri is not null)
            client = new QueueClient(settings.EndpointUri, settings.TokenCredential);

        if (settings.ConnectionString is not null)
            client = new QueueClient(settings.ConnectionString, settings.QueueName);

        if (client is null)
            throw new ApplicationException(
                "There was a problem creating the client. Unable to determine if the endpoint URI or connection string should be used. " +
                "Please be sure to set the connection string or the token credential and endpoint URI together.");

        return client;
    }
}