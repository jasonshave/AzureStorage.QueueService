# AzureStorage.QueueService 3.0 — design

Status: **implemented on the `v3` branch** (2026-10-15); this document is the record of the decisions. Tracks issue #39. Where the implementation diverged from the first draft, the text below says so.

3.0 changes how the package is consumed. 2.x stays on the `2.x` branch for existing customers and keeps receiving fixes; `master` is 3.0.

**Target framework.** 3.0 targets `net10.0` only; 2.x stays on `net8.0`. Decided 2026-10-15: .NET 8 leaves support in November 2026, a major version is the right moment to raise the floor, and the 2.x line exists for anyone who cannot move yet. The library gains nothing technically from .NET 10 — this is about support status — so the cost of a clean split is low and the cost of multi-targeting (two builds to keep green for no feature gain) is not worth paying.

## Goals

1. Customers never write a polling loop. The library owns it, including everything people get wrong: backoff, visibility renewal, poison messages, cancellation, shutdown.
2. Sending and receiving are separate things with separate interfaces, and receiving gives the caller the message's identity (id, dequeue count, pop receipt) and explicit control (complete, abandon, dead-letter, renew).
3. Each named client is a handle that extensions can attach to, the way `IHttpClientBuilder` works.
4. ~~Everything 2.x customers call keeps compiling for the whole of 3.x, marked `[Obsolete]`.~~ Dropped on 2026-10-15; 3.0 is a clean break and 2.x stays maintained. See "Compatibility layer".

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
}

// Everything else is an extension method, exactly like IHttpClientBuilder, so other packages extend it the same way.
public static class QueueClientBuilderExtensions
{
    public static IQueueClientBuilder Configure(this IQueueClientBuilder b, Action<QueueClientSettings> configure);
    public static IQueueClientBuilder UseSerializer(this IQueueClientBuilder b, IQueueMessageSerializer serializer);
    public static IQueueClientBuilder UseJsonSerializer(this IQueueClientBuilder b, JsonSerializerOptions options);
    public static IQueueClientBuilder AddProcessor<TMessage, THandler>(this IQueueClientBuilder b, Action<QueueProcessorOptions>? configure = null)
        where THandler : class, IQueueMessageHandler<TMessage>;
}
```

(The first draft put these on the interface; the implementation moved them to extension methods so the interface is the minimal `IHttpClientBuilder` shape.)

`QueueClientSettings` keeps `ConnectionString`, `QueueName`, `EndpointUri`, `TokenCredential`, `CreateIfNotExists`, `VisibilityTimeout`, `ClientOptions`, and gains `Serializer` (set by `UseSerializer` / `UseJsonSerializer`). `SerializerOptions` is obsolete and forwarded. `SectionName` is removed (it never matched the README).

Configuration errors throw `InvalidOperationException` (was `ApplicationException`), naming the client and what is missing. `queues.ValidateOnStart()` on the parent builder (inside the configure delegate) opts into fail-fast at host start; `AddAzureStorageQueueClient` keeps returning `IServiceCollection` so chained calls still compile.

The current internal `IQueueClientBuilder` (which constructs SDK clients from settings) is renamed `ISdkQueueClientFactory` to free the name for the public per-client builder.

## Consumption

Two ways to consume, from most to least opinionated. Neither is mandatory.

1. **`AddProcessor<TMessage, THandler>()`** — recommended. The library owns the loop: backoff, lease renewal, poison queue, cancellation, drain. The customer writes a handler class.
2. **Your own `BackgroundService` over `IQueueReceiver`** — for customers who want the loop. `ReceiveAsync<T>` returns messages with their identity and explicit `Complete` / `Abandon` / `DeadLetter` / `RenewLock`; the caller owns backoff, renewal and error handling, and transport failures throw so the caller's loop can react.

(The first draft had a third path, the 2.x client kept as an obsolete façade; it was dropped — see "Compatibility layer".)

Three interfaces, all registered per client (keyed by name) and for the default client (unkeyed):

```csharp
/// Common base, so IStorageQueue inherits one QueueName rather than an ambiguous pair.
public interface INamedQueue { string QueueName { get; } }

public interface IQueueSender : INamedQueue
{
    ValueTask<SendReceipt> SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default);
}

