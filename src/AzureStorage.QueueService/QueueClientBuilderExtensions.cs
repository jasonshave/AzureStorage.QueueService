using System.Text.Json;
using Azure.Messaging;
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

        builder.Services.TryAddScoped<THandler>();
        return builder.AddHostedProcessor<TMessage, THandler>(typeof(THandler).Name, configure, createHandler: null);
    }

    /// <summary>
    /// Runs a hosted processor for a queue of CloudEvents, such as an Event Grid subscription that delivers to a
    /// storage queue with the CloudEvents schema. Each message is read as a <see cref="CloudEvent"/>, its data is read
    /// as <typeparamref name="TData"/> with this client's serializer, and both go to a new <typeparamref name="THandler"/>
    /// resolved from its own dependency-injection scope. Everything else is <see cref="AddProcessor{TMessage, THandler}"/>:
    /// the same options, settlement, retries, poison queue, lease renewal and shutdown. An event whose data cannot be
    /// read as <typeparamref name="TData"/> counts as an unreadable body (see
    /// <see cref="QueueProcessorOptions.DeadLetterOnDeserializationFailure"/>).
    /// </summary>
    public static IQueueClientBuilder AddCloudEventProcessor<TData, THandler>(this IQueueClientBuilder builder, Action<QueueProcessorOptions>? configure = null)
        where THandler : class, ICloudEventHandler<TData>
    {
        ArgumentNullException.ThrowIfNull(builder);

        var clientName = builder.Name;
        var optionsName = OptionsName<CloudEventHandlerAdapter<TData, THandler>>(builder);

        builder.Services.TryAddScoped<THandler>();
        return builder.AddHostedProcessor<CloudEvent, CloudEventHandlerAdapter<TData, THandler>>(typeof(THandler).Name, configure, provider =>
        {
            // The serializer the client itself uses, so the data is read with the same naming policy as everything
            // else on this queue. Both lookups are cached by the options monitor, so this is cheap per message.
            var serializer = provider.GetRequiredService<IOptionsMonitor<QueueClientSettings>>().Get(clientName).Serializer
                             ?? provider.GetRequiredService<IQueueMessageSerializer>();
            var options = provider.GetRequiredService<IOptionsMonitor<QueueProcessorOptions>>().Get(optionsName);
            return new CloudEventHandlerAdapter<TData, THandler>(provider.GetRequiredService<THandler>(), serializer, options.DeadLetterOnDeserializationFailure);
        });
    }

    // Options are named per client + handler so the same handler type can serve two queues with different settings.
    // A CloudEvent processor is named by its adapter type, so it never shares options with an AddProcessor of the same class.
    private static string OptionsName<THandler>(IQueueClientBuilder builder) => $"{builder.Name}|{typeof(THandler).FullName}";

    private static IQueueClientBuilder AddHostedProcessor<TMessage, THandler>(
        this IQueueClientBuilder builder,
        string handlerName,
        Action<QueueProcessorOptions>? configure,
        Func<IServiceProvider, THandler>? createHandler)
        where THandler : class, IQueueMessageHandler<TMessage>
    {
        var optionsName = OptionsName<THandler>(builder);
        var clientName = builder.Name;

        builder.Services.AddOptions<QueueProcessorOptions>(optionsName).Configure(configure ?? (_ => { }));

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
            provider.GetService<IHostApplicationLifetime>(),
            createHandler,
            handlerName));

        return builder;
    }
}
