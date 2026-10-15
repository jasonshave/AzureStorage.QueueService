using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AzureStorage.QueueService;

public class QueueClientSettingsBuilder
{
    internal IServiceCollection Services { get; }

    internal QueueClientSettingsRegistry Registry { get; }

    internal QueueClientSettingsBuilder(IServiceCollection services, QueueClientSettingsRegistry registry)
    {
        Services = services;
        Registry = registry;
    }

    /// <summary>
    /// Registers a named client. Get it from <see cref="IQueueClientFactory.GetQueueClient(string)"/> or inject it
    /// directly with <c>[FromKeyedServices("name")] AzureStorageQueueClient client</c>. Registering the same name
    /// again adds to its configuration rather than replacing it.
    /// </summary>
    public QueueClientSettingsBuilder AddClient(string clientName, Action<QueueClientSettings> configure)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientName);
        ArgumentNullException.ThrowIfNull(configure);

        Registry.Add(clientName);
        Services.Configure(clientName, configure);
        Services.TryAddKeyedSingleton<AzureStorageQueueClient>(clientName, static (provider, key) =>
            provider.GetRequiredService<IQueueClientFactory>().GetQueueClient((string)key!));
        Services.TryAddKeyedSingleton<IAzureStorageQueueClient>(clientName, static (provider, key) =>
            provider.GetRequiredKeyedService<AzureStorageQueueClient>(key));

        return this;
    }

    /// <summary>
    /// Registers a named client bound from a configuration section, for example
    /// <c>configuration.GetSection("Queues:Orders")</c>.
    /// </summary>
    public QueueClientSettingsBuilder AddClient(string clientName, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return AddClient(clientName, settings => configuration.Bind(settings));
    }

    /// <summary>
    /// Registers the default client. Get it from <see cref="IQueueClientFactory.GetQueueClient()"/> or inject
    /// <see cref="AzureStorageQueueClient"/> (or <see cref="IAzureStorageQueueClient"/>) directly.
    /// </summary>
    public void AddDefaultClient(Action<QueueClientSettings> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        Registry.HasDefaultClient = true;
        Services.Configure(Options.DefaultName, configure);
        Services.TryAddSingleton<AzureStorageQueueClient>(static provider =>
            provider.GetRequiredService<IQueueClientFactory>().GetQueueClient());
        Services.TryAddSingleton<IAzureStorageQueueClient>(static provider =>
            provider.GetRequiredService<AzureStorageQueueClient>());
    }

    /// <summary>
    /// Registers the default client bound from a configuration section.
    /// </summary>
    public void AddDefaultClient(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        AddDefaultClient(settings => configuration.Bind(settings));
    }
}
