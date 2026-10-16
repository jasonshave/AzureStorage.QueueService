using System.Diagnostics.CodeAnalysis;
using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AzureStorage.QueueService;

/// <summary>
/// Registers clients inside <c>AddAzureStorageQueueClient(queues => ...)</c>. Each <c>AddClient</c> /
/// <c>AddDefaultClient</c> returns a builder for that one client to attach a serializer or a processor to.
/// </summary>
public class QueueServiceBuilder
{
    internal IServiceCollection Services { get; }

    internal QueueClientSettingsRegistry Registry { get; }

    internal QueueServiceBuilder(IServiceCollection services, QueueClientSettingsRegistry registry)
    {
        Services = services;
        Registry = registry;
    }

    /// <summary>
    /// Registers a named client. Get it from <see cref="IQueueClientFactory.GetQueue(string)"/>, or inject
    /// <see cref="IStorageQueue"/>, <see cref="IQueueSender"/>, <see cref="IQueueReceiver"/> or <see cref="IDeadLetterQueue"/>
    /// with <c>[FromKeyedServices("name")]</c>. Registering the same name again adds to its configuration.
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
        Services.TryAddKeyedSingleton<IDeadLetterQueue>(clientName, static (provider, key) =>
            provider.GetRequiredService<IQueueClientFactory>().GetDeadLetterQueue((string)key!));

        if (Registry.ValidateOnStart) AddStartupValidation(clientName);