public interface IQueueReceiver : INamedQueue
{
    /// visibilityTimeout defaults to QueueClientSettings.VisibilityTimeout, then the service default (30 s). maxMessages is 1..32.
    ValueTask<IReadOnlyList<QueueMessage<T>>> ReceiveAsync<T>(int maxMessages = 1, TimeSpan? visibilityTimeout = null, CancellationToken cancellationToken = default);
    /// Peeked messages have no pop receipt, so they get a type with no operations on it.
    ValueTask<IReadOnlyList<PeekedQueueMessage<T>>> PeekAsync<T>(int maxMessages = 1, CancellationToken cancellationToken = default);
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
}

public sealed record SendOptions(TimeSpan? VisibilityTimeout = null, TimeSpan? TimeToLive = null);
```

Injection: `IStorageQueue` / `IQueueSender` / `IQueueReceiver` for the default client; `[FromKeyedServices("orders")]` for named ones. A class that only sends depends on `IQueueSender` and is trivially mockable.

### The message

```csharp
public sealed class PeekedQueueMessage<T>
{
    public string MessageId { get; }
    public long DequeueCount { get; }
    public DateTimeOffset? InsertedOn { get; }
    public DateTimeOffset? ExpiresOn { get; }
    public BinaryData RawBody { get; }
    public T? Body { get; }
    public DeserializationException? Error { get; }
}

public sealed class QueueMessage<T>
{
    /// Public so a test that stubs IQueueReceiver can build messages; the library passes its real operations.
    public QueueMessage(QueueMessageInfo info, BinaryData rawBody, T? body, DeserializationException? error, IQueueMessageOperations operations);

    public string MessageId { get; }
    public string PopReceipt { get; }          // changes after Renew/Abandon
    public long DequeueCount { get; }
    public DateTimeOffset? InsertedOn { get; }
    public DateTimeOffset? ExpiresOn { get; }
    public DateTimeOffset? NextVisibleOn { get; }
    public BinaryData RawBody { get; }

    /// Null when deserialization failed (see Error) or when the body was JSON null.
    public T? Body { get; }
    /// Set when the body could not be deserialized. The message is still returned so the caller can dead-letter it.
    public DeserializationException? Error { get; }
    /// True once Complete, Abandon or DeadLetter has succeeded. Further operations throw InvalidOperationException.
    public bool IsSettled { get; }

    public ValueTask CompleteAsync(CancellationToken cancellationToken = default);                       // delete
    public ValueTask AbandonAsync(TimeSpan? visibilityTimeout = null, CancellationToken cancellationToken = default); // make visible again (now, or after a delay)
    public ValueTask RenewLockAsync(TimeSpan visibilityTimeout, CancellationToken cancellationToken = default);       // extend the lease; updates PopReceipt
    public ValueTask DeadLetterAsync(string? reason = null, CancellationToken cancellationToken = default);            // copy to the poison queue, then delete
}

/// What the library implements over the SDK client; what a test stubs.
public interface IQueueMessageOperations
{
    ValueTask DeleteAsync(string messageId, string popReceipt, CancellationToken cancellationToken);
    ValueTask<UpdateReceipt> UpdateVisibilityAsync(string messageId, string popReceipt, TimeSpan visibilityTimeout, CancellationToken cancellationToken);
    ValueTask DeadLetterAsync(string messageId, string popReceipt, long dequeueCount, BinaryData rawBody, string? reason, CancellationToken cancellationToken);
}
```

Operations on one `QueueMessage<T>` are serialized internally (a renewal timer and a handler calling `CompleteAsync` at the same moment must not race on `PopReceipt`), and every operation uses the latest receipt. After `IsSettled`, further operations throw.

`ReceiveAsync` never throws for a bad body — it returns the message with `Error` set — so a poison message can be handled instead of pinning the consumer. It does throw for transport failures (`RequestFailedException`) and caller cancellation; the receiver is the low-level API and its caller owns the loop. The processor is where "never throws" lives.

Dead-lettering sends the **raw body unchanged** to `<queue>-poison` (creating it if needed), then deletes the original. The reason, message id, dequeue count and insertion time go to the log, not into the message: a poison message can then be moved straight back to the main queue to replay it, and the behaviour matches Azure Functions. Because copy-then-delete is two calls, a lease that expires between them can produce a duplicate in the poison queue; that is the at-least-once trade and is documented. There are no Azure Storage Queue message properties to carry a reason in.

## The processor

```csharp
public interface IQueueMessageHandler<TMessage>
{
    ValueTask HandleAsync(QueueMessage<TMessage> message, CancellationToken cancellationToken);
}

