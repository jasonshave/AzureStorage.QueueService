using JasonShave.AzureStorage.QueueService.Interfaces;
using JasonShave.AzureStorage.QueueService.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JasonShave.AzureStorage.QueueService.Services;

internal sealed class QueueClientFactory : IQueueClientFactory
{
    private readonly Dictionary<string, AzureStorageQueueClient> _namedClients = new();
    private AzureStorageQueueClient? _defaultClient;

    private readonly IServiceProvider _services;
    private readonly QueueClientSettingsRegistry _registry;
    private readonly IQueueClientBuilder _queueClientBuilder;
    
    public QueueClientFactory(IServiceProvider services, QueueClientSettingsRegistry registry, IQueueClientBuilder queueClientBuilder)
    {
        _services = services;
        _registry = registry;
        _queueClientBuilder = queueClientBuilder;
    }

    public AzureStorageQueueClient GetQueueClient(string? clientName)
    {
        if (clientName is null)
        {
            // use default client
            if (_defaultClient is not null) return _defaultClient;
            _defaultClient = Create(_registry.DefaultClientSettings);
            return _defaultClient;
        }

        // try named client
        _namedClients.TryGetValue(clientName, out var azureStorageQueueClient);
        if (azureStorageQueueClient is not null)
        {
            return azureStorageQueueClient;
        }

        // not found so create one and add it
        _registry.ClientSettings.TryGetValue(clientName, out var customClientSettings);
        if (customClientSettings is null)
        {
            throw new ApplicationException("Named client settings not found.");
        }

        var client = Create(customClientSettings);
        _namedClients.TryAdd(clientName, client);

        return client;
    }

    public AzureStorageQueueClient GetQueueClient() => GetQueueClient(null);

    private AzureStorageQueueClient Create(QueueClientSettings settings)
    {
        var messageConverter = _services.GetRequiredService<IMessageConverter>();
        var logger = _services.GetRequiredService<ILogger<AzureStorageQueueClient>>();
        var queueClient = _queueClientBuilder.CreateClient(settings);
        var azureStorageQueueClient = new AzureStorageQueueClient(messageConverter, queueClient, logger);
        return azureStorageQueueClient;
    }
}