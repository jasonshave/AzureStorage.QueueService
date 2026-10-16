using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureStorage.QueueService;

public static class QueueClientBuilderExtensions
{
    /// <summary>
    /// Adds to this client's settings. Runs after the delegate passed to <c>AddClient</c>.
    /// </summary>
    public static IQueueClientBuilder Configure(this IQueueClientBuilder builder, Action<QueueClientSettings> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        builder.Services.Configure(builder.Name, configure);
        return builder;
    }

    /// <summary>
    /// Serializes this client's messages with <paramref name="serializer"/> instead of the shared one.
    /// </summary>
    public static IQueueClientBuilder UseSerializer(this IQueueClientBuilder builder, IQueueMessageSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);
        return builder.Configure(settings => settings.Serializer = serializer);
    }

    /// <summary>
    /// Serializes this client's messages with System.Text.Json using <paramref name="options"/>.
    /// </summary>
    public static IQueueClientBuilder UseJsonSerializer(this IQueueClientBuilder builder, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return builder.UseSerializer(new JsonQueueMessageSerializer(options));
    }

    /// <summary>
    /// Runs a hosted processor for this client: the library receives messages of type <typeparamref name="TMessage"/>
    /// and hands each one to a new <typeparamref name="THandler"/> resolved from its own dependency-injection scope.
    /// The processor owns the polling loop, backoff, visibility renewal, retries, the poison queue, cancellation and
    /// shutdown; see <see cref="QueueProcessorOptions"/> for the knobs. A processor whose client is misconfigured fails
    /// host startup on purpose; runtime failures never stop the host.
    /// </summary>
    public static IQueueClientBuilder AddProcessor<TMessage, THandler>(this IQueueClientBuilder builder, Action<QueueProcessorOptions>? configure = null)
        where THandler : class, IQueueMessageHandler<TMessage>
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Options are named per client + handler so the same handler type can serve two queues with different settings.
        var optionsName = $"{builder.Name}|{typeof(THandler).FullName}";
        var clientName = builder.Name;

        builder.Services.AddOptions<QueueProcessorOptions>(optionsName).Configure(configure ?? (_ => { }));
        builder.Services.TryAddScoped<THandler>();

        // Not AddHostedService: that de-duplicates by implementation type, and two processors of the same type for
        // different queues must both run.
        builder.Services.AddSingleton<IHostedService>(provider => new QueueProcessor<TMessage, THandler>(
            clientName,
            optionsName,
            provider.GetRequiredService<IQueueClientFactory>(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptionsFactory<QueueProcessorOptions>>(),
            provider.GetRequiredService<ILoggerFactory>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System,
            provider.GetService<IHostApplicationLifetime>()));

        return builder;
    }
}
