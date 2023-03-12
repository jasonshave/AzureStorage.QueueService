using JasonShave.AzureStorage.QueueService.Converters;
using JasonShave.AzureStorage.QueueService.Interfaces;
using JasonShave.AzureStorage.QueueService.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace JasonShave.AzureStorage.QueueService.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAzureStorageQueueClient(this IServiceCollection services, Action<QueueClientSettingsBuilder> azureStorageQueueClientBuilderDelegate, JsonSerializerOptions? serializerOptions = null)
    {
        var builder = new QueueClientSettingsBuilder();
        azureStorageQueueClientBuilderDelegate(builder);

        services.AddSingleton(builder.SettingsRegistry);
        services.AddSingleton<IQueueClientBuilder, QueueClientBuilder>();
        services.AddSingleton<IQueueClientFactory, QueueClientFactory>();
        services.AddSingleton<IMessageConverter>(new JsonQueueMessageConverter(serializerOptions));

        return services;
    }
}