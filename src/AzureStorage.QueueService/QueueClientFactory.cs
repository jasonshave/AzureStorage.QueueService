using AzureStorage.QueueService.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureStorage.QueueService;

internal sealed class QueueClientFactory : IQueueClientFactory
{
    private readonly Dictionary<string, AzureStorageQueueClient> _namedClients = new();
    private AzureStorageQueueClient? _defaultClient;
    private readonly QueueClientSettingsRegistry _registry;
    private readonly IQueueClientBuilder _queueClientBuilder;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IMessageConverter _messageConverter;
    private readonly IOptions<QueueServiceTelemetrySettings> _telemetrySettings;

    public QueueClientFactory(
        QueueClientSettingsRegistry registry, 
        IQueueClientBuilder queueClientBuilder, 
        ILoggerFactory loggerFactory, 
        IMessageConverter messageConverter,
        IOptions<QueueServiceTelemetrySettings> telemetrySettingsOptions)
    {
        _registry = registry;
        _queueClientBuilder = queueClientBuilder;
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
        var queueClient = _queueClientBuilder.CreateClient(settings);
        var azureStorageQueueClient = new AzureStorageQueueClient(_messageConverter, queueClient, _loggerFactory, _telemetrySettings);
        return azureStorageQueueClient;
    }
}