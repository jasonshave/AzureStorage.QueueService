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
        services.AddSingleton<IQueueClientBuilder, QueueClientBuilder>();
        services.AddSingleton<IMessageConverter>(new JsonQueueMessageConverter(serializerOptions));

        return services;
    }

    /// <summary>
    /// Adds a strongly-typed queue client using the default queue client configuration.
    /// Similar to IHttpClientFactory's typed client pattern.
    /// </summary>
    /// <typeparam name="TClient">The typed client interface</typeparam>
    /// <typeparam name="TImplementation">The typed client implementation</typeparam>
    /// <typeparam name="TMessage">The message type the client handles</typeparam>
    /// <param name="services">The service collection</param>
    /// <param name="clientName">Optional name of the queue client to use. If null, uses the default client.</param>
    /// <returns>The service collection</returns>
    public static IServiceCollection AddTypedQueueClient<TClient, TImplementation, TMessage>(
        this IServiceCollection services, 
        string? clientName = null)
        where TClient : class, ITypedQueueClient<TMessage>
        where TImplementation : class, TClient
        where TMessage : class
    {
        services.AddTransient<TClient>(provider =>
        {
            var factory = provider.GetRequiredService<IQueueClientFactory>();
            var queueClient = string.IsNullOrEmpty(clientName) 
                ? factory.GetQueueClient() 
                : factory.GetQueueClient(clientName);
            
            var typedClient = new TypedQueueClient<TMessage>(queueClient);
            return (TClient)(object)typedClient;
        });

        return services;
    }

    /// <summary>
    /// Adds a strongly-typed queue client interface with default implementation.
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

    public static IServiceCollection ConfigureQueueServiceTelemetry(this IServiceCollection services, Action<QueueServiceTelemetrySettings> options)
    {
        services.Configure(options);
        return services;
    }
}