# AzureStorage.QueueService 3.0 — design

Status: **proposal, for review before implementation.** Tracks issue #39.

3.0 changes how the package is consumed. 2.x stays on the `2.x` branch for existing customers and keeps receiving fixes; `master` is 3.0.

## Goals

1. Customers never write a polling loop. The library owns it, including everything people get wrong: backoff, visibility renewal, poison messages, cancellation, shutdown.
2. Sending and receiving are separate things with separate interfaces, and receiving gives the caller the message's identity (id, dequeue count, pop receipt) and explicit control (complete, abandon, dead-letter, renew).
3. Each named client is a handle that extensions can attach to, the way `IHttpClientBuilder` works.
4. Everything 2.x customers call keeps compiling for the whole of 3.x, marked `[Obsolete]` with the replacement named.

Not goals for 3.0: an in-memory test double (3.1), `Microsoft.Extensions.Azure` integration (prototype first; see "Open decisions"), OpenTelemetry semantic-convention rename (3.1).

## Registration

```csharp
services.AddAzureStorageQueueClient(queues =>
{
    queues.AddClient("orders", configuration.GetSection("Queues:Orders"))
          .UseJsonSerializer(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
          .AddProcessor<OrderMessage, OrderHandler>(o =>
          {
              o.MaxConcurrentMessages = 4;
              o.MaxDequeueCount = 5;
          });

    queues.AddDefaultClient(o =>
    {
        o.ConnectionString = configuration["Storage:ConnectionString"];
        o.QueueName = "notifications";
    });
});
```

`AddClient` and `AddDefaultClient` now **return a per-client builder** instead of the parent. This is the one break that cannot be hidden (source-compatible for single-line `x.AddClient(...)` calls, binary-breaking otherwise).

```csharp
public interface IQueueClientBuilder
{
    /// The registered name; Options.DefaultName ("") for the default client.
    string Name { get; }
    IServiceCollection Services { get; }

    IQueueClientBuilder Configure(Action<QueueClientSettings> configure);
    IQueueClientBuilder UseSerializer(IQueueMessageSerializer serializer);
    IQueueClientBuilder UseJsonSerializer(JsonSerializerOptions options);
    IQueueClientBuilder AddProcessor<TMessage, THandler>(Action<QueueProcessorOptions>? configure = null)
        where THandler : class, IQueueMessageHandler<TMessage>;
}
```

Third parties extend this with their own extension methods on `IQueueClientBuilder`, using `Name` and `Services`.

`QueueClientSettings` keeps `ConnectionString`, `QueueName`, `EndpointUri`, `TokenCredential`, `CreateIfNotExists`, `VisibilityTimeout`, `ClientOptions`. `SerializerOptions` moves to `UseJsonSerializer`. `SectionName` is removed (it never matched the README).

Configuration errors throw `InvalidOperationException` (was `ApplicationException`), naming the client and what is missing. `AddAzureStorageQueueClient(...).ValidateOnStart()` is available for fail-fast at host start.

## Consumption

Three ways to consume, from most to least opinionated. None is mandatory.

1. **`AddProcessor<TMessage, THandler>()`** — recommended. The library owns the loop: backoff, lease renewal, poison queue, cancellation, drain. The customer writes a handler class.
2. **Your own `BackgroundService` over `IQueueReceiver`** — for customers who want the loop. `ReceiveAsync<T>` returns messages with their identity and explicit `Complete` / `Abandon` / `DeadLetter` / `RenewLock`; the caller owns backoff, renewal and error handling, and transport failures throw so the caller's loop can react.
3. **The 2.x façade** — `AzureStorageQueueClient.ReceiveMessagesAsync(handle, handleException, …)` keeps working, `[Obsolete]`, for the whole of 3.x.

Three interfaces, all registered per client (keyed by name) and for the default client (unkeyed):

```csharp
public interface IQueueSender
{
    string QueueName { get; }
    ValueTask<SendReceipt> SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default);
}

public interface IQueueReceiver
{
    string QueueName { get; }
    ValueTask<IReadOnlyList<QueueMessage<T>>> ReceiveAsync<T>(int maxMessages = 1, TimeSpan? visibilityTimeout = null, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<QueueMessage<T>>> PeekAsync<T>(int maxMessages = 1, CancellationToken cancellationToken = default);
}

/// Sender + receiver + admin for one queue. What IQueueClientFactory hands out.
public interface IStorageQueue : IQueueSender, IQueueReceiver
{
    ValueTask CreateIfNotExistsAsync(IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default);
    ValueTask ClearAsync(CancellationToken cancellationToken = default);
    ValueTask<QueueProperties> GetPropertiesAsync(CancellationToken cancellationToken = default);
}

public interface IQueueClientFactory
{
    IStorageQueue GetQueue(string clientName);
    IStorageQueue GetQueue();

    [Obsolete("Use GetQueue(). Removed in 4.0.")] AzureStorageQueueClient GetQueueClient(string clientName);
    [Obsolete("Use GetQueue(). Removed in 4.0.")] AzureStorageQueueClient GetQueueClient();
}

public sealed record SendOptions(TimeSpan? VisibilityTimeout = null, TimeSpan? TimeToLive = null);
```

Injection: `IStorageQueue` / `IQueueSender` / `IQueueReceiver` for the default client; `[FromKeyedServices("orders")]` for named ones. A class that only sends depends on `IQueueSender` and is trivially mockable.

### The message

```csharp
public sealed class QueueMessage<T>
{
    public string MessageId { get; }
    public string PopReceipt { get; }          // changes after Renew/Abandon
    public long DequeueCount { get; }
    public DateTimeOffset? InsertedOn { get; }
    public DateTimeOffset? ExpiresOn { get; }
    public DateTimeOffset? NextVisibleOn { get; }
    public BinaryData RawBody { get; }

    /// Null when deserialization failed; see Error.
    public T? Body { get; }
    /// Set when the body could not be deserialized. The message is still returned so the caller can dead-letter it.
    public DeserializationException? Error { get; }

    public ValueTask CompleteAsync(CancellationToken cancellationToken = default);                       // delete
    public ValueTask AbandonAsync(TimeSpan? visibilityTimeout = null, CancellationToken cancellationToken = default); // make visible again (now, or after a delay)
    public ValueTask RenewLockAsync(TimeSpan visibilityTimeout, CancellationToken cancellationToken = default);       // extend the lease; updates PopReceipt
    public ValueTask DeadLetterAsync(string? reason = null, CancellationToken cancellationToken = default);            // copy to the poison queue, then delete
}
```

`ReceiveAsync` never throws for a bad body — it returns the message with `Error` set — so a poison message can be handled instead of pinning the consumer. It does throw for transport failures (`RequestFailedException`) and caller cancellation; the receiver is the low-level API and its caller owns the loop. The processor is where "never throws" lives.

Dead-lettering copies the raw body (plus `MessageId`, `DequeueCount`, `InsertedOn` and `reason` as a small JSON envelope) to `<queue>-poison`, creating it if needed, then deletes the original. This matches what Azure Functions does so existing tooling and expectations carry over.

## The processor

```csharp
public interface IQueueMessageHandler<TMessage>
{
    ValueTask HandleAsync(QueueMessage<TMessage> message, CancellationToken cancellationToken);
}

public sealed class QueueProcessorOptions
{
    public int MaxMessagesPerReceive { get; set; } = 16;          // service max is 32
    public int MaxConcurrentMessages { get; set; } = 1;
    public TimeSpan VisibilityTimeout { get; set; } = TimeSpan.FromMinutes(1);
    public bool RenewVisibility { get; set; } = true;             // renew at half the timeout while the handler runs
    public bool AutoComplete { get; set; } = true;                // complete on return, abandon on throw
    public int MaxDequeueCount { get; set; } = 5;                 // then dead-letter
    public string? PoisonQueueName { get; set; }                  // default "<queue>-poison"
    public bool DeadLetterOnDeserializationFailure { get; set; } = true;
    public Func<long, TimeSpan> RetryDelay { get; set; }          // delay before a failed message is visible again; default 5s * 2^(n-1), capped at 5 min
    public TimeSpan EmptyQueueBackoffMin { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan EmptyQueueBackoffMax { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan ErrorBackoffMax { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
```

Behaviour, per processor registration (one hosted service each):

- **Scope per message.** `THandler` is resolved from a new `IServiceScope` for every message, so scoped services (`DbContext`, request-scoped loggers) work the way they do in a controller.
- **Poison first.** Before the handler sees a message: if `DequeueCount > MaxDequeueCount`, or `Error` is set and `DeadLetterOnDeserializationFailure`, the message is dead-lettered and the handler is not called.
- **Lease renewal.** While the handler runs, the lease is renewed at half `VisibilityTimeout`. A handler that takes ten minutes no longer causes duplicate processing.
- **Completion.** With `AutoComplete`, a handler that returns completes the message; one that throws abandons it with `RetryDelay(DequeueCount)`. A handler may call `CompleteAsync` / `AbandonAsync` / `DeadLetterAsync` itself, and the processor then does nothing further for that message.
- **Nothing escapes.** Transport errors on receive are logged and backed off (exponential to `ErrorBackoffMax`); handler exceptions are logged with message id and dequeue count; an exception from dead-lettering falls back to abandon. The host never stops because of this library.
- **Empty queue.** Polling backs off exponentially from `EmptyQueueBackoffMin` to `EmptyQueueBackoffMax`, resetting on the first non-empty receive.
- **Concurrency.** Up to `MaxConcurrentMessages` handlers in flight, each with its own scope and renewal timer.
- **Lifecycle.** Implements `IHostedLifecycleService`: starts polling after the application has started (so a slow startup does not hold leases), and on shutdown stops receiving, cancels the handlers' token, and waits up to `ShutdownDrainTimeout` for in-flight messages. Messages not completed in time become visible again after their timeout.
- **Telemetry.** A `Consumer` span per message, the existing counters and histogram tagged with queue name and processor outcome (`completed`, `abandoned`, `dead_lettered`).

## Serialization

```csharp
public interface IQueueMessageSerializer
{
    BinaryData Serialize<T>(T value);
    T? Deserialize<T>(BinaryData data);
}
```

Per client, default `System.Text.Json`. Base64 is the SDK's job: set `ClientOptions.MessageEncoding = QueueMessageEncoding.Base64` for queues shared with Azure Functions or older SDKs. The Base64 sniffing heuristic is removed. `IMessageConverter` remains as an `[Obsolete]` adapter.

## Compatibility layer

Kept for the whole of 3.x, all `[Obsolete]` with the replacement named:

- `AzureStorageQueueClient` and `IAzureStorageQueueClient`, implemented over `IStorageQueue`. `ReceiveMessagesAsync(handle, handleException, …)` keeps its 2.1 never-throws behaviour.
- `IQueueClientFactory.GetQueueClient(...)`.
- `IMessageConverter`.
- `QueueClientSettings.SerializerOptions` (forwarded to `UseJsonSerializer`).

Removed in 3.0 (already obsolete or never documented): the synchronous `PeekMessages`, the three old `LoggerMessageExtensions` methods, `MetricExtensions.ProcessWithMetricsAsync`, `QueueClientSettings.SectionName`, `ApplicationException` as a thrown type.

## Open decisions

1. **`Microsoft.Extensions.Azure` underneath.** Gains credentials from configuration (`"Credential": "managedidentity"`), which `TokenCredential` cannot do today. Costs a dependency and their config schema. Decision: prototype on the `v3` branch before the processor work starts; adopt if `AddClient(name, IConfiguration)` can bind both schemas without surprising anyone.
2. **Abandon delay vs. leaving the lease to expire.** Proposed: abandon with an explicit growing delay, so a failing message does not hot-loop and does not wait a full visibility timeout either. Alternative: do nothing and let the lease expire (simpler, but ties retry timing to the lease length).
3. **Package split.** One package, or `AzureStorage.QueueService.Abstractions` (interfaces + `QueueMessage<T>`) so handler libraries do not take the SDK dependency. Proposed: one package for 3.0; split only if asked.

## Work breakdown

In order, each its own pull request to `master`:

1. `IQueueMessageSerializer`, `QueueMessage<T>`, `IQueueSender` / `IQueueReceiver` / `IStorageQueue`, factory returning `IStorageQueue`, keyed registrations; 2.x façade rebuilt on top. All existing tests pass unchanged through the façade.
2. Per-client `IQueueClientBuilder`; `AddClient` / `AddDefaultClient` return it; `ValidateOnStart`; `InvalidOperationException`.
3. The processor: options, handler, hosted service, poison queue, renewal, backoff, drain. Integration tests against Azurite with fake time for the backoff and renewal timers.
4. Removals, README rewrite, migration guide, `3.0.0-preview.1` tag.

Previews publish from `v3.0.0-preview.N` tags with the existing `nuget.yml`; nuget.org marks them pre-release automatically.