public sealed class QueueProcessorOptions
{
    public int MaxConcurrentMessages { get; set; } = 1;          // workers; each receives one message and delivers it (MaxMessagesPerReceive was dropped with the worker model)
    public TimeSpan VisibilityTimeout { get; set; } = TimeSpan.FromMinutes(1); // the processor's own; QueueClientSettings.VisibilityTimeout applies to direct ReceiveAsync calls only
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

- **Scope per message.** `THandler` is resolved from a new `IServiceScope` for every message, so scoped services (`DbContext`, request-scoped loggers) work the way they do in a controller. `AddProcessor` registers and resolves the concrete `THandler` type, not `IQueueMessageHandler<TMessage>`, so two queues carrying the same `TMessage` can have different handlers.
- **Handler contract.** With `DeadLetterOnDeserializationFailure = false` the handler can receive a message whose `Body` is null and `Error` is set; otherwise `Body` is null only when the message was JSON `null`.
- **Poison first.** Before the handler sees a message: if `DequeueCount > MaxDequeueCount`, or `Error` is set and `DeadLetterOnDeserializationFailure`, the message is dead-lettered and the handler is not called.
- **Lease renewal.** While the handler runs, the lease is renewed at half `VisibilityTimeout`. A handler that takes ten minutes no longer causes duplicate processing. The renewal timer is stopped before the message is settled, so a renew and a complete never race.
- **Completion.** With `AutoComplete`, a handler that returns completes the message; one that throws abandons it with `RetryDelay(DequeueCount)`. A handler may settle the message itself (`IsSettled`), and the processor then does nothing further for it. Completing after a successful handler is **not** tied to the shutdown token (the 2.1 lesson: cancelling it only redelivers work that is already done).
- **Nothing escapes.** Transport errors on receive are logged and backed off (exponential to `ErrorBackoffMax`); a missing queue is logged with "set CreateIfNotExists" and backed off the same way; handler exceptions are logged with message id and dequeue count; an exception from dead-lettering (for example, no permission to create the poison queue) falls back to abandon with the maximum retry delay and an error-level log, so nothing is lost silently. The host never stops because of this library.
- **Empty queue.** Polling backs off exponentially from `EmptyQueueBackoffMin` to `EmptyQueueBackoffMax`, resetting on the first non-empty receive.
- **Concurrency.** Up to `MaxConcurrentMessages` handlers in flight, each with its own scope and renewal timer.
- **Lifecycle.** Implements `IHostedLifecycleService`: `StartAsync` validates options and resolves the queue (a misconfigured client fails startup on purpose); polling starts in `StartedAsync`, after the application is up, so a slow startup does not hold leases. On shutdown it stops receiving, waits up to `ShutdownDrainTimeout` for in-flight handlers to finish on their own, and only then cancels their token (drain, then cancel — the first draft had these the other way round). A cancelled handler's message is abandoned with no delay so another instance can take it. Messages still unsettled become visible again when their lease lapses.
- **Telemetry.** A `Consumer` span per message, the existing counters and histogram tagged with queue name and processor outcome (`completed`, `abandoned`, `dead_lettered`).

## Serialization

```csharp
public interface IQueueMessageSerializer
{
    BinaryData Serialize<T>(T value);
    T? Deserialize<T>(BinaryData data);
}
```

Per client, default `System.Text.Json`. Base64 is the SDK's job: set `ClientOptions.MessageEncoding = QueueMessageEncoding.Base64` for queues shared with Azure Functions or older SDKs. The Base64 sniffing heuristic and `IMessageConverter` are gone. A message the SDK cannot decode is returned with `Error` set (see F2 below) rather than failing the batch.

## Compatibility layer — dropped

The first draft kept the 2.x client as an `[Obsolete]` façade over `IStorageQueue` for the whole of 3.x. **Decided 2026-10-15 to drop it**, after the independent review (issue #39) and the decision to target .NET 10: anyone adopting 3.0 must upgrade their runtime anyway, so compile-time compatibility bought little, while the façade cost a second cache, a second serializer path threaded through the transport, Base64 sniffing kept alive, and ~250 lines of code and tests. Customers who cannot migrate stay on the maintained `2.x` branch. The README carries the 2.x → 3.0 mapping.

Removed in 3.0: `AzureStorageQueueClient`, `IAzureStorageQueueClient`, `IMessageConverter`, `GetQueueClient(...)`, `QueueClientSettings.SerializerOptions`, Base64 sniffing, the synchronous `PeekMessages`, the three old `LoggerMessageExtensions` methods, `MetricExtensions.ProcessWithMetricsAsync`, `QueueClientSettings.SectionName`, `ApplicationException` as a thrown type.

## Self-review (2026-10-15)

Problems found reading the first draft adversarially, all folded in above: peek returned a type with operations that cannot work (now `PeekedQueueMessage<T>`); `QueueMessage<T>` could not be built by a test (public constructor + `IQueueMessageOperations`); renewal and completion could race on `PopReceipt` (serialized operations, `IsSettled`, timer stopped before settling); the processor held leases on messages it was not working on (`MaxMessagesPerReceive` defaults to `MaxConcurrentMessages`); two visibility-timeout settings with no precedence (stated); the dead-letter envelope broke replay (raw body unchanged); handler registration by interface collided across queues (concrete type); `ValidateOnStart` had nowhere to live without a breaking return type (parent builder); the public builder name clashed with an internal type (rename).

## Independent review (2026-10-15) and what changed

An independent review on issue #39 found eight correctness problems in the first implementation, all confirmed and fixed, with a regression test each:

- **F1** The host's shutdown deadline could cancel `StopAsync` before handlers were cancelled, leaving handlers and renewals running. Cleanup is now in a `finally`: however `StopAsync` exits, handler and renewal tokens are cancelled.
- **F2** With `MessageEncoding = Base64`, one undecodable message made the SDK throw for the whole receive. The SDK's `MessageDecodingFailed` event is now captured (via an `AsyncLocal` collector, since the event fires inside the receive call) and such messages come back with `Error` set, identity intact, so they can be dead-lettered. Verified against Azurite.
- **F3** Finishing a handler cancelled a renewal request that might already have been accepted, so completion could use a stale receipt. Only the wait between renewals is cancellable now; an in-flight renewal is always awaited before settling.
- **F4** A handler with a long synchronous prefix delayed dispatch of the rest of a batch. Resolved structurally: the processor is now worker-per-slot, each worker receiving one message and delivering it inline. `MaxMessagesPerReceive` is gone. (The second review found the same gap one step earlier — deserialization ran before renewal was armed — closed as R2 below.)
- **F5** Handler construction, scope disposal and a throwing `RetryDelay` escaped the exception boundary and were discarded unobserved. One delivery routine now owns the whole lifecycle inside a single boundary; a throwing `RetryDelay` falls back to five minutes.
- **F6** `<name>-poison` exceeded Azure's 63-character limit for long queue names, and a trailing slash on the endpoint URI produced `/orders/orders-poison`. `PoisonQueueName` is now a setting, validated at startup and on first use; sibling URIs are built by replacing the last path segment.
- **F7** `GetPropertiesAsync` skipped `CreateIfNotExists`. Fixed.
- **F8** (inherited from 2.x) `TagList` is a struct, so the callback-based activity helper mutated a copy and every `Queue.Read` / `Queue.Send` span was emitted with no tags. Tags are now passed populated; a test with a real `ActivityListener` checks them.

A second review pass found six more, all confirmed and fixed with a regression test each:

- **R1** Each delivery's linked cancellation source was cancelled but never disposed, so the processor-wide renewal token retained one registration per message ever processed. `LeaseRenewal` now has one end of life (`DisposeAsync`) and the processor disposes its own sources. The test proves it with a `WeakReference` after GC, with an undisposed control case.
- **R2** The worker called the typed receive, which deserializes, and only then armed renewal, so a slow serializer could let the lease lapse. The processor now receives undeserialized through an internal `IOwnedReceiver`, arms renewal, then deserializes inside the delivery boundary. The public `ReceiveAsync<T>` is unchanged.
- **R3** At the drain deadline handlers were cancelled but renewal only stopped in the `finally`, so a handler that ignored its token kept its lease alive indefinitely. Renewal now stops at the same moment handlers are cancelled (an in-flight renewal still completes), and an uncooperative handler is waited for one more `ShutdownDrainTimeout`, then abandoned to the lease.
- **R4** Durations beyond what the timer APIs accept (~49 days) passed validation and faulted a worker unobserved. Validated at startup, including the 7-day service ceiling on `VisibilityTimeout`; a worker that fails unexpectedly is logged at Critical and restarted.
- **R5** `PoisonQueueName` equal to the source queue made dead-lettering re-enqueue the message with a fresh dequeue count, for ever. Rejected for both connection styles.
- **R6** `ValidateOnStart` checked only an explicit `PoisonQueueName`, not the derived default. Derivation and validation now live in one place (`QueueClientSettings.Validate`) used by both startup validation and construction.

A third pass found four, three of them regressions from the R-fixes — the lesson being that validation has a scope, and reusing it across scopes is how it goes wrong:

- **S1** Source-queue validation was reused when building the *poison* client, so it demanded a poison queue for the poison queue and failed for source names of 50–56 characters. Split into `ValidateConnection()` (what any SDK client needs; used for every client) and `Validate()` (connection plus source policy; used for sources and by `ValidateOnStart`). Regression-tested with the real SDK factory, since the mock had hidden it, and end-to-end on Azurite.
- **S2** The effective queue name preferred `QueueName` even under endpoint authentication, where the SDK ignores it, so a stale alias could hide a self-poison configuration. The effective name now follows construction precedence exactly, and a `QueueName` that disagrees with the endpoint's queue is rejected.
- **S3** The processor's `VisibilityTimeout` lower bound was checked against zero while the message said one second; 500 ms passed and became `visibilitytimeout=0`, a permanently failing receive. Now 1 second to 7 days, on both the processor options and the client setting.
- **S4** Workers started in the processor's `StartedAsync`, but a *later* service's `StartedAsync` can still fail, after which the host disposes everything without `StopAsync` — and `Dispose` disposed the token sources without cancelling them, so already-running workers kept polling. Workers now start on `IHostApplicationLifetime.ApplicationStarted` (never fired by a failed startup), and `Dispose` cancels before it disposes. Tested through a real Generic Host.

Simplifications taken from the same review: `MessageSettlement` enum instead of a bare `IsSettled`, so a handler that abandons or dead-letters is not counted as a success; our own `VisibilityReceipt` record instead of the SDK's `UpdateReceipt` in `IQueueMessageOperations`; options read once through `IOptionsFactory` (a snapshot) instead of `IOptionsMonitor`; the façade dropped (above). Not taken: making keyed DI own queue lifetimes (the factory stays the single source of truth and works without a container), and a disposition-returning handler API (a different design from the Service Bus-style one agreed; revisit after real use).

## Open decisions

1. **`Microsoft.Extensions.Azure` underneath.** Gains credentials from configuration (`"Credential": "managedidentity"`), which `TokenCredential` cannot do today. Costs a dependency and their config schema. Decision: prototype on the `v3` branch before the processor work starts; adopt if `AddClient(name, IConfiguration)` can bind both schemas without surprising anyone.
2. **Abandon delay vs. leaving the lease to expire.** Proposed: abandon with an explicit growing delay, so a failing message does not hot-loop and does not wait a full visibility timeout either. Alternative: do nothing and let the lease expire (simpler, but ties retry timing to the lease length).
3. **Package split.** One package, or `AzureStorage.QueueService.Abstractions` (interfaces + `QueueMessage<T>`) so handler libraries do not take the SDK dependency. Proposed: one package for 3.0; split only if asked.

## Work breakdown

Planned as four pull requests; delivered as one (#42) at the owner's request. The slices, for orientation in the diff:

1. `IQueueMessageSerializer`, `QueueMessage<T>`, `IQueueSender` / `IQueueReceiver` / `IStorageQueue`, factory returning `IStorageQueue`, keyed registrations; 2.x façade rebuilt on top. All existing tests pass unchanged through the façade.
2. Per-client `IQueueClientBuilder`; `AddClient` / `AddDefaultClient` return it; `ValidateOnStart`; `InvalidOperationException`.
3. The processor: options, handler, hosted service, poison queue, renewal, backoff, drain. Integration tests against Azurite with fake time for the backoff and renewal timers.
4. Removals, README rewrite, migration guide, `3.0.0-preview.1` tag.

Previews publish from `v3.0.0-preview.N` tags with the existing `nuget.yml`; nuget.org marks them pre-release automatically.