        return new QueueClientBuilder(clientName, Services);
    }

    /// <summary>
    /// Registers a named client bound from a configuration section, for example
    /// <c>configuration.GetSection("Queues:Orders")</c>. Every setting that has a value in configuration can be bound,
    /// including <c>"ClientOptions": { "MessageEncoding": "Base64" }</c>; the credential, serializer and large message
    /// store are set in code.
    /// </summary>
    /// <remarks>
    /// Binding uses reflection, which trimming and native AOT cannot see through; in such an application, set the
    /// settings in code with <see cref="AddClient(string, Action{QueueClientSettings})"/> instead.
    /// </remarks>
    [RequiresUnreferencedCode(BindingMessage)]
    [RequiresDynamicCode(BindingMessage)]
    public IQueueClientBuilder AddClient(string clientName, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return AddClient(clientName, settings => Bind(configuration, settings));
    }

    /// <summary>
    /// Registers the default client. Get it from <see cref="IQueueClientFactory.GetQueue()"/>, or inject
    /// <see cref="IStorageQueue"/>, <see cref="IQueueSender"/>, <see cref="IQueueReceiver"/> or <see cref="IDeadLetterQueue"/> directly.
    /// </summary>
    public IQueueClientBuilder AddDefaultClient(Action<QueueClientSettings> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        Registry.HasDefaultClient = true;
        Services.Configure(Options.DefaultName, configure);

        // The unkeyed IStorageQueue, IQueueSender, IQueueReceiver and IDeadLetterQueue are registered by
        // AddAzureStorageQueueClient, default client or not, so that injecting one without a default client says so.

        if (Registry.ValidateOnStart) AddStartupValidation(Options.DefaultName);

        return new QueueClientBuilder(Options.DefaultName, Services);
    }

    /// <summary>
    /// Registers the default client bound from a configuration section. See <see cref="AddClient(string, IConfiguration)"/>.
    /// </summary>
    [RequiresUnreferencedCode(BindingMessage)]
    [RequiresDynamicCode(BindingMessage)]
    public IQueueClientBuilder AddDefaultClient(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return AddDefaultClient(settings => Bind(configuration, settings));
    }

    private const string BindingMessage = "Binds QueueClientSettings from configuration with reflection. In a trimmed or native AOT application, set the settings in code with the Action<QueueClientSettings> overload.";

    // Reflection binding on purpose, not the source generator this project uses for processor options: the generator
    // cannot create or fill QueueClientOptions, so "ClientOptions": { "MessageEncoding": "Base64" } would be silently
    // ignored. Through a delegate, because the generator replaces direct calls to Bind.
    [RequiresUnreferencedCode(BindingMessage)]
    [RequiresDynamicCode(BindingMessage)]
    private static void Bind(IConfiguration configuration, QueueClientSettings settings)
    {
        Action<IConfiguration, object?> bind = ConfigurationBinder.Bind;
        bind(configuration, settings);
    }

    /// <summary>
    /// Checks every registered client's settings when the host starts, so a missing connection string or a bad
    /// poison queue name fails startup with a clear message instead of failing the first request. Applies to
    /// clients registered before and after this call.
    /// </summary>
    public QueueServiceBuilder ValidateOnStart()
    {
        if (Registry.ValidateOnStart) return this;

        Registry.ValidateOnStart = true;
        // TryAddEnumerable, not TryAddSingleton: an app's own validator for these settings must not stop this one being added.
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<QueueClientSettings>, QueueClientSettingsValidator>());
        foreach (var name in Registry.ClientNames) AddStartupValidation(name);
        if (Registry.HasDefaultClient) AddStartupValidation(Options.DefaultName);

        return this;
    }

    /// <summary>
    /// Connects every client that has an <see cref="QueueClientSettings.EndpointUri"/> but no
    /// <see cref="QueueClientSettings.TokenCredential"/> of its own with <paramref name="credential"/>, so configuration
    /// only needs the endpoint, for example <c>UseTokenCredential(new DefaultAzureCredential())</c>. Applies to default
    /// and named clients, registered before and after this call, including those bound from configuration. A client's
    /// own credential wins, and clients that use a connection string are left alone. Calling this again replaces the
    /// credential.
    /// </summary>
    public QueueServiceBuilder UseTokenCredential(TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);

        // The post-configurer reads the credential from the registry when settings are built, so a later call replaces
        // it rather than queueing behind the first.
        Registry.SharedTokenCredential = credential;
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<QueueClientSettings>, SharedAccountPostConfigure>());

        return this;
    }

    /// <summary>
    /// Connects every client that has neither a <see cref="QueueClientSettings.ConnectionString"/> nor an
    /// <see cref="QueueClientSettings.EndpointUri"/> of its own to this storage account, so each client only needs its
    /// <see cref="QueueClientSettings.QueueName"/>. <paramref name="connectionStringOrServiceUri"/> is either a connection
    /// string or the account's queue endpoint (<c>https://account.queue.core.windows.net/</c>), which is what .NET Aspire
    /// puts in <c>ConnectionStrings:&lt;name&gt;</c>: pass <c>builder.Configuration.GetConnectionString("queues")</c>. An
    /// endpoint needs a credential, from <see cref="UseTokenCredential"/> or each client's own. Applies to default and
    /// named clients, registered before and after this call; calling it again replaces the account.
    /// </summary>
    public QueueServiceBuilder UseStorageAccount(string connectionStringOrServiceUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionStringOrServiceUri);

        if (Uri.TryCreate(connectionStringOrServiceUri.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            return UseStorageAccount(uri);

        Registry.SharedConnectionString = connectionStringOrServiceUri;
        Registry.SharedServiceUri = null;
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<QueueClientSettings>, SharedAccountPostConfigure>());
        return this;
    }

    /// <summary>
    /// As <see cref="UseStorageAccount(string)"/>, with the account's queue endpoint
    /// (<c>https://account.queue.core.windows.net/</c>). Clients connect with <see cref="UseTokenCredential"/>'s
    /// credential or their own.
    /// </summary>
    public QueueServiceBuilder UseStorageAccount(Uri serviceUri)
    {
        ArgumentNullException.ThrowIfNull(serviceUri);

        Registry.SharedServiceUri = serviceUri;
        Registry.SharedConnectionString = null;
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<IPostConfigureOptions<QueueClientSettings>, SharedAccountPostConfigure>());
        return this;
    }

    /// <summary>
    /// Runs <typeparamref name="TMiddleware"/> around every message every client's processors deliver, resolved from the
    /// message's scope (registered as scoped unless you registered it yourself). See <see cref="IQueueMessageMiddleware"/>.
    /// </summary>
    public QueueServiceBuilder UseMiddleware<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TMiddleware>()
        where TMiddleware : class, IQueueMessageMiddleware
    {
        Services.TryAddScoped<TMiddleware>();
        Services.ConfigureAll<QueueMiddlewareOptions>(o => o.Middleware.Add(static provider => provider.GetRequiredService<TMiddleware>()));
        return this;
    }

    /// <summary>Runs <paramref name="middleware"/> around every message every client's processors deliver. See <see cref="IQueueMessageMiddleware"/>.</summary>
    public QueueServiceBuilder Use(Func<QueueMessageContext, QueueMessageDelegate, ValueTask> middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        var instance = new DelegateMiddleware(middleware);
        Services.ConfigureAll<QueueMiddlewareOptions>(o => o.Middleware.Add(_ => instance));
        return this;
    }

    /// <summary>
    /// Reports every registered client's queue and poison queue depth as the <c>queue_approximate_messages</c> gauge,
    /// tagged <c>queue.kind</c> <c>main</c> or <c>poison</c>, read every <paramref name="interval"/> (default one minute).
    /// Each read is a billable call per queue, so the interval is a cost. The count is the service's approximate one,
    /// and includes messages that are hidden while being processed or waiting for a retry.
    /// </summary>
    public QueueServiceBuilder AddQueueDepthMetrics(TimeSpan? interval = null)
    {
        var every = interval ?? TimeSpan.FromMinutes(1);
        if (every < TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException(nameof(interval), every, "The depth interval must be at least 1 second.");

        Registry.DepthInterval = every;
        Services.TryAddEnumerable(ServiceDescriptor.Singleton<Microsoft.Extensions.Hosting.IHostedService, QueueDepthMonitor>());
        return this;
    }

    private void AddStartupValidation(string clientName) =>
        Services.AddOptions<QueueClientSettings>(clientName).ValidateOnStart();

    // Post-configure, not configure: it must see each client's finished settings (every Configure call, including
    // ones made after UseStorageAccount or UseTokenCredential) to know whether the client brought its own account and
    // credential, and the options factory runs post-configuration before validation, so a client with only a queue
    // name passes ValidateOnStart and the factory's first-use check alike. One post-configurer for both, so the
    // account is filled in before the credential that depends on it.
    private sealed class SharedAccountPostConfigure(QueueClientSettingsRegistry registry) : IPostConfigureOptions<QueueClientSettings>
    {
        public void PostConfigure(string? name, QueueClientSettings options)
        {
            if (!options.UsesConnectionString && options.EndpointUri is null)
            {
                if (registry.SharedConnectionString is { } connectionString) options.ConnectionString = connectionString;
                else if (registry.SharedServiceUri is { } serviceUri) options.EndpointUri = serviceUri;
            }

            if (options.UsesConnectionString || options.EndpointUri is null) return;
            options.TokenCredential ??= registry.SharedTokenCredential;
        }
    }

    // The in-memory store is registered only by AddInMemoryQueues; its presence means there is no connection to check.
    private sealed class QueueClientSettingsValidator(InMemoryQueueStore? inMemory = null) : IValidateOptions<QueueClientSettings>
    {
        public ValidateOptionsResult Validate(string? name, QueueClientSettings options)
        {
            var error = inMemory is null ? options.Validate() : options.ValidateInMemory();
            if (error is null) return ValidateOptionsResult.Success;

            var description = string.IsNullOrEmpty(name) ? "The default queue client" : $"Queue client '{name}'";
            return ValidateOptionsResult.Fail($"{description} {error}");
        }
    }
}
