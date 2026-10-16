using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Azure.Messaging;
using AzureStorage.QueueService.Telemetry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureStorage.QueueService;

/// <summary>What can be attached to one registered client: settings, a serializer, middleware and processors.</summary>
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
    /// Serializes this client's messages with System.Text.Json using <paramref name="options"/>. For trimmed or native
    /// AOT applications, give the options a source-generated <c>TypeInfoResolver</c>, or use the
    /// <see cref="UseJsonSerializer(IQueueClientBuilder, System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver)"/> overload.
    /// </summary>
    public static IQueueClientBuilder UseJsonSerializer(this IQueueClientBuilder builder, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return builder.UseSerializer(new JsonQueueMessageSerializer(options));
    }

    /// <summary>
    /// Serializes this client's messages with System.Text.Json using source-generated metadata, for example
    /// <c>UseJsonSerializer(MyJsonContext.Default)</c>, which is what a trimmed or native AOT application needs. Property
    /// names are read case-insensitively, as with the default serializer.
    /// </summary>
    public static IQueueClientBuilder UseJsonSerializer(this IQueueClientBuilder builder, System.Text.Json.Serialization.Metadata.IJsonTypeInfoResolver typeInfoResolver)
    {
        ArgumentNullException.ThrowIfNull(typeInfoResolver);
        return builder.UseSerializer(new JsonQueueMessageSerializer(new JsonSerializerOptions(JsonQueueMessageSerializer.DefaultOptions) { TypeInfoResolver = typeInfoResolver }));
    }

    /// <summary>
    /// Gzips message bodies of at least <paramref name="thresholdBytes"/> bytes before sending (see
    /// <see cref="QueueClientSettings.CompressionThreshold"/>). Every receiver of the queue must be on 3.1 or later.
    /// </summary>
    public static IQueueClientBuilder UseCompression(this IQueueClientBuilder builder, int thresholdBytes = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(thresholdBytes);
        return builder.Configure(settings => settings.CompressionThreshold = thresholdBytes);
    }

    /// <summary>
    /// Keeps bodies too big for a queue message in <paramref name="store"/>, leaving a reference on the queue (see
    /// <see cref="ILargeMessageStore"/>). With <paramref name="thresholdBytes"/>, bodies at least that big go to the store
    /// even when they would fit.
    /// </summary>
    public static IQueueClientBuilder UseLargeMessageStore(this IQueueClientBuilder builder, ILargeMessageStore store, int? thresholdBytes = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        return builder.Configure(settings =>
        {
            settings.LargeMessageStore = store;
            settings.LargeMessageThreshold = thresholdBytes;
        });
    }

    /// <summary>
    /// As <see cref="UseLargeMessageStore(IQueueClientBuilder, ILargeMessageStore, int?)"/>, with the store built from the
    /// application's services the first time the client is used.
    /// </summary>
    public static IQueueClientBuilder UseLargeMessageStore(this IQueueClientBuilder builder, Func<IServiceProvider, ILargeMessageStore> createStore, int? thresholdBytes = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(createStore);

        builder.Services.AddKeyedSingleton(QueueClientFactory.LargeMessageStoreKey(builder.Name), (provider, _) => createStore(provider));
        return builder.Configure(settings => settings.LargeMessageThreshold = thresholdBytes);
    }

    /// <summary>
    /// Runs <typeparamref name="TMiddleware"/> around every message this client's processors deliver, resolved from the
    /// message's scope (registered as scoped unless you registered it yourself). See <see cref="IQueueMessageMiddleware"/>.
    /// </summary>
    public static IQueueClientBuilder UseMiddleware<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TMiddleware>(this IQueueClientBuilder builder)
        where TMiddleware : class, IQueueMessageMiddleware
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddScoped<TMiddleware>();
        builder.Services.Configure<QueueMiddlewareOptions>(builder.Name, o => o.Middleware.Add(static provider => provider.GetRequiredService<TMiddleware>()));
        return builder;
    }

    /// <summary>Runs <paramref name="middleware"/> around every message this client's processors deliver. See <see cref="IQueueMessageMiddleware"/>.</summary>
    public static IQueueClientBuilder Use(this IQueueClientBuilder builder, Func<QueueMessageContext, QueueMessageDelegate, ValueTask> middleware)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(middleware);

        var instance = new DelegateMiddleware(middleware);
        builder.Services.Configure<QueueMiddlewareOptions>(builder.Name, o => o.Middleware.Add(_ => instance));
        return builder;
    }

    /// <summary>
    /// Runs a hosted processor for this client: the library receives messages of type <typeparamref name="TMessage"/>
    /// and hands each one to a new <typeparamref name="THandler"/> resolved from its own dependency-injection scope.
    /// The processor owns the polling loop, backoff, visibility renewal, retries, the poison queue, cancellation and
    /// shutdown; see <see cref="QueueProcessorOptions"/> for the knobs. A processor whose client is misconfigured fails
    /// host startup on purpose; runtime failures never stop the host. Registering the same handler on the same client
    /// again adds to its options; it does not start a second processor.
    /// </summary>
    public static IQueueClientBuilder AddProcessor<TMessage, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(this IQueueClientBuilder builder, Action<QueueProcessorOptions>? configure = null)
        where THandler : class, IQueueMessageHandler<TMessage>
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddScoped<THandler>();
        return builder.AddHostedProcessor(OptionsName<THandler>(builder), configure, new ProcessorHooks<TMessage, THandler>(HandlerName: typeof(THandler).Name));
    }

    /// <summary>
    /// As <see cref="AddProcessor{TMessage, THandler}(IQueueClientBuilder, Action{QueueProcessorOptions}?)"/>, with the
    /// options bound from <paramref name="configuration"/> (for example <c>configuration.GetSection("Queues:Orders:Processor")</c>)
    /// and then <paramref name="configure"/>, so <c>MaxConcurrentMessages</c> and the rest can differ by environment.
    /// </summary>
    public static IQueueClientBuilder AddProcessor<TMessage, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(this IQueueClientBuilder builder, IConfiguration configuration, Action<QueueProcessorOptions>? configure = null)
        where THandler : class, IQueueMessageHandler<TMessage>
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return builder.AddProcessor<TMessage, THandler>(options =>
        {
            BindProcessorOptions(configuration, options);
            configure?.Invoke(options);
        });
    }

    /// <summary>
    /// Runs a hosted processor whose handler is a delegate, with its parameters filled per message: a parameter of type
    /// <typeparamref name="TMessage"/> gets the body; <see cref="QueueMessage{T}"/> or <see cref="IQueueMessage"/> the
    /// message; <see cref="CancellationToken"/> the shutdown token; anything else is resolved from the message's
    /// dependency-injection scope (<c>[FromKeyedServices]</c> works, and a parameter with a default value is optional).
    /// It must return <see cref="Task"/>, <see cref="ValueTask"/> or nothing. Everything else is
    /// <see cref="AddProcessor{TMessage, THandler}(IQueueClientBuilder, Action{QueueProcessorOptions}?)"/>.
    /// </summary>
    /// <example><code>
    /// queues.AddClient("orders", config.GetSection("Queues:Orders"))
    ///       .AddProcessor&lt;Order&gt;(async (Order order, OrdersDb db, CancellationToken ct) => await db.Fulfil(order, ct));
    /// </code></example>
    public static IQueueClientBuilder AddProcessor<TMessage>(this IQueueClientBuilder builder, Delegate handler, Action<QueueProcessorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(handler);

        var binding = new DelegateHandlerBinding<TMessage>(handler);
        // Each delegate is its own processor, so each gets its own options name.
        var optionsName = $"{builder.Name}|delegate:{ServiceCollectionExtensions.GetOrAddRegistry(builder.Services).NextProcessorNumber()}|{typeof(TMessage).FullName}";
        return builder.AddHostedProcessor(optionsName, configure, new ProcessorHooks<TMessage, DelegateHandler<TMessage>>(
            CreateHandler: provider => new DelegateHandler<TMessage>(binding, provider),
            HandlerName: binding.Name));
    }

    /// <summary>
    /// Runs a hosted processor for a queue of CloudEvents, such as an Event Grid subscription that delivers to a
    /// storage queue with the CloudEvents schema. Each message is read as a <see cref="CloudEvent"/>, its data is read
    /// as <typeparamref name="TData"/> with this client's serializer, and both go to a new <typeparamref name="THandler"/>
    /// resolved from its own dependency-injection scope. Everything else is <see cref="AddProcessor{TMessage, THandler}(IQueueClientBuilder, Action{QueueProcessorOptions}?)"/>:
    /// the same options, settlement, retries, poison queue, lease renewal and shutdown. An event whose data cannot be
    /// read as <typeparamref name="TData"/> counts as an unreadable body (see
    /// <see cref="QueueProcessorOptions.DeadLetterOnDeserializationFailure"/>).
    /// </summary>
    public static IQueueClientBuilder AddCloudEventProcessor<TData, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(this IQueueClientBuilder builder, Action<QueueProcessorOptions>? configure = null)
        where THandler : class, ICloudEventHandler<TData>
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.TryAddScoped<THandler>();
        var serializer = ClientSerializer(builder.Name);
        return builder.AddHostedProcessor(OptionsName<CloudEventHandlerAdapter<TData, THandler>>(builder), configure, provider => new ProcessorHooks<CloudEvent, CloudEventHandlerAdapter<TData, THandler>>(
            CreateHandler: scope => new CloudEventHandlerAdapter<TData, THandler>(scope.GetRequiredService<THandler>()),
            HandlerName: typeof(THandler).Name,
            AfterDeserialize: ReadWithClientSerializer<CloudEvent>(provider, serializer, CloudEventHandlerAdapter<TData, THandler>.ReadData)));
    }

    /// <summary>As <see cref="AddCloudEventProcessor{TData, THandler}(IQueueClientBuilder, Action{QueueProcessorOptions}?)"/>, with the options bound from <paramref name="configuration"/> first.</summary>
    public static IQueueClientBuilder AddCloudEventProcessor<TData, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] THandler>(this IQueueClientBuilder builder, IConfiguration configuration, Action<QueueProcessorOptions>? configure = null)
        where THandler : class, ICloudEventHandler<TData>
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return builder.AddCloudEventProcessor<TData, THandler>(options =>
        {
            BindProcessorOptions(configuration, options);
            configure?.Invoke(options);
        });
    }

    /// <summary>
    /// Runs one hosted processor for a queue that carries several message types, choosing each message's handler by its
    /// <see cref="QueueMessageHeaders.MessageType"/> header (see <see cref="QueueMessageRoutes"/>). A message with no type,
    /// or a type nothing is routed for, is dead-lettered. Two ordinary processors on one queue would take each other's
    /// messages and fail to read them; this is the way to share a queue.
    /// </summary>
    public static IQueueClientBuilder AddRoutedProcessor(this IQueueClientBuilder builder, Action<QueueMessageRoutes> routes, Action<QueueProcessorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(routes);

        var table = new QueueMessageRoutes(builder.Services);
        routes(table);
        if (table.Routes.Count == 0) throw new InvalidOperationException($"{Describe(builder)}: AddRoutedProcessor needs at least one route.");

        var serializer = ClientSerializer(builder.Name);
        return builder.AddHostedProcessor(OptionsName<RoutingHandler>(builder), configure, provider => new ProcessorHooks<object, RoutingHandler>(
            CreateHandler: scope => new RoutingHandler(scope),
            HandlerName: "routed processor",
            ReadBody: ReadWithClientSerializer<object>(provider, serializer, (message, s) => RoutingHandler.Read(message, table.Routes, s))));
    }

    /// <summary>
    /// Runs one hosted processor for a queue of CloudEvents of several types, choosing each event's handler by its
    /// <c>type</c> (see <see cref="CloudEventRoutes"/>). An event of a type nothing is routed for is dead-lettered.
    /// </summary>
    public static IQueueClientBuilder AddCloudEventRouter(this IQueueClientBuilder builder, Action<CloudEventRoutes> routes, Action<QueueProcessorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(routes);

        var table = new CloudEventRoutes(builder.Services);
        routes(table);
        if (table.Routes.Count == 0) throw new InvalidOperationException($"{Describe(builder)}: AddCloudEventRouter needs at least one route.");

        var serializer = ClientSerializer(builder.Name);
        return builder.AddHostedProcessor(OptionsName<CloudEventRoutingHandler>(builder), configure, provider => new ProcessorHooks<CloudEvent, CloudEventRoutingHandler>(
            CreateHandler: scope => new CloudEventRoutingHandler(scope),
            HandlerName: "CloudEvent router",
            AfterDeserialize: ReadWithClientSerializer<CloudEvent>(provider, serializer, (message, s) => CloudEventRoutingHandler.Read(message, table.Routes, s))));
    }

    // Source-generated, so it is trim- and AOT-safe. The generator skips what configuration cannot hold (the callbacks
    // and RateLimiter), as reflection binding would.
    private static void BindProcessorOptions(IConfiguration configuration, QueueProcessorOptions options) => configuration.Bind(options);

    private static string Describe(IQueueClientBuilder builder) => builder.Name.Length == 0 ? "The default queue client" : $"Queue client '{builder.Name}'";

    // Options are named per client + handler so the same handler type can serve two queues with different settings.
    // A CloudEvent processor is named by its adapter type, so it never shares options with an AddProcessor of the same class.
    private static string OptionsName<THandler>(IQueueClientBuilder builder) => $"{builder.Name}|{typeof(THandler).FullName}";

    // The serializer the client itself uses, so a wrapper reads with the same naming policy as everything else on this
    // queue. Looked up on first use rather than now: by then the processor's StartAsync has built and validated the
    // client, so a misconfigured client fails startup there, the way it always has.
    private static Func<IServiceProvider, IQueueMessageSerializer> ClientSerializer(string clientName) => provider =>
        provider.GetRequiredService<IOptionsMonitor<QueueClientSettings>>().Get(clientName).Serializer
        ?? provider.GetRequiredService<IQueueMessageSerializer>();

    private static Action<QueueMessage<T>> ReadWithClientSerializer<T>(IServiceProvider provider, Func<IServiceProvider, IQueueMessageSerializer> serializer, Action<QueueMessage<T>, IQueueMessageSerializer> read)
    {
        var lazy = new Lazy<IQueueMessageSerializer>(() => serializer(provider));
        return message => read(message, lazy.Value);
    }

    private static IQueueClientBuilder AddHostedProcessor<TMessage, THandler>(this IQueueClientBuilder builder, string optionsName, Action<QueueProcessorOptions>? configure, ProcessorHooks<TMessage, THandler> hooks)
        where THandler : class, IQueueMessageHandler<TMessage> =>
        builder.AddHostedProcessor(optionsName, configure, _ => hooks);

    private static IQueueClientBuilder AddHostedProcessor<TMessage, THandler>(this IQueueClientBuilder builder, string optionsName, Action<QueueProcessorOptions>? configure, Func<IServiceProvider, ProcessorHooks<TMessage, THandler>> createHooks)
        where THandler : class, IQueueMessageHandler<TMessage>
    {
        var clientName = builder.Name;
        builder.Services.AddOptions<QueueProcessorOptions>(optionsName).Configure(configure ?? (_ => { }));

        // The same handler on the same client again: its options are merged above, and the one processor runs with them.
        if (!ServiceCollectionExtensions.GetOrAddRegistry(builder.Services).AddProcessor(optionsName)) return builder;

        // Not AddHostedService: that de-duplicates by implementation type, and two processors of the same type for
        // different queues must both run.
        builder.Services.AddSingleton<IHostedService>(provider =>
        {
            var middleware = provider.GetRequiredService<IOptionsMonitor<QueueMiddlewareOptions>>().Get(clientName).Middleware.ToList();
            return new QueueProcessor<TMessage, THandler>(
                clientName,
                optionsName,
                provider.GetRequiredService<IQueueClientFactory>(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IOptionsFactory<QueueProcessorOptions>>(),
                provider.GetRequiredService<ILoggerFactory>(),
                provider.GetRequiredService<QueueServiceMetrics>(),
                provider.GetService<TimeProvider>() ?? TimeProvider.System,
                provider.GetService<IHostApplicationLifetime>(),
                createHooks(provider) with { Middleware = middleware },
                provider.GetService<QueueProcessorController>());
        });

        return builder;
    }
}

/// <summary>Poison queues from the factory, for code that does not inject <see cref="IDeadLetterQueue"/>.</summary>
public static class QueueClientFactoryExtensions
{
    /// <summary>The poison queue of the client registered as <paramref name="clientName"/>.</summary>
    /// <exception cref="InvalidOperationException">No client with that name was registered, or its settings are incomplete.</exception>
    public static IDeadLetterQueue GetDeadLetterQueue(this IQueueClientFactory factory, string clientName)
    {
        ArgumentNullException.ThrowIfNull(clientName);
        return factory is QueueClientFactory library
            ? library.GetDeadLetterQueue(clientName)
            : throw new NotSupportedException("Dead-letter queues come from the library's own IQueueClientFactory.");
    }

    /// <summary>The poison queue of the default client.</summary>
    /// <exception cref="InvalidOperationException">No default client was registered, or its settings are incomplete.</exception>
    public static IDeadLetterQueue GetDeadLetterQueue(this IQueueClientFactory factory) => factory.GetDeadLetterQueue(string.Empty);
}
