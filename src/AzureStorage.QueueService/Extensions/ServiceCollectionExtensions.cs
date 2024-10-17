using JasonShave.AzureStorage.QueueService.Converters;
using JasonShave.AzureStorage.QueueService.Interfaces;
using JasonShave.AzureStorage.QueueService.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace JasonShave.AzureStorage.QueueService.Extensions;

public static class ServiceCollectionExtensions
{
    private static QueueClientSettingsRegistry _registry = new ();

    public static IServiceCollection AddAzureStorageQueueClient(this IServiceCollection services, Action<QueueClientSettingsBuilder> azureStorageQueueClientBuilderDelegate, JsonSerializerOptions? serializerOptions = null)
    {
        var builder = new QueueClientSettingsBuilder(_registry);
        azureStorageQueueClientBuilderDelegate(builder);

        services.AddSingleton(_registry);
        services.AddSingleton<IQueueClientBuilder, QueueClientBuilder>();
        services.AddSingleton<IQueueClientFactory, QueueClientFactory>();
        services.AddSingleton<IMessageConverter>(new JsonQueueMessageConverter(serializerOptions));

        return services;
    }
}