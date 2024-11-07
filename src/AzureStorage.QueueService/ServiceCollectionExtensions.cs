using System.Text.Json;
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
}