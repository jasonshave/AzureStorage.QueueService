using System.Reflection;
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
    /// Adds the base Azure Storage Queue Client infrastructure with default JSON serialization.
    /// Use this when registering typed clients without needing the full configuration builder.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="serializerOptions">Optional JSON serializer options</param>
    /// <returns>The service collection</returns>
    public static IServiceCollection AddAzureStorageQueueClient(this IServiceCollection services, JsonSerializerOptions? serializerOptions = null)
    {
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
    /// The client type constructor should accept AzureStorageQueueClient as a parameter.
    /// </summary>
    /// <typeparam name="TClient">The custom client class</typeparam>
    /// <param name="services">The service collection</param>
    /// <param name="configureSettings">Action to configure the queue client settings</param>
    /// <returns>The service collection</returns>
    public static IServiceCollection AddQueueClient<TClient>(
        this IServiceCollection services,
        Action<QueueClientSettings> configureSettings)
        where TClient : class
    {
        var clientName = typeof(TClient).Name;
        return AddQueueClient<TClient>(services, clientName, configureSettings);
    }

    /// <summary>
    /// Adds a custom typed queue client class that will be resolved through DI with an explicit client name.
    /// Similar to IHttpClientFactory's typed client pattern where you register custom client types.
    /// The client type constructor should accept AzureStorageQueueClient as a parameter.
    /// </summary>
    /// <typeparam name="TClient">The custom client class</typeparam>
    /// <param name="services">The service collection</param>
    /// <param name="clientName">The name for the queue client configuration</param>
    /// <param name="configureSettings">Action to configure the queue client settings</param>
    /// <returns>The service collection</returns>
    public static IServiceCollection AddQueueClient<TClient>(
        this IServiceCollection services,
        string clientName,
        Action<QueueClientSettings> configureSettings)
        where TClient : class
    {
        // Ensure the base queue client infrastructure is registered
        EnsureBaseServicesRegistered(services);

        // Get or create the registry from the service collection
        var registryDescriptor = services.FirstOrDefault(x => x.ServiceType == typeof(QueueClientSettingsRegistry));
        var registry = registryDescriptor?.ImplementationInstance as QueueClientSettingsRegistry ?? Registry;

        // Configure the queue client settings for this client type
        var builder = new QueueClientSettingsBuilder(registry);
        builder.AddClient(clientName, configureSettings);

        // Register the client type with a factory that provides configured AzureStorageQueueClient
        services.AddTransient<TClient>(provider =>
        {
            var factory = provider.GetRequiredService<IQueueClientFactory>();
            var azureQueueClient = factory.GetQueueClient(clientName);
            
            // Create instance of TClient and inject the configured AzureStorageQueueClient
            return (TClient)Activator.CreateInstance(typeof(TClient), azureQueueClient)!;
        });

        return services;
    }

    /// <summary>
    /// Adds a custom typed queue client class that will be resolved through DI.
    /// This overload is for clients that don't need additional configuration and will use an existing named client.
    /// The client type constructor should accept AzureStorageQueueClient as a parameter.
    /// </summary>
    /// <typeparam name="TClient">The custom client class</typeparam>
    /// <param name="services">The service collection</param>
    /// <param name="clientName">The name of an existing queue client configuration to use</param>
    /// <returns>The service collection</returns>
    public static IServiceCollection AddQueueClient<TClient>(
        this IServiceCollection services,
        string clientName)
        where TClient : class
    {
        services.AddTransient<TClient>(provider =>
        {
            var factory = provider.GetRequiredService<IQueueClientFactory>();
            var azureQueueClient = factory.GetQueueClient(clientName);
            
            // Create instance of TClient and inject the configured AzureStorageQueueClient
            return (TClient)Activator.CreateInstance(typeof(TClient), azureQueueClient)!;
        });

        return services;
    }

    /// <summary>
    /// Adds a custom typed queue client class that will be resolved through DI using the default client.
    /// This method will intelligently inject the appropriate dependencies based on the constructor.
    /// </summary>
    /// <typeparam name="TClient">The custom client class</typeparam>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection</returns>
    public static IServiceCollection AddQueueClient<TClient>(this IServiceCollection services)
        where TClient : class
    {
        // Check if the client type has a constructor that takes AzureStorageQueueClient
        var constructors = typeof(TClient).GetConstructors();
        var hasAzureClientConstructor = constructors.Any(c => 
            c.GetParameters().Length == 1 && 
            c.GetParameters()[0].ParameterType == typeof(AzureStorageQueueClient));

        if (hasAzureClientConstructor)
        {
            // Use factory to create AzureStorageQueueClient
            services.AddTransient<TClient>(provider =>
            {
                var factory = provider.GetRequiredService<IQueueClientFactory>();
                var azureQueueClient = factory.GetQueueClient(); // Use default client
                
                // Create instance of TClient and inject the configured AzureStorageQueueClient
                return (TClient)Activator.CreateInstance(typeof(TClient), azureQueueClient)!;
            });
        }
        else
        {
            // Fall back to normal DI resolution for other constructor patterns
            services.AddTransient<TClient>();
        }

        return services;
    }

    /// <summary>
    /// Ensures the base queue client services are registered if they haven't been already.
    /// </summary>
    private static void EnsureBaseServicesRegistered(IServiceCollection services)
    {
        if (!services.Any(x => x.ServiceType == typeof(QueueClientSettingsRegistry)))
        {
            services.AddSingleton(Registry);
        }
        if (!services.Any(x => x.ServiceType == typeof(IQueueClientFactory)))
        {
            services.AddSingleton<IQueueClientFactory, QueueClientFactory>();
        }
        if (!services.Any(x => x.ServiceType == typeof(IMessageConverter)))
        {
            services.AddSingleton<IMessageConverter>(new JsonQueueMessageConverter());
        }
    }

    public static IServiceCollection ConfigureQueueServiceTelemetry(this IServiceCollection services, Action<QueueServiceTelemetrySettings> options)
    {
        services.Configure(options);
        return services;
    }
}