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

        if (serializerOptions is not null)
            services.Replace(ServiceDescriptor.Singleton<IQueueMessageSerializer>(new JsonQueueMessageSerializer(serializerOptions)));
        else
            services.TryAddSingleton<IQueueMessageSerializer>(new JsonQueueMessageSerializer());

        configure(new QueueClientSettingsBuilder(services, registry));

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
