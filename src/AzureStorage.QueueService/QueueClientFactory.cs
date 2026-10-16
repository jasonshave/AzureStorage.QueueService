using System.Collections.Concurrent;
using Azure.Storage.Queues;
using AzureStorage.QueueService.Telemetry;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly InMemoryQueueStore? _inMemory;
    private readonly QueueServiceMetrics _metrics;
    private readonly IServiceProvider? _services;
    private readonly TimeProvider _time;

    /// <param name="inMemory">Registered only by <c>AddInMemoryQueues</c>; when present, every client is an in-memory queue.</param>
    /// <param name="services">For a large message store registered with <c>UseLargeMessageStore(provider => ...)</c>.</param>
    public QueueClientFactory(
        QueueClientSettingsRegistry registry,
        IOptionsFactory<QueueClientSettings> settings,
        ISdkQueueClientFactory sdkClients,
        ILoggerFactory loggerFactory,
        IQueueMessageSerializer serializer,
        IOptions<QueueServiceTelemetrySettings> telemetrySettingsOptions,
        QueueServiceMetrics metrics,
        InMemoryQueueStore? inMemory = null,
        IServiceProvider? services = null,
        TimeProvider? time = null)
    {
        _registry = registry;
        _settings = settings;
        _sdkClients = sdkClients;
        _loggerFactory = loggerFactory;
        _serializer = serializer;
        _telemetry = telemetrySettingsOptions.Value;
        _inMemory = inMemory;
        _metrics = metrics;
        _services = services;
        _time = time ?? TimeProvider.System;
    }

    public IStorageQueue GetQueue(string clientName)
    {
        ArgumentNullException.ThrowIfNull(clientName);

        // The default client asked for by its registered name, the empty string, as the processor controller names it.
        if (clientName.Length == 0) return GetQueue();

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
            var named = _registry.ClientNames.Count == 0 ? "" : $" Registered named clients: {string.Join(", ", _registry.ClientNames.Select(name => $"'{name}'"))}; inject one with [FromKeyedServices(\"name\")].";
            throw new InvalidOperationException(
                "No default queue client has been registered. Register one with " +
                "services.AddAzureStorageQueueClient(x => x.AddDefaultClient(...)), or ask for a named client by name." + named);
        }

        return GetOrCreate(Options.DefaultName);
    }

    /// <summary>The poison queue of the client registered as <paramref name="clientName"/>; empty for the default client.</summary>
    internal IDeadLetterQueue GetDeadLetterQueue(string clientName) =>
        ((StorageQueue)(clientName.Length == 0 ? GetQueue() : GetQueue(clientName))).DeadLetterQueue;

    private StorageQueue GetOrCreate(string clientName) =>
        _queues.GetOrAdd(clientName, name => new Lazy<StorageQueue>(() => Create(name), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private StorageQueue Create(string clientName)
    {
        // A snapshot: the settings a queue was built with are the settings it keeps.
        var settings = _settings.Create(clientName);
        var description = clientName == Options.DefaultName ? "The default queue client" : $"Queue client '{clientName}'";
        var base64 = settings.ClientOptions?.MessageEncoding == QueueMessageEncoding.Base64;

        var codec = new MessageCodec(
            settings.Serializer ?? _serializer,
            settings.PropagateTraceContext,
            settings.CompressionThreshold,
            settings.SendMessageType,
            settings.LargeMessageStore ?? _services?.GetKeyedService<ILargeMessageStore>(LargeMessageStoreKey(clientName)),
            settings.LargeMessageThreshold);

        if (_inMemory is not null)
        {
            // The check ValidateOnStart runs in this mode: the policy without the connection.
            if (settings.ValidateInMemory() is { } problem) throw new InvalidOperationException($"{description} {problem}");

            var queueName = settings.InMemoryQueueName!;
            var poisonQueueName = settings.InMemoryPoisonQueueName!;
            return new StorageQueue(
                new InMemoryQueueTransport(_inMemory.GetQueue(queueName), base64),
                () => new InMemoryQueueTransport(_inMemory.GetQueue(poisonQueueName), base64),
                codec,
                _loggerFactory,
                _telemetry,
                _metrics,
                QueueOptions(settings, _inMemory.Time));
        }

        // The same check ValidateOnStart runs, so first use can never fail for something startup accepted.
        if (settings.Validate() is { } error) throw new InvalidOperationException($"{description} {error}");

        var sdkClient = _sdkClients.CreateClient(description, settings);
        var poisonName = settings.EffectivePoisonQueueName!;

        IQueueTransport PoisonQueue() => new SdkQueueTransport(
            _sdkClients.CreateClient($"{description} (poison queue '{poisonName}')", ForSiblingQueue(settings, poisonName)), base64);

        return new StorageQueue(new SdkQueueTransport(sdkClient, base64), PoisonQueue, codec, _loggerFactory, _telemetry, _metrics, QueueOptions(settings, _time));
    }

    private static StorageQueueOptions QueueOptions(QueueClientSettings settings, TimeProvider time) =>
        new(settings.VisibilityTimeout, settings.CreateIfNotExists, settings.PoisonTimeToLive, time);

    /// <summary>The DI key a client's large message store is registered under by <c>UseLargeMessageStore(provider => ...)</c>.</summary>
    internal static string LargeMessageStoreKey(string clientName) => $"AzureStorage.QueueService.LargeMessageStore:{clientName}";

    // The same account and credentials, pointed at another queue. A destination only: no poison policy of its own.
    internal static QueueClientSettings ForSiblingQueue(QueueClientSettings settings, string queueName) => new()
    {
        ConnectionString = settings.ConnectionString,
        QueueName = settings.UsesConnectionString ? queueName : null,
        EndpointUri = settings.QueueUri is { } queueUri ? SiblingQueueUri(queueUri, queueName) : null,
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
