using System.Collections.Concurrent;
using Azure.Storage.Queues;
using AzureStorage.QueueService.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureStorage.QueueService;

internal sealed class QueueClientFactory : IQueueClientFactory
{
    // Lazy<T> so two threads asking for the same client for the first time get one instance, not two.
    private readonly ConcurrentDictionary<string, Lazy<StorageQueue>> _queues = new(StringComparer.Ordinal);
    private readonly QueueClientSettingsRegistry _registry;
    private readonly IOptionsFactory<QueueClientSettings> _settings;
    private readonly ISdkQueueClientFactory _sdkClients;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IQueueMessageSerializer _serializer;
    private readonly QueueServiceTelemetrySettings _telemetry;

    public QueueClientFactory(
        QueueClientSettingsRegistry registry,
        IOptionsFactory<QueueClientSettings> settings,
        ISdkQueueClientFactory sdkClients,
        ILoggerFactory loggerFactory,
        IQueueMessageSerializer serializer,
        IOptions<QueueServiceTelemetrySettings> telemetrySettingsOptions)
    {
        _registry = registry;
        _settings = settings;
        _sdkClients = sdkClients;
        _loggerFactory = loggerFactory;
        _serializer = serializer;
        _telemetry = telemetrySettingsOptions.Value;
    }

    public IStorageQueue GetQueue(string clientName)
    {
        ArgumentNullException.ThrowIfNull(clientName);

        if (!_registry.Contains(clientName))
        {
            var registered = _registry.ClientNames.Count == 0
                ? "none"
                : string.Join(", ", _registry.ClientNames.Select(name => $"'{name}'"));

            throw new InvalidOperationException(
                $"No queue client named '{clientName}' has been registered (registered named clients: {registered}). " +
                $"Register it with services.AddAzureStorageQueueClient(x => x.AddClient(\"{clientName}\", ...)).");
        }

        return GetOrCreate(clientName);
    }

    public IStorageQueue GetQueue()
    {
        if (!_registry.HasDefaultClient)
        {
            throw new InvalidOperationException(
                "No default queue client has been registered. Register one with " +
                "services.AddAzureStorageQueueClient(x => x.AddDefaultClient(...)), or ask for a named client by name.");
        }

        return GetOrCreate(Options.DefaultName);
    }

    private StorageQueue GetOrCreate(string clientName) =>
        _queues.GetOrAdd(clientName, name => new Lazy<StorageQueue>(() => Create(name), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private StorageQueue Create(string clientName)
    {
        // A snapshot: the settings a queue was built with are the settings it keeps.
        var settings = _settings.Create(clientName);
        var description = clientName == Options.DefaultName ? "The default queue client" : $"Queue client '{clientName}'";

        // The same check ValidateOnStart runs, so first use can never fail for something startup accepted.
        if (settings.Validate() is { } error) throw new InvalidOperationException($"{description} {error}");

        var sdkClient = _sdkClients.CreateClient(description, settings);
        var poisonQueueName = settings.EffectivePoisonQueueName!;

        QueueClient PoisonQueue() => _sdkClients.CreateClient($"{description} (poison queue '{poisonQueueName}')", ForSiblingQueue(settings, poisonQueueName));

        return new StorageQueue(
            sdkClient,
            settings.Serializer ?? _serializer,
            _loggerFactory,
            _telemetry,
            settings.VisibilityTimeout,
            settings.CreateIfNotExists,
            PoisonQueue);
    }

    // The same account and credentials, pointed at another queue. A destination only: no poison policy of its own.
    internal static QueueClientSettings ForSiblingQueue(QueueClientSettings settings, string queueName) => new()
    {
        ConnectionString = settings.ConnectionString,
        QueueName = settings.UsesConnectionString ? queueName : null,
        EndpointUri = settings.EndpointUri is null ? null : SiblingQueueUri(settings.EndpointUri, queueName),
        TokenCredential = settings.TokenCredential,
        ClientOptions = settings.ClientOptions,
    };

    /// <summary>Replaces the last path segment of a queue URI, whether or not it ends with a slash.</summary>
    internal static Uri SiblingQueueUri(Uri queueUri, string queueName)
    {
        var builder = new UriBuilder(queueUri);
        var path = builder.Path.TrimEnd('/');
        var lastSlash = path.LastIndexOf('/');
        builder.Path = (lastSlash >= 0 ? path[..(lastSlash + 1)] : "/") + queueName;
        return builder.Uri;
    }
}
