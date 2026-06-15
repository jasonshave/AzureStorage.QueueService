using Azure.Storage.Queues;
using AzureStorage.QueueService.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureStorage.QueueService;

internal sealed class QueueClientFactory : IQueueClientFactory
{
    private readonly Dictionary<string, AzureStorageQueueClient> _namedClients = new();
    private AzureStorageQueueClient? _defaultClient;
    private readonly QueueClientSettingsRegistry _registry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IMessageConverter _messageConverter;
    private readonly IOptions<QueueServiceTelemetrySettings> _telemetrySettings;

    public QueueClientFactory(
        QueueClientSettingsRegistry registry, 
        ILoggerFactory loggerFactory, 
        IMessageConverter messageConverter,
        IOptions<QueueServiceTelemetrySettings> telemetrySettingsOptions)
    {
        _registry = registry;
        _loggerFactory = loggerFactory;
        _messageConverter = messageConverter;
        _telemetrySettings = telemetrySettingsOptions;
    }

    public AzureStorageQueueClient GetQueueClient(string clientName)
    {
        // try named client
        _namedClients.TryGetValue(clientName, out var azureStorageQueueClient);
        if (azureStorageQueueClient is not null)
        {
            return azureStorageQueueClient;
        }

        // not found so create one and add it
        _registry.NamedClientsSettings.TryGetValue(clientName, out var customClientSettings);
        if (customClientSettings is null)
        {
            throw new ApplicationException($"Settings for named client, {clientName} not found.");
        }

        var client = Create(customClientSettings);
        _namedClients.TryAdd(clientName, client);

        return client;
    }

    public AzureStorageQueueClient GetQueueClient()
    {
        // use default client
        if (_defaultClient is not null) return _defaultClient;
        _defaultClient = Create(_registry.DefaultClientSettings);
        return _defaultClient;
    }

    private AzureStorageQueueClient Create(QueueClientSettings settings)
    {
        var queueClient = CreateQueueClient(settings);
        var azureStorageQueueClient = new AzureStorageQueueClient(_messageConverter, queueClient, _loggerFactory, _telemetrySettings);
        return azureStorageQueueClient;
    }

    private QueueClient CreateQueueClient(QueueClientSettings settings)
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

        if (settings.CreateIfNotExists) client.CreateIfNotExists();

        return client;
    }
}