using System.Text.Json;
using Azure.Storage.Queues.Models;
using JasonShave.AzureStorage.QueueService.Converters;
using JasonShave.AzureStorage.QueueService.Interfaces;
using JasonShave.AzureStorage.QueueService.Models;
using JasonShave.AzureStorage.QueueService.Services;
using Microsoft.Extensions.DependencyInjection;

namespace JasonShave.AzureStorage.QueueService.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAzureStorageQueueServices(this IServiceCollection services, Action<AzureStorageQueueSettings> configurationDelegate, Action<JsonSerializerOptions> serializerDelegate)
    {
        var storageConfigurationOptions = new AzureStorageQueueSettings();
        configurationDelegate(storageConfigurationOptions);

        var serializerOptions = new JsonSerializerOptions();
        serializerDelegate(serializerOptions);

        AddServices(services, storageConfigurationOptions, serializerOptions);
        
        return services;
    }

    public static IServiceCollection AddAzureStorageQueueServices(this IServiceCollection services, Action<AzureStorageQueueSettings> configurationDelegate)
    {
        var storageConfigurationOptions = new AzureStorageQueueSettings();
        configurationDelegate(storageConfigurationOptions);

        var serializerOptions = new JsonSerializerOptions();

        AddServices(services, storageConfigurationOptions, serializerOptions);

        return services;
    }

    private static void AddServices(IServiceCollection services, AzureStorageQueueSettings storageSettingsOptions, JsonSerializerOptions serializerOptions)
    {
        services.AddSingleton<IQueueService, AzureStorageQueueService>();
        services.AddSingleton<IQueueClientFactory, AzureQueueClientFactory>();
        services.AddSingleton<IMessageConverter>(new JsonQueueMessageConverter(serializerOptions));
    }
}