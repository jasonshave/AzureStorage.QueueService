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
    /// Serializer options shared by every client that does not set its own <see cref="QueueClientSettings.SerializerOptions"/>.
    /// When omitted, an <see cref="IMessageConverter"/> you registered yourself is used if present.
    /// </param>
    public static IServiceCollection AddAzureStorageQueueClient(this IServiceCollection services, Action<QueueClientSettingsBuilder> configure, JsonSerializerOptions? serializerOptions = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var registry = GetOrAddRegistry(services);

        services.TryAddSingleton<IQueueClientFactory, QueueClientFactory>();
        services.TryAddSingleton<IQueueClientBuilder, QueueClientBuilder>();

        if (serializerOptions is not null)
            services.Replace(ServiceDescriptor.Singleton<IMessageConverter>(new JsonQueueMessageConverter(serializerOptions)));
        else
            services.TryAddSingleton<IMessageConverter>(new JsonQueueMessageConverter());

        configure(new QueueClientSettingsBuilder(services, registry));

        return services;
    }

    public static IServiceCollection ConfigureQueueServiceTelemetry(this IServiceCollection services, Action<QueueServiceTelemetrySettings> options)
    {
        services.Configure(options);
        return services;
    }

    // One registry per service collection. It used to be a process-wide static, so every host built in the same
    // process (a second host, a test host) silently shared and overwrote one set of client settings.
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
