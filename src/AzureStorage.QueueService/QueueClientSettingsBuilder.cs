using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AzureStorage.QueueService;

/// <summary>
/// Registers clients inside <c>AddAzureStorageQueueClient(queues => ...)</c>. Each <c>AddClient</c> /
/// <c>AddDefaultClient</c> returns a builder for that one client to attach a serializer or a processor to.
/// </summary>
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
    /// Registers a named client. Get it from <see cref="IQueueClientFactory.GetQueue(string)"/>, or inject
    /// <see cref="IStorageQueue"/>, <see cref="IQueueSender"/> or <see cref="IQueueReceiver"/> with
    /// <c>[FromKeyedServices("name")]</c>. Registering the same name again adds to its configuration.
    /// </summary>
    public IQueueClientBuilder AddClient(string clientName, Action<QueueClientSettings> configure)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientName);
        ArgumentNullException.ThrowIfNull(configure);

        Registry.Add(clientName);
        Services.Configure(clientName, configure);

        Services.TryAddKeyedSingleton<IStorageQueue>(clientName, static (provider, key) =>
            provider.GetRequiredService<IQueueClientFactory>().GetQueue((string)key!));
        Services.TryAddKeyedSingleton<IQueueSender>(clientName, static (provider, key) =>
            provider.GetRequiredKeyedService<IStorageQueue>(key));
        Services.TryAddKeyedSingleton<IQueueReceiver>(clientName, static (provider, key) =>
            provider.GetRequiredKeyedService<IStorageQueue>(key));

        if (Registry.ValidateOnStart) AddStartupValidation(clientName);

        return new QueueClientBuilder(clientName, Services);
    }

    /// <summary>
    /// Registers a named client bound from a configuration section, for example
    /// <c>configuration.GetSection("Queues:Orders")</c>.
    /// </summary>
    public IQueueClientBuilder AddClient(string clientName, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return AddClient(clientName, settings => configuration.Bind(settings));
    }

    /// <summary>
    /// Registers the default client. Get it from <see cref="IQueueClientFactory.GetQueue()"/>, or inject
    /// <see cref="IStorageQueue"/>, <see cref="IQueueSender"/> or <see cref="IQueueReceiver"/> directly.
    /// </summary>
    public IQueueClientBuilder AddDefaultClient(Action<QueueClientSettings> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        Registry.HasDefaultClient = true;
        Services.Configure(Options.DefaultName, configure);

        Services.TryAddSingleton<IStorageQueue>(static provider => provider.GetRequiredService<IQueueClientFactory>().GetQueue());
        Services.TryAddSingleton<IQueueSender>(static provider => provider.GetRequiredService<IStorageQueue>());
        Services.TryAddSingleton<IQueueReceiver>(static provider => provider.GetRequiredService<IStorageQueue>());

        if (Registry.ValidateOnStart) AddStartupValidation(Options.DefaultName);

        return new QueueClientBuilder(Options.DefaultName, Services);
    }

    /// <summary>
    /// Registers the default client bound from a configuration section.
    /// </summary>
    public IQueueClientBuilder AddDefaultClient(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return AddDefaultClient(settings => configuration.Bind(settings));
    }

    /// <summary>
    /// Checks every registered client's settings when the host starts, so a missing connection string or a bad
    /// poison queue name fails startup with a clear message instead of failing the first request. Applies to
    /// clients registered before and after this call.
    /// </summary>
    public QueueClientSettingsBuilder ValidateOnStart()
    {
        if (Registry.ValidateOnStart) return this;

        Registry.ValidateOnStart = true;
        // TryAddEnumerable, not TryAddSingleton: an app's own validator for these settings must not stop this one being added.
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<QueueClientSettings>, QueueClientSettingsValidator>());
        foreach (var name in Registry.ClientNames) AddStartupValidation(name);
        if (Registry.HasDefaultClient) AddStartupValidation(Options.DefaultName);

        return this;
    }

    private void AddStartupValidation(string clientName) =>
        Services.AddOptions<QueueClientSettings>(clientName).ValidateOnStart();

    private sealed class QueueClientSettingsValidator : IValidateOptions<QueueClientSettings>
    {
        public ValidateOptionsResult Validate(string? name, QueueClientSettings options)
        {
            var error = options.Validate();
            if (error is null) return ValidateOptionsResult.Success;

            var description = string.IsNullOrEmpty(name) ? "The default queue client" : $"Queue client '{name}'";
            return ValidateOptionsResult.Fail($"{description} {error}");
        }
    }
}
