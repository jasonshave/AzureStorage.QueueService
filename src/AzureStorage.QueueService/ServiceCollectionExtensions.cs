using System.Text.Json;
using AzureStorage.QueueService.Telemetry;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorage.QueueService;

public static class ServiceCollectionExtensions
{
    private static readonly QueueClientSettingsRegistry Registry = new();

    public static IServiceCollection AddAzureStorageQueueClient(this IServiceCollection services, Action<QueueClientSettingsBuilder> azureStorageQueueClientBuilderDelegate, JsonSerializerOptions? serializerOptions = null)
    {
        var builder = new QueueClientSettingsBuilder(Registry);
        azureStorageQueueClientBuilderDelegate(builder);

        services.AddSingleton(Registry);
        services.AddSingleton<IQueueClientFactory, QueueClientFactory>();
        services.AddSingleton<IMessageConverter>(new JsonQueueMessageConverter(serializerOptions));

        return services;
    }

    /// <summary>
    /// Adds a strongly-typed queue client for a specific message type.
    /// </summary>
    /// <typeparam name="TMessage">The message type the client handles</typeparam>
    /// <param name="services">The service collection</param>
    /// <param name="clientName">Optional name of the queue client to use. If null, uses the default client.</param>
    /// <returns>The service collection</returns>
    public static IServiceCollection AddTypedQueueClient<TMessage>(
        this IServiceCollection services, 
        string? clientName = null)
        where TMessage : class
    {
        services.AddTransient<ITypedQueueClient<TMessage>>(provider =>
        {
            var factory = provider.GetRequiredService<IQueueClientFactory>();
            var queueClient = string.IsNullOrEmpty(clientName) 
                ? factory.GetQueueClient() 
                : factory.GetQueueClient(clientName);
            
            return new TypedQueueClient<TMessage>(queueClient);
        });

        return services;
    }

    /// <summary>
    /// Adds a custom typed queue client class that will be resolved through DI.
    /// Similar to IHttpClientFactory's typed client pattern where you register custom client types.
    /// </summary>
    /// <typeparam name="TClient">The custom client class</typeparam>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection</returns>
    public static IServiceCollection AddQueueClient<TClient>(this IServiceCollection services)
        where TClient : class
    {
        services.AddTransient<TClient>();
        return services;
    }

    public static IServiceCollection ConfigureQueueServiceTelemetry(this IServiceCollection services, Action<QueueServiceTelemetrySettings> options)
    {
        services.Configure(options);
        return services;
    }
}