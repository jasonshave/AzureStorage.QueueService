using Azure.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AzureStorage.QueueService;

/// <summary>
/// The handlers of a routed processor (<c>AddRoutedProcessor</c>): one queue carrying several message types, each read
/// as its own type and handed to its own handler, chosen by the message's <see cref="QueueMessageHeaders.MessageType"/>
/// header. Senders set the header with <see cref="SendOptions.MessageType"/> or <see cref="QueueClientSettings.SendMessageType"/>.
/// </summary>
public sealed class QueueMessageRoutes
{
    private readonly IServiceCollection _services;

    internal QueueMessageRoutes(IServiceCollection services)
    {
        _services = services;
    }

    internal Dictionary<string, IMessageRoute> Routes { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Sends messages whose type header is <paramref name="messageType"/> (by default <typeparamref name="TMessage"/>'s
    /// name, which is what <see cref="QueueClientSettings.SendMessageType"/> sends) to <typeparamref name="THandler"/>.
    /// </summary>
    public QueueMessageRoutes Route<TMessage, THandler>(string? messageType = null)
        where THandler : class, IQueueMessageHandler<TMessage>
    {
        var key = messageType ?? typeof(TMessage).Name;
        if (!Routes.TryAdd(key, new MessageRoute<TMessage, THandler>()))
            throw new InvalidOperationException($"Message type '{key}' is routed twice; each type can have one handler.");

        _services.TryAddScoped<THandler>();
        return this;
    }

    internal static string Describe(IEnumerable<string> keys) => string.Join(", ", keys.Select(k => $"'{k}'"));
}

/// <summary>
/// The handlers of a CloudEvent router (<c>AddCloudEventRouter</c>): one queue of CloudEvents, such as an Event Grid
/// subscription with several event types, each event's data read as its own type and handed to its own handler, chosen
/// by the event's <c>type</c>.
/// </summary>
public sealed class CloudEventRoutes
{
    private readonly IServiceCollection _services;

    internal CloudEventRoutes(IServiceCollection services)
    {
        _services = services;
    }

    internal Dictionary<string, ICloudEventRoute> Routes { get; } = new(StringComparer.Ordinal);

    /// <summary>Sends events whose <c>type</c> is <paramref name="eventType"/> to <typeparamref name="THandler"/>, with their data read as <typeparamref name="TData"/>.</summary>
    public CloudEventRoutes Route<TData, THandler>(string eventType)
        where THandler : class, ICloudEventHandler<TData>
    {
        ArgumentException.ThrowIfNullOrEmpty(eventType);
        if (!Routes.TryAdd(eventType, new CloudEventRoute<TData, THandler>()))
            throw new InvalidOperationException($"CloudEvent type '{eventType}' is routed twice; each type can have one handler.");

        _services.TryAddScoped<THandler>();
        return this;
    }
}

internal interface IMessageRoute
{
    (object? Body, DeserializationException? Error) Read(IQueueMessageSerializer serializer, BinaryData raw);

    ValueTask HandleAsync(QueueMessage<object> message, IServiceProvider services, CancellationToken cancellationToken);
}

internal sealed class MessageRoute<TMessage, THandler> : IMessageRoute
    where THandler : class, IQueueMessageHandler<TMessage>
{
    public (object? Body, DeserializationException? Error) Read(IQueueMessageSerializer serializer, BinaryData raw) =>
        MessageCodec.TryDeserialize<TMessage>(serializer, raw);

    // The handler gets a view typed as its own message, sharing the received message's receipt and settlement.
    public ValueTask HandleAsync(QueueMessage<object> message, IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<THandler>().HandleAsync(message.As(message.Body is TMessage body ? body : default, message.Error), cancellationToken);
}

/// <summary>The handler behind a routed processor: hands each message to the route its read step chose.</summary>
internal sealed class RoutingHandler(IServiceProvider services) : IQueueMessageHandler<object>
{
    public ValueTask HandleAsync(QueueMessage<object> message, CancellationToken cancellationToken) =>
        ((IMessageRoute)message.HandlerData!).HandleAsync(message, services, cancellationToken);

    /// <summary>The read step: choose the route by the type header, and read the body as that route's type.</summary>
    public static void Read(QueueMessage<object> message, IReadOnlyDictionary<string, IMessageRoute> routes, IQueueMessageSerializer serializer)
    {
        if (!message.Headers.TryGetValue(QueueMessageHeaders.MessageType, out var type))
        {
            message.SetBody(null, new UnroutableMessageException(
                $"Message {message.MessageId} has no '{QueueMessageHeaders.MessageType}' header, so the routed processor cannot choose a handler. Send it with SendOptions.MessageType, or set SendMessageType on the sending client."));
            return;
        }

        if (!routes.TryGetValue(type, out var route))
        {
            message.SetBody(null, new UnroutableMessageException(
                $"Message {message.MessageId} has type '{type}', which no handler is routed for (routed: {QueueMessageRoutes.Describe(routes.Keys)})."));
            return;
        }

        message.HandlerData = route;
        var (body, error) = route.Read(serializer, message.RawBody);
        message.SetBody(body, error);
    }
}

internal interface ICloudEventRoute
{
    void ReadData(QueueMessage<CloudEvent> message, IQueueMessageSerializer serializer);

    ValueTask HandleAsync(QueueMessage<CloudEvent> message, object? data, IServiceProvider services, CancellationToken cancellationToken);
}

internal sealed class CloudEventRoute<TData, THandler> : ICloudEventRoute
    where THandler : class, ICloudEventHandler<TData>
{
    public void ReadData(QueueMessage<CloudEvent> message, IQueueMessageSerializer serializer) =>
        CloudEventHandlerAdapter<TData, THandler>.ReadData(message, serializer);

    public ValueTask HandleAsync(QueueMessage<CloudEvent> message, object? data, IServiceProvider services, CancellationToken cancellationToken) =>
        services.GetRequiredService<THandler>().HandleAsync(message, data is TData typed ? typed : default, cancellationToken);
}

/// <summary>The handler behind a CloudEvent router: hands each event to the route its type chose.</summary>
internal sealed class CloudEventRoutingHandler(IServiceProvider services) : IQueueMessageHandler<CloudEvent>
{
    public ValueTask HandleAsync(QueueMessage<CloudEvent> message, CancellationToken cancellationToken)
    {
        var routed = (RoutedCloudEvent)message.HandlerData!;
        return routed.Route.HandleAsync(message, routed.Data, services, cancellationToken);
    }

    /// <summary>The read step after the envelope deserialized: choose the route by the event's type, and read its data.</summary>
    public static void Read(QueueMessage<CloudEvent> message, IReadOnlyDictionary<string, ICloudEventRoute> routes, IQueueMessageSerializer serializer)
    {
        if (message.Body is not { } cloudEvent)
        {
            message.SetBody(null, new DeserializationException("The queue message is JSON null, not a CloudEvent."));
            return;
        }

        if (!routes.TryGetValue(cloudEvent.Type, out var route))
        {
            message.SetBody(cloudEvent, new UnroutableMessageException(
                $"CloudEvent '{cloudEvent.Id}' has type '{cloudEvent.Type}', which no handler is routed for (routed: {QueueMessageRoutes.Describe(routes.Keys)})."));
            return;
        }

        route.ReadData(message, serializer);
        message.HandlerData = new RoutedCloudEvent(route, message.Error is null ? message.HandlerData : null);
    }

    private sealed record RoutedCloudEvent(ICloudEventRoute Route, object? Data);
}
