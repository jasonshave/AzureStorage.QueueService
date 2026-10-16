using System.Diagnostics.Metrics;
using System.Text.Json;
using AzureStorage.QueueService.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AzureStorage.QueueService;

/// <summary>Registers the library with a service collection.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the queue client factory and the clients configured in <paramref name="configure"/>.
    /// Can be called more than once; later calls add clients to the same registration.
    /// </summary>
    /// <param name="services">The application's services.</param>
    /// <param name="configure">Registers clients, and settings shared by all of them.</param>
    /// <param name="serializerOptions">
    /// JSON options shared by every client that does not set its own serializer with <c>UseSerializer</c> /
    /// <c>UseJsonSerializer</c>. When omitted, an <see cref="IQueueMessageSerializer"/> you registered yourself is used if
    /// present, and otherwise System.Text.Json's defaults with property names read case-insensitively.
    /// </param>
    public static IServiceCollection AddAzureStorageQueueClient(this IServiceCollection services, Action<QueueClientSettingsBuilder> configure, JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var registry = GetOrAddRegistry(services);

        services.TryAddSingleton<IQueueClientFactory, QueueClientFactory>();
        services.TryAddSingleton<ISdkQueueClientFactory, SdkQueueClientFactory>();
        services.TryAddSingleton<QueueProcessorController>();
        services.TryAddSingleton<IQueueProcessorController>(static provider => provider.GetRequiredService<QueueProcessorController>());
        // Through the host's meter factory when there is one, so the meter is scoped to and disposed with this container.
        services.TryAddSingleton(static provider => QueueServiceMetrics.Create(provider.GetService<IMeterFactory>()));

        if (serializerOptions is not null)
            services.Replace(ServiceDescriptor.Singleton<IQueueMessageSerializer>(new JsonQueueMessageSerializer(serializerOptions)));
        else
            services.TryAddSingleton<IQueueMessageSerializer>(new JsonQueueMessageSerializer());

        // Any client asked for by a name — registered or not — goes through the factory, so a typo or a missing
        // registration fails with the factory's message listing the registered names, not the container's generic one.
        // A registered name also has its own registration (AddClient); this is the fallback for the rest.
        services.TryAddKeyedSingleton<IStorageQueue>(KeyedService.AnyKey, static (provider, key) => provider.GetRequiredService<IQueueClientFactory>().GetQueue(ClientName(key)));
        services.TryAddKeyedSingleton<IQueueSender>(KeyedService.AnyKey, static (provider, key) => provider.GetRequiredKeyedService<IStorageQueue>(key));
        services.TryAddKeyedSingleton<IQueueReceiver>(KeyedService.AnyKey, static (provider, key) => provider.GetRequiredKeyedService<IStorageQueue>(key));
        services.TryAddKeyedSingleton<IDeadLetterQueue>(KeyedService.AnyKey, static (provider, key) => provider.GetRequiredService<IQueueClientFactory>().GetDeadLetterQueue(ClientName(key)));

        // The same for the default client: injecting one when none is registered says so, and names the clients that are.
        services.TryAddSingleton<IStorageQueue>(static provider => provider.GetRequiredService<IQueueClientFactory>().GetQueue());
        services.TryAddSingleton<IQueueSender>(static provider => provider.GetRequiredService<IStorageQueue>());
        services.TryAddSingleton<IQueueReceiver>(static provider => provider.GetRequiredService<IStorageQueue>());
        services.TryAddSingleton<IDeadLetterQueue>(static provider => provider.GetRequiredService<IQueueClientFactory>().GetDeadLetterQueue());

        configure(new QueueClientSettingsBuilder(services, registry));

        return services;
    }

    private static string ClientName(object? key) => key as string
        ?? throw new InvalidOperationException($"Queue clients are keyed by their registered name, a string; [FromKeyedServices] was given {(key is null ? "null" : $"a {key.GetType().Name}")}.");

    /// <summary>
    /// For tests: makes every registered queue client an in-memory queue — default and named, injected or from
    /// <see cref="IQueueClientFactory"/>, and the queue each processor reads — so a handler can be exercised end to
    /// end (send, process, complete, retry, dead-letter) with no storage account or emulator. Can be called before or
    /// after <see cref="AddAzureStorageQueueClient"/>, for example from a test host's service overrides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The queues follow the service's rules: messages are serialized with each client's serializer; a received message
    /// is hidden for its visibility timeout, its dequeue count goes up and it gets a new pop receipt; settling it with an
    /// outdated receipt throws <see cref="Azure.RequestFailedException"/> with status 404; <see cref="SendOptions"/> are
    /// honoured, and so is the 64 KiB size limit. Time comes from the registered <see cref="TimeProvider"/>, or the system
    /// clock, so a fake clock drives visibility, retry delays and expiry. Sends, receives and settlements emit the same
    /// spans and metrics as against the service.
    /// </para>
    /// <para>
    /// Clients that name the same queue share it, as on the service. To see what was dead-lettered, inject
    /// <see cref="IDeadLetterQueue"/> for the client, or register a client whose <see cref="QueueClientSettings.QueueName"/>
    /// is the poison queue's name (the queue name plus <c>-poison</c> unless <see cref="QueueClientSettings.PoisonQueueName"/>
    /// says otherwise) and peek it.
    /// </para>
    /// <para>
    /// Connection details are neither needed nor checked, here or by <see cref="QueueClientSettingsBuilder.ValidateOnStart"/>.
    /// Each client still needs a queue name, and its poison queue name is validated as usual.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddInMemoryQueues(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The factory and the startup validator take the store as an optional dependency: its presence is the switch.
        services.TryAddSingleton(static provider => new InMemoryQueueStore(provider.GetService<TimeProvider>() ?? TimeProvider.System));

        return services;
    }

    /// <summary>Turns the optional spans for individual sends and receives on or off.</summary>
    public static IServiceCollection ConfigureQueueServiceTelemetry(this IServiceCollection services, Action<QueueServiceTelemetrySettings> options)
    {
        services.Configure(options);
        return services;
    }

    // One registry per service collection, so two hosts in one process never share client settings.
    internal static QueueClientSettingsRegistry GetOrAddRegistry(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(QueueClientSettingsRegistry) && descriptor.ImplementationInstance is QueueClientSettingsRegistry existing)
                return existing;
        }

        var registry = new QueueClientSettingsRegistry();
        services.AddSingleton(registry);
        return registry;
    }
}
