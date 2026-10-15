using System.Collections.Concurrent;
using AzureStorage.QueueService.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureStorage.QueueService;

internal sealed class QueueClientFactory : IQueueClientFactory
{
    // Lazy<T> so two threads asking for the same client for the first time get one instance, not two.
    private readonly ConcurrentDictionary<string, Lazy<AzureStorageQueueClient>> _clients = new(StringComparer.Ordinal);
    private readonly QueueClientSettingsRegistry _registry;
    private readonly IOptionsMonitor<QueueClientSettings> _settings;
    private readonly IQueueClientBuilder _queueClientBuilder;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IMessageConverter _messageConverter;
    private readonly IOptions<QueueServiceTelemetrySettings> _telemetrySettings;

    public QueueClientFactory(
        QueueClientSettingsRegistry registry,
        IOptionsMonitor<QueueClientSettings> settings,
        IQueueClientBuilder queueClientBuilder,
        ILoggerFactory loggerFactory,
        IMessageConverter messageConverter,
        IOptions<QueueServiceTelemetrySettings> telemetrySettingsOptions)
    {
        _registry = registry;
        _settings = settings;
        _queueClientBuilder = queueClientBuilder;
        _loggerFactory = loggerFactory;
        _messageConverter = messageConverter;
        _telemetrySettings = telemetrySettingsOptions;
    }

    public AzureStorageQueueClient GetQueueClient(string clientName)
    {
        ArgumentNullException.ThrowIfNull(clientName);

        if (!_registry.Contains(clientName))
        {
            var registered = _registry.ClientNames.Count == 0
                ? "none"
                : string.Join(", ", _registry.ClientNames.Select(name => $"'{name}'"));

            throw new ApplicationException(
                $"No queue client named '{clientName}' has been registered (registered named clients: {registered}). " +
                $"Register it with services.AddAzureStorageQueueClient(x => x.AddClient(\"{clientName}\", ...)).");
        }

        return GetOrCreate(clientName);
    }

    public AzureStorageQueueClient GetQueueClient()
    {
        if (!_registry.HasDefaultClient)
        {
            throw new ApplicationException(
                "No default queue client has been registered. Register one with " +
                "services.AddAzureStorageQueueClient(x => x.AddDefaultClient(...)), or ask for a named client with GetQueueClient(name).");
        }

        return GetOrCreate(Options.DefaultName);
    }

    private AzureStorageQueueClient GetOrCreate(string clientName) =>
        _clients.GetOrAdd(clientName, name => new Lazy<AzureStorageQueueClient>(() => Create(name), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private AzureStorageQueueClient Create(string clientName)
    {
        var settings = _settings.Get(clientName);
        var description = clientName == Options.DefaultName ? "The default queue client" : $"Queue client '{clientName}'";
        var queueClient = _queueClientBuilder.CreateClient(description, settings);

        var messageConverter = settings.SerializerOptions is null
            ? _messageConverter
            : new JsonQueueMessageConverter(settings.SerializerOptions);

        return new AzureStorageQueueClient(
            messageConverter,
            queueClient,
            _loggerFactory,
            _telemetrySettings,
            settings.VisibilityTimeout,
            settings.CreateIfNotExists);
    }
}
