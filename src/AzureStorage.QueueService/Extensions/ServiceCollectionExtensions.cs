using Azure.Storage.Queues;
using JasonShave.AzureStorage.QueueService.Converters;
using JasonShave.AzureStorage.QueueService.Interfaces;
using JasonShave.AzureStorage.QueueService.Models;
using JasonShave.AzureStorage.QueueService.Services;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace JasonShave.AzureStorage.QueueService.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAzureStorageQueueServices(this IServiceCollection services, Action<QueueClientSettings> queueClientSettingsDelegate, Action<JsonSerializerOptions> serializationOptions)
    {
        JsonSerializerOptions serializerOptions = new();
        serializationOptions(serializerOptions);

        AddServices(services, queueClientSettingsDelegate, serializerOptions);

        return services;
    }

    public static IServiceCollection AddAzureStorageQueueServices(this IServiceCollection services, Action<QueueClientSettings> queueClientSettingsDelegate)
    {
        JsonSerializerOptions defaultSerializerOptions = new();

        AddServices(services, queueClientSettingsDelegate, defaultSerializerOptions);

        return services;
    }

    private static void AddServices(IServiceCollection services, Action<QueueClientSettings> queueClientSettingsDelegate, JsonSerializerOptions serializerOptions)
    {
        QueueClientSettings queueClientSettings = new();
        queueClientSettingsDelegate(queueClientSettings);
        services.AddSingleton(new QueueClient(queueClientSettings.ConnectionString, queueClientSettings.QueueName));
        services.AddSingleton<IQueueService, AzureStorageQueueService>();
        services.AddSingleton<IMessageConverter>(new JsonQueueMessageConverter(serializerOptions));
    }
}