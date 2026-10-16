using System.Diagnostics.Metrics;
using System.Text.Json;
using AzureStorage.QueueService.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AzureStorage.QueueService;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the queue client factory and the clients configured in <paramref name="configure"/>.
    /// Can be called more than once; later calls add clients to the same registration.
    /// </summary>
    /// <param name="serializerOptions">
    /// JSON options shared by every client that does not set its own serializer with <c>UseSerializer</c> /
    /// <c>UseJsonSerializer</c>. When omitted, an <see cref="IQueueMessageSerializer"/> you registered yourself is used if present.
    /// </param>
    public static IServiceCollection AddAzureStorageQueueClient(this IServiceCollection services, Action<QueueClientSettingsBuilder> configure, JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var registry = GetOrAddRegistry(services);

        services.TryAddSingleton<IQueueClientFactory, QueueClientFactory>();
        services.TryAddSingleton<ISdkQueueClientFactory, SdkQueueClientFactory>();
        // Through the host's meter factory when there is one, so the meter is scoped to and disposed with this container.
        services.TryAddSingleton(static provider => QueueServiceMetrics.Create(provider.GetService<IMeterFactory>()));

        if (serializerOptions is not null)
            services.Replace(ServiceDescriptor.Singleton<IQueueMessageSerializer>(new JsonQueueMessageSerializer(serializerOptions)));
        else
            services.TryAddSingleton<IQueueMessageSerializer>(new JsonQueueMessageSerializer());

        configure(new QueueClientSettingsBuilder(services, registry));

        return services;
    }

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
    /// honoured. Time comes from the registered <see cref="TimeProvider"/>, or the system clock, so a fake clock drives
    /// visibility, retry delays and expiry.
    /// </para>
    /// <para>
    /// Clients that name the same queue share it, as on the service. To see what was dead-lettered, register a client
    /// whose <see cref="QueueClientSettings.QueueName"/> is the poison queue's name (the queue name plus <c>-poison</c>
    /// unless <see cref="QueueClientSettings.PoisonQueueName"/> says otherwise) and peek it.
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

    public static IServiceCollection ConfigureQueueServiceTelemetry(this IServiceCollection services, Action<QueueServiceTelemetrySettings> options)
    {
        services.Configure(options);
        return services;
    }

    // One registry per service collection, so two hosts in one process never share client settings.
    private static QueueClientSettingsRegistry GetOrAddRegistry(IServiceCollection services)
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
