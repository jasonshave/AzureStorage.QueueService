namespace AzureStorage.QueueService;

/// <summary>The rest of a processor's pipeline: the next middleware, and finally the handler.</summary>
public delegate ValueTask QueueMessageDelegate(QueueMessageContext context);

/// <summary>
/// Runs around every message a processor delivers, the way ASP.NET Core middleware runs around a request: for
/// idempotency checks, timing, a circuit breaker, a tenant scope or anything else every handler would otherwise repeat.
/// Resolved from the message's own dependency-injection scope, so it can take scoped services.
/// </summary>
/// <remarks>
/// Call <c>next</c> to go on to the handler, or return without calling it to stop there: the processor then
/// treats the message as handled (with <see cref="QueueProcessorOptionsBase.AutoComplete"/> on, it is completed). Throw and
/// the message counts as failed, exactly as if the handler had thrown. A middleware can settle the message itself
/// through <see cref="QueueMessageContext.Message"/>. Register it with <c>UseMiddleware&lt;T&gt;()</c> on one client's
/// builder, or on <c>AddAzureStorageQueueClient(queues => queues.UseMiddleware&lt;T&gt;())</c> for every client; each
/// runs in the order it was registered.
/// </remarks>
public interface IQueueMessageMiddleware
{
    /// <summary>Handles <paramref name="context"/>, usually by doing something before and after <paramref name="next"/>.</summary>
    ValueTask InvokeAsync(QueueMessageContext context, QueueMessageDelegate next);
}

/// <summary>One message's way through a processor's middleware.</summary>
public sealed class QueueMessageContext
{
    internal QueueMessageContext(IQueueMessage message, IServiceProvider services, string clientName, Type messageType, string handlerName, CancellationToken cancellationToken)
    {
        Message = message;
        Services = services;
        ClientName = clientName;
        MessageType = messageType;
        HandlerName = handlerName;
        CancellationToken = cancellationToken;
    }

    /// <summary>The message, with its body already read.</summary>
    public IQueueMessage Message { get; }

    /// <summary>The message's own dependency-injection scope, the one the handler is resolved from.</summary>
    public IServiceProvider Services { get; }

    /// <summary>The client the processor belongs to; empty for the default client.</summary>
    public string ClientName { get; }

    /// <summary>The queue the message came from.</summary>
    public string QueueName => Message.QueueName;

    /// <summary>The type the processor reads bodies as.</summary>
    public Type MessageType { get; }

    /// <summary>The handler's name, as it appears in the processor's logs.</summary>
    public string HandlerName { get; }

    /// <summary>Cancelled when the host shuts down and the drain timeout has passed.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>A place for middleware to leave things for later middleware or the handler's scope to find.</summary>
    public IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
}

/// <summary>The middleware registered for one client (or, under every name, for all of them), in order.</summary>
internal sealed class QueueMiddlewareOptions
{
    public List<Func<IServiceProvider, IQueueMessageMiddleware>> Middleware { get; } = new();
}

/// <summary>A middleware written as a lambda.</summary>
internal sealed class DelegateMiddleware(Func<QueueMessageContext, QueueMessageDelegate, ValueTask> invoke) : IQueueMessageMiddleware
{
    public ValueTask InvokeAsync(QueueMessageContext context, QueueMessageDelegate next) => invoke(context, next);
}
