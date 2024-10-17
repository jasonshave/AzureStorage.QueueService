using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorage.QueueService;

public static class ServiceCollectionExtensions
{
    private static QueueClientSettingsRegistry _registry = new();

    public static IServiceCollection AddAzureStorageQueueClient(this IServiceCollection services, Action<QueueClientSettingsBuilder> azureStorageQueueClientBuilderDelegate, JsonSerializerOptions? serializerOptions = null)
    {
        var builder = new QueueClientSettingsBuilder(_registry);
        azureStorageQueueClientBuilderDelegate(builder);

        services.AddSingleton(_registry);
        services.AddSingleton<IQueueClientFactory, QueueClientFactory>();
        services.AddSingleton<IQueueClientBuilder, QueueClientBuilder>();
        services.AddSingleton<IMessageConverter>(new JsonQueueMessageConverter(serializerOptions));

        return services;
    }
}