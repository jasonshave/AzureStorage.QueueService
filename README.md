# Azure Storage Queue Service library

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="./images/logo-dark.svg">
  <img src="./images/logo-light.svg" alt="Unifinity" width="75" />
</picture>

[![.NET](https://github.com/jasonshave/AzureStorage.QueueService/actions/workflows/dotnet.yml/badge.svg)](https://github.com/jasonshave/AzureStorage.QueueService/actions/workflows/dotnet.yml)
[![Nuget](https://github.com/jasonshave/AzureStorage.QueueService/actions/workflows/nuget.yml/badge.svg)](https://github.com/jasonshave/AzureStorage.QueueService/actions/workflows/nuget.yml)

Typed sending, receiving and processing for Azure Storage queues on .NET 10 and later, registered the way `IHttpClientFactory` registers clients: configure named queues once at startup, then inject them or run a processor against them.

Still on .NET 8? Use the 2.x package line, which stays on .NET 8 and keeps receiving fixes. Its documentation is in the [2.x README](https://github.com/jasonshave/AzureStorage.QueueService/tree/2.x#readme).

- **Send** with `IQueueSender`: your object goes in, JSON goes on the queue, with optional headers, a delay and compression.
- **Receive** with `IQueueReceiver`: each message comes back typed, with its identity, and you complete, abandon or dead-letter it.
- **Process** with `AddProcessor<TMessage, THandler>()`: the library runs the loop — backoff, lease renewal, retries, a poison queue, cancellation and graceful shutdown — and hands each message to a handler resolved from its own dependency-injection scope. Handlers can be classes or lambdas, share middleware, and share one queue between several message types.
- **Look after the poison queue** with `IDeadLetterQueue`: see what was dead-lettered and why, and move messages back once the cause is fixed.

Upgrading? See [Upgrading from 3.0 to 3.1](#upgrading-from-30-to-31) for the behaviour changes, or [Upgrading from 2.x](#upgrading-from-2x) for the mapping from the old API.

## Install

Add the NuGet package [`AzureStorage.QueueService`](https://www.nuget.org/packages/AzureStorage.QueueService). Two optional packages build on it:

| Package | Adds |
|---|---|
| [`AzureStorage.QueueService.Blobs`](https://www.nuget.org/packages/AzureStorage.QueueService.Blobs) | [Messages over 64 KiB](#messages-over-64-kib), kept in Blob storage |
| [`AzureStorage.QueueService.HealthChecks`](https://www.nuget.org/packages/AzureStorage.QueueService.HealthChecks) | ASP.NET Core [health checks](#health-checks) for processors and queues |

You need an Azure Storage account and either its connection string or a queue URI plus an identity that can read and write the queue. The library can create the queue for you (`CreateIfNotExists`); the processor also creates a `<queue>-poison` queue the first time it dead-letters a message.

## Configure clients

```csharp
builder.Services.AddAzureStorageQueueClient(queues =>
{
    // The default client, bound from configuration
    queues.AddDefaultClient(builder.Configuration.GetSection("Queues:Notifications"));

    // A named client, configured in code and given its own serializer settings
    queues.AddClient("orders", settings =>
        {
            settings.EndpointUri = new Uri("https://myaccount.queue.core.windows.net/orders");
            settings.TokenCredential = new DefaultAzureCredential();
            settings.CreateIfNotExists = true;
        })
        .UseJsonSerializer(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    // Fail host startup if any client is missing its connection details
    queues.ValidateOnStart();
});
```

```json
{
  "Queues": {
    "Notifications": {
      "ConnectionString": "[your connection string]",
      "QueueName": "notifications",
      "CreateIfNotExists": true,
      "VisibilityTimeout": "00:02:00"
    }
  }
}
```

### Settings

| Setting | What it does |
|---|---|
| `ConnectionString` + `QueueName` | Connect with a storage account connection string. |
| `EndpointUri` + `TokenCredential` | Connect with an identity instead. `EndpointUri` is either the queue's URI (`https://account.queue.core.windows.net/orders`) or the account's endpoint (`https://account.queue.core.windows.net/`) with `QueueName`. A `QueueName` that contradicts the queue in the URI is rejected. |
| `CreateIfNotExists` | Create the queue on first use if it does not exist. Happens on the first operation, not at startup. |
| `VisibilityTimeout` | How long a message received with `ReceiveAsync` stays hidden when the call does not say. Default 30 seconds. A processor uses its own setting. |
| `PoisonQueueName` | Where dead-lettered messages go. Default: the queue name plus `-poison`. Set it when the queue name is longer than 56 characters (Azure's limit is 63) or to share one poison queue. |
| `PoisonTimeToLive` | How long a dead-lettered message is kept. Default: for ever, until you delete or replay it. |
| `ClientOptions` | `QueueClientOptions` for the Azure SDK client: retry policy, diagnostics, `MessageEncoding`, client-side encryption. |
| `PropagateTraceContext` | Carry the current trace with each sent message, so the processor's span joins the sender's trace. Off by default; see [what travels with a message](#what-travels-with-a-message). |
| `CompressionThreshold` | Gzip bodies of at least this many bytes. Off by default; set with `UseCompression()`. |
| `SendMessageType` | Send each message's type as a header, for a [routed processor](#several-message-types-on-one-queue). |
| `LargeMessageStore` / `LargeMessageThreshold` | Where bodies over 64 KiB go; see [Messages over 64 KiB](#messages-over-64-kib). |

Registering the same name twice adds to its configuration. `AddClient` and `AddDefaultClient` return a builder for that one client with `Configure(...)`, `UseSerializer(...)`, `UseJsonSerializer(...)`, `UseCompression(...)`, `UseLargeMessageStore(...)`, `UseMiddleware<T>()`, and the processor registrations; other packages add their own extension methods to it.

Everything a setting can hold in configuration binds from it, `ClientOptions` included:

```json
{ "Queues": { "Legacy": { "ConnectionString": "...", "QueueName": "legacy", "ClientOptions": { "MessageEncoding": "Base64" } } } }
```

### One storage account and identity for every client

A credential can't go in `appsettings.json`, so set it once in code. `UseTokenCredential` gives it to every client that has an `EndpointUri` but no `TokenCredential` of its own. `UseStorageAccount` does the same for the account itself: every client with neither a `ConnectionString` nor an `EndpointUri` connects to it, so configuration only needs queue names. Both apply to default and named clients registered before or after the call; a client's own settings still win; calling either again replaces what it set. `DefaultAzureCredential` comes from the `Azure.Identity` package, which your app references; this library doesn't.

```csharp
builder.Services.AddAzureStorageQueueClient(queues =>
{
    queues.AddDefaultClient(builder.Configuration.GetSection("Queues:Notifications"));
    queues.AddClient("orders", builder.Configuration.GetSection("Queues:Orders"));

    queues.UseStorageAccount(builder.Configuration.GetConnectionString("queues")!) // a connection string or https://account.queue.core.windows.net/
          .UseTokenCredential(new DefaultAzureCredential())
          .ValidateOnStart();
});
```

```json
{
  "ConnectionStrings": { "queues": "https://myaccount.queue.core.windows.net/" },
  "Queues": {
    "Notifications": { "QueueName": "notifications" },
    "Orders": { "QueueName": "orders", "CreateIfNotExists": true }
  }
}
```

That is the shape .NET Aspire hands an app: `ConnectionStrings:<name>` holds the account's queue endpoint (or, with the emulator, a connection string), and `UseStorageAccount` takes either.

### Queues shared with Azure Functions or older SDKs

Those write Base64-encoded messages. Tell the SDK, and everything else is unchanged. A message on such a queue that is *not* valid Base64 does not fail the receive: it comes back with `Error` set alongside the healthy messages, so it can be dead-lettered like any other bad message.

```csharp
queues.AddClient("legacy", settings =>
{
    settings.ConnectionString = "...";
    settings.QueueName = "legacy";
    settings.ClientOptions = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
});
```

Those senders usually write camelCase JSON. The library's default serializer reads property names case-insensitively, so `{"orderId": 7}` fills `OrderId`. It still writes PascalCase; use `UseJsonSerializer(new JsonSerializerOptions(JsonSerializerDefaults.Web))` to write camelCase too.

### Client-side encryption

The SDK's client-side encryption works as it is: give the client `SpecializedQueueClientOptions` with `ClientSideEncryption` set, and every message is encrypted before it leaves the process and decrypted on receive. Encryption makes each message bigger, which the library's 64 KiB check before sending does not account for, so leave headroom.

```csharp
settings.ClientOptions = new SpecializedQueueClientOptions
{
    ClientSideEncryption = new ClientSideEncryptionOptions(ClientSideEncryptionVersion.V2_0)
    {
        KeyEncryptionKey = key, KeyResolver = resolver, KeyWrapAlgorithm = "RSA-OAEP",
    },
};
```

## Get a client

Inject the default client as `IStorageQueue` (everything), `IQueueSender` (send only), `IQueueReceiver` (receive only) or `IDeadLetterQueue` (its poison queue). Named clients are keyed services:

```csharp
public class OrderService(
    IQueueSender notifications,                                   // the default client
    [FromKeyedServices("orders")] IStorageQueue orders)           // a named client
{
}
```

Or ask the factory: `IQueueClientFactory.GetQueue()` / `GetQueue("orders")`, and `GetDeadLetterQueue("orders")`. Clients are created once and shared for the life of the process; the SDK client underneath is thread-safe. Asking for a name nobody registered — a typo in `[FromKeyedServices]`, or the default client when only named ones exist — fails with a message listing the clients that are registered.

## Send

```csharp
await notifications.SendAsync(new Notification("hello"));
await notifications.SendAsync(new Notification("later"), new SendOptions { Delay = TimeSpan.FromMinutes(5) });
await notifications.SendAsync(new Notification("tagged"), new SendOptions
{
    Headers = new Dictionary<string, string> { [QueueMessageHeaders.CorrelationId] = correlationId },
});
```

`SendOptions` takes a `Delay` (also called `VisibilityTimeout`: 0 to 7 days), a `TimeToLive` (`Timeout.InfiniteTimeSpan` for never; the service default is 7 days), `Headers`, and a `MessageType`. A delay the message would expire within, or a message over the service's 64 KiB limit, throws before anything is sent: `ArgumentOutOfRangeException` and `QueueMessageTooLargeException` respectively. The size is checked as the message would go on the queue, so Base64 encoding and the library's envelope count. The return value is the SDK's `SendReceipt`. Failures are logged and thrown.

`UseCompression()` on a client gzips bodies of 1 KiB or more (pass another threshold if you like), skipping any message compression would not make smaller. Repetitive JSON often shrinks by 80% or more, which also keeps more messages under the 64 KiB limit.

### What travels with a message

A storage queue message is only a body: no properties, no headers. Anything that has to travel with it — the trace context, headers, a compressed body, a body kept in Blob storage — goes in a small JSON envelope around the body. A message that needs none of these goes out exactly as the serializer wrote it, as in 3.0.

```json
{"$azqs-envelope":1,"traceparent":"00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01","headers":{"tenant":"contoso"},"body":{"OrderId":42}}
```

Every client from 3.1 on reads the envelope when it receives, whatever its own settings, so `Body` and `RawBody` are the message you sent and `Headers` holds the headers. The full format is under [Wire format](#wire-format). The catch is older readers: a consumer on 3.0 or earlier, or other code reading the queue (an Azure Function, a script), sees the envelope as the message and fails to deserialize it, or worse, deserializes it into an object with empty fields. So headers, compression, `PropagateTraceContext` and `SendMessageType` are all off until you use them; turn them on only once every reader of the queue is on 3.1 or later.

**The envelope makes a message bigger:** roughly 100 bytes for the trace context, plus the headers, and binary data (a compressed body, or a custom serializer's non-JSON output) is Base64-encoded, a third bigger. A message close to the 64 KiB limit can tip over it; the send then throws rather than going out plain.

## Process messages (recommended)

Write a handler and register it against a client. A new handler is resolved from a new dependency-injection scope for every message, so scoped services such as a `DbContext` work exactly as they do in a controller.

```csharp
public class OrderHandler(OrdersDbContext db) : IQueueMessageHandler<Order>
{
    public async ValueTask HandleAsync(QueueMessage<Order> message, CancellationToken cancellationToken)
    {
        await db.Fulfil(message.Body!, cancellationToken);
        // Return and the message is completed. Throw and it is retried later.
    }
}

builder.Services.AddAzureStorageQueueClient(queues =>
    queues.AddClient("orders", builder.Configuration.GetSection("Queues:Orders"))
          .AddProcessor<Order, OrderHandler>(options =>
          {
              options.MaxConcurrentMessages = 4;
              options.MaxDequeueCount = 5;
          }));
```

Or as a lambda: parameters of the message type get the body, `QueueMessage<T>` or `IQueueMessage` the message, `CancellationToken` the shutdown token, and anything else is resolved from the message's scope (`[FromKeyedServices]` works too). Give every parameter a type.

```csharp
queues.AddClient("orders", builder.Configuration.GetSection("Queues:Orders"))
      .AddProcessor<Order>(async (Order order, OrdersDbContext db, CancellationToken ct) => await db.Fulfil(order, ct));
```

What the processor does for you:

- **Takes only what it can handle.** One receive loop per processor asks for as many messages as it has free handlers (up to 32 per request, at most `MaxConcurrentMessages` in flight) and starts each on its own task with its own scope. No message waits behind another's handler, and the processor never holds a message no handler is free for. When messages arrive it asks again straight away; an empty queue is polled at a growing interval (1 s doubling to 30 s by default) that resets as soon as a message arrives. A failed receive — storage down, the queue missing, no permission — is logged once and backed off the same way. Nothing at runtime stops the host.
- **Keeps the lease alive.** While your handler runs, the message's visibility timeout is renewed, which closes the avoidable window where a slow handler lets another consumer take the same message. Delivery is still at-least-once — a lease can lapse, a completion can fail after the work is done — so handlers should tolerate seeing a message twice. `MaxRenewalDuration` caps the renewal for a handler that may hang; a handler running longer than `LongRunningHandlerWarning` (10 minutes) is logged once.
- **Retries with a delay.** A handler that throws abandons the message with a growing delay (5 s, 10 s, 20 s, ... up to 5 minutes). That is a warning in the log while retries remain and an error on the last attempt; `message.IsLastAttempt` tells the handler which it is on. A handler whose constructor throws is treated the same way, not swallowed. An [error policy](#error-policy) can dead-letter or complete instead.
- **Dead-letters poison messages.** A message delivered more than `MaxDequeueCount` times, or one whose body cannot be read, is moved to the poison queue without calling the handler, with a record of why. If that move fails, the message is abandoned instead, never dropped. See [The poison queue](#the-poison-queue).
- **Lets the handler take over.** Call `message.CompleteAsync()`, `AbandonAsync()` or `DeadLetterAsync()` yourself and the processor leaves it alone; `message.Settlement` says what happened.
- **Shuts down cleanly.** On host shutdown it stops receiving, gives running handlers `ShutdownDrainTimeout` (30 s) to finish, then cancels their token and stops renewing their leases. A handler that ignores its token gets the same time again, after which shutdown stops waiting for it. All of that happens even if the host's own shutdown deadline cuts the drain short. Unfinished messages become visible again when their lease lapses.
- **Starts safely.** A processor whose client is misconfigured fails host startup, on purpose. Polling begins only when the host reports that the whole application has started, so a slow startup does not hold leases and a startup that fails part-way never polls at all.

| Option | Default | What it does |
|---|---|---|
| `MaxConcurrentMessages` | 1 | Messages handled at once, each in its own scope. |
| `VisibilityTimeout` | 1 minute | How long each received message is hidden (1 second to 7 days, whole seconds); renewed while the handler runs. |
| `RenewVisibility` | true | Renew the lease at half the timeout while the handler runs. |
| `MaxRenewalDuration` | none | Stop renewing after this long, so a hung handler's message is redelivered. |
| `LongRunningHandlerWarning` | 10 minutes | Log a warning, once, for a handler still running after this long. |
| `AutoComplete` | true | Complete on return, abandon on throw. Off, the handler must settle every message. |
| `MaxDequeueCount` | 5 | Dead-letter a message delivered more times than this. |
| `DeadLetterOnDeserializationFailure` | true | Dead-letter a message whose body cannot be read. |
| `RetryDelay` | 5 s × 2ⁿ, max 5 min | How long an abandoned message stays hidden before the next attempt. |
| `DeadLetterOn<TException>()`, `OnHandlerError` | none | The [error policy](#error-policy). |
| `RateLimiter` | none | A `System.Threading.RateLimiting` limiter: one permit per message, taken before each receive. |
| `EmptyQueueBackoffMin` / `Max` | 1 s / 30 s | Polling interval on an empty queue. |
| `ErrorBackoffMax` | 1 minute | Longest wait after a failed receive. |
| `ShutdownDrainTimeout` | 30 s | How long shutdown waits for running handlers. |

Options can come from configuration, so they can differ by environment: `AddProcessor<Order, OrderHandler>(builder.Configuration.GetSection("Queues:Orders:Processor"), options => ...)`. Configuration is applied first, then the delegate; the callbacks and the rate limiter are set in code. Registering the same handler on the same client again adds to its options rather than starting a second processor.

While a message is being delivered, everything logged — by the library and by your handler — carries a scope with the queue name, message id and dequeue count, for log sinks that don't correlate by trace id.

### The message

`QueueMessage<T>` carries `MessageId`, `QueueName`, `PopReceipt`, `DequeueCount`, `InsertedOn`, `ExpiresOn`, `NextVisibleOn`, `Headers`, `IsLastAttempt`, the `RawBody`, the typed `Body`, and `Error` when the body could not be read. `CompleteAsync` deletes it; `AbandonAsync(delay)` makes it visible again now or later; `RenewLockAsync(timeout)` extends the lease; `DeadLetterAsync(reason)` moves it to the poison queue; `Settlement` records which of those happened. Timeouts the service would refuse throw `ArgumentOutOfRangeException` before any call. `IQueueMessage` is the same message without its body's type, for code that serves every message type.

### Error policy

Some failures are not worth retrying. Tell the processor which, instead of wrapping every handler in a try/catch:

```csharp
.AddProcessor<Order, OrderHandler>(options =>
{
    options.DeadLetterOn<ValidationException>();       // straight to the poison queue, no retries
    options.OnHandlerError = context => context.Exception switch
    {
        DuplicateOrderException => HandlerErrorAction.Complete,   // already done: delete it
        _ => HandlerErrorAction.Retry,
    };
});
```

`DeadLetterOn` rules are checked first, then `OnHandlerError`; with neither, the message is retried. A callback that throws counts as `Retry`.

### Middleware

Middleware runs around every message, the way ASP.NET Core middleware runs around a request: idempotency checks, timing, a circuit breaker, a tenant scope — anything every handler would otherwise repeat. It is resolved from the message's scope, so it can take scoped services.

```csharp
public class IdempotencyMiddleware(ProcessedMessages processed) : IQueueMessageMiddleware
{
    public async ValueTask InvokeAsync(QueueMessageContext context, QueueMessageDelegate next)
    {
        if (await processed.ContainsAsync(context.Message.MessageId)) return; // handled already: complete it
        await next(context);
        await processed.AddAsync(context.Message.MessageId);
    }
}

builder.Services.AddAzureStorageQueueClient(queues =>
{
    queues.UseMiddleware<TimingMiddleware>();                  // every client's processors
    queues.AddClient("orders", ...).UseMiddleware<IdempotencyMiddleware>().AddProcessor<Order, OrderHandler>();
});
```

Middleware runs in the order registered. Not calling `next` stops there, and the message counts as handled (completed, with `AutoComplete` on). An exception counts as a handler failure, error policy included. `context.Items` passes things along; `context.Services` is the message's scope.

### Several message types on one queue

Two processors reading one queue take each other's messages and fail to read them. To share a queue, route by type: the sender sends each message's type as a header, and one processor hands each message to the handler for its type.

```csharp
// Sending side: every message carries its type (typeof(T).Name), or set SendOptions.MessageType per message
queues.AddClient("orders", settings => { ...; settings.SendMessageType = true; });

// Receiving side
queues.AddClient("orders", ...)
      .AddRoutedProcessor(routes => routes
          .Route<OrderCreated, OrderCreatedHandler>()             // type "OrderCreated"
          .Route<OrderCancelled, OrderCancelledHandler>("cancelled"));
```

A message with no type, or one nothing is routed for, is dead-lettered as `unroutable`. If you can't change the senders, `System.Text.Json`'s polymorphism works with a plain processor, as long as the sender sends the base type:

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(OrderCreated), "created")]
[JsonDerivedType(typeof(OrderCancelled), "cancelled")]
public abstract record OrderEvent;

await sender.SendAsync<OrderEvent>(new OrderCreated(42));        // writes "$type":"created"
queues.AddClient("orders", ...).AddProcessor<OrderEvent, OrderEventHandler>(); // switch on message.Body
```

### Pausing a processor

`IQueueProcessorController` pauses and resumes a client's processors — during a downstream outage, say — and reports how each one is doing (`GetStatus()`: state, handlers running, receive failures in a row, the oldest handler's age). A paused processor stops receiving; what it already holds is delivered and settled as usual. The default client's name is the empty string.

```csharp
controller.Pause("orders");
// ...
controller.Resume("orders");
```

### Event Grid and other CloudEvents

When the queue is fed by an Event Grid subscription that uses the CloudEvents schema (or anything else that writes [CloudEvents](https://cloudevents.io) as JSON), register an `ICloudEventHandler<TData>` instead. The handler gets the event's `data` already read as `TData`, and the event itself as `message.Body`, for its type, source, id, subject and time:

```csharp
public record OrderCreated(string OrderId, decimal Total);

public class OrderCreatedHandler(OrdersDbContext db) : ICloudEventHandler<OrderCreated>
{
    public async ValueTask HandleAsync(QueueMessage<CloudEvent> message, OrderCreated? data, CancellationToken cancellationToken)
    {
        var cloudEvent = message.Body!; // cloudEvent.Type, .Source, .Id, .Subject, .Time
        await db.Record(cloudEvent.Id, data!, cancellationToken);
    }
}

builder.Services.AddAzureStorageQueueClient(queues =>
    queues.AddClient("orders-events", settings =>
          {
              settings.ConnectionString = "...";
              settings.QueueName = "orders-events";
              // Event Grid Base64-encodes what it writes to a storage queue unless the subscription says otherwise
              settings.ClientOptions = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
          })
          .UseJsonSerializer(new JsonSerializerOptions(JsonSerializerDefaults.Web)) // event data is usually camelCase
          .AddCloudEventProcessor<OrderCreated, OrderCreatedHandler>());
```

A subscription that delivers several event types goes to a router, which picks the handler by the event's `type`:

```csharp
.AddCloudEventRouter(routes => routes
    .Route<OrderCreated, OrderCreatedHandler>("Contoso.Orders.Created")
    .Route<OrderShipped, OrderShippedHandler>("Contoso.Orders.Shipped"));
```

It is the same processor underneath, with the same options and behaviour: return to complete, throw to retry, settle through `message` yourself if you want to. A few things are specific to events:

- **Base64.** Event Grid's storage queue destination Base64-encodes each message by default. Set `MessageEncoding = QueueMessageEncoding.Base64` as above, or turn off Base64 encoding on the event subscription and leave the client as it is.
- **The data is read with this client's serializer,** so its naming policy applies. An event with no data reaches the handler with `data` null.
- **An event whose data cannot be read as `TData`** is treated like any other unreadable message: moved to the poison queue without calling the handler, with the event id and type in the reason. With `DeadLetterOnDeserializationFailure` off, the handler is called with `data` null and `message.Error` set. A router dead-letters an event of a type nothing is routed for.
- **Binary data** (`data_base64`, or any data that is not JSON): use `BinaryData` as `TData` to get the bytes as they are.
- **Event Grid's own schema** (`EventGridEvent`, Event Grid's default schema) is not supported; it needs the `Azure.Messaging.EventGrid` package. Choose the CloudEvents v1.0 schema when you create the subscription.

`AddProcessor<CloudEvent, THandler>()` also works, if you would rather read `message.Body.Data` yourself.

## Receive in your own loop

If you want the loop, inject `IQueueReceiver` and own it. `ReadAllAsync` does the polling — a growing wait while the queue is empty or after a failed receive — and hands you messages one at a time until the token is cancelled; settling each one, renewal, retries and dead-lettering are yours.

```csharp
public class OrderPoller(IQueueReceiver receiver, ILogger<OrderPoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = new ReadAllOptions { OnReceiveError = ex => logger.LogError(ex, "Receiving orders failed.") };
        await foreach (var message in receiver.ReadAllAsync<Order>(options, stoppingToken))
        {
            if (message.Error is not null)
            {
                await message.DeadLetterAsync(message.Error.Message);
                continue;
            }

            await Fulfil(message.Body!);
            await message.CompleteAsync();
        }
    }
}
```

`ReceiveAsync<T>(maxMessages, visibilityTimeout)` is underneath, for one receive at a time. `PeekAsync<T>` returns `PeekedQueueMessage<T>`, which has no pop receipt and so nothing to settle.

## The poison queue

A dead-lettered message is copied to the poison queue (`<queue>-poison` unless `PoisonQueueName` says otherwise) with a record of why, then deleted from its queue. It stays there until you delete or replay it; set `PoisonTimeToLive` to expire it instead. `IDeadLetterQueue` reads the record and moves messages back:

```csharp
public class PoisonAdmin([FromKeyedServices("orders")] IDeadLetterQueue poison)
{
    public async Task<int> RetryOutageFailures()
    {
        foreach (var message in await poison.PeekAsync())
            Console.WriteLine($"{message.OriginalMessageId}: {message.Category} — {message.Reason} (after {message.DequeueCount} attempts, {message.DeadLetteredOn})");

        // Back to the orders queue, as they were sent, with a fresh dequeue count
        return await poison.ReplayAsync(filter: m => m.Category == DeadLetterCategories.Handler && m.Reason!.Contains("TimeoutException"));
    }
}
```

Each record has the reason, the category (`max_dequeue`, `deserialization`, `handler` or `unroutable`), the original message id, the dequeue count, the source queue and the time. `GetBody<T>()` reads a dead-lettered body with the source client's serializer. A replayed message goes back exactly as it was sent — trace context, headers and all — without the record. A message the library could not read at all (a later version's format, say) is kept exactly as it arrived, so a replay after upgrading reads it.

## Messages over 64 KiB

The service rejects a queue message over 64 KiB. Add the `AzureStorage.QueueService.Blobs` package and a client keeps bodies that big in Azure Blob storage instead: the body is uploaded to a blob, and the queue message carries only the blob's address (the claim-check pattern). Processors and receivers download the body before your handler sees it, so handlers don't change. The blob is deleted once the message is completed, and kept when it is dead-lettered so a replay still has its body.

```csharp
queues.AddClient("orders", builder.Configuration.GetSection("Queues:Orders"))
      .UseBlobStorageForLargeMessages();          // container "queue-large-messages" in the queue's own account
```

With no arguments it uses the queue's own storage account and credentials: a connection string as is, or `https://myaccount.queue.core.windows.net/...` becomes `https://myaccount.blob.core.windows.net/`. You can also pass a container name, a `BlobContainerClient`, or `sp => ...` to build the container client from your services. The container is created the first time a large message is sent; pass `createContainerIfNotExists: false` if it already exists. Pass `thresholdBytes` to send smaller bodies to Blob storage as well. Any other store can plug in by implementing `ILargeMessageStore` and calling `UseLargeMessageStore`.

Things to know before turning it on:

- **Every client that reads the queue needs the same store.** One without it can't read those messages and dead-letters them. Large messages travel in the library's envelope, which only 3.1 and later read.
- **Some blobs are left behind.** A message that expires on the queue or is cleared from it is never processed, so its blob stays; a send that fails after the upload deletes its blob only on a best-effort basis. Add a [lifecycle management rule](https://learn.microsoft.com/azure/storage/blobs/lifecycle-management-overview) that deletes blobs in the container older than the queue's message time to live plus a margin (for example 9 days for the default 7), longer if you replay dead-lettered messages later.
- **Permissions:** the identity needs **Storage Blob Data Contributor** on the container or the account. One that can't create containers needs the container created beforehand and `createContainerIfNotExists: false`, for example `.UseBlobStorageForLargeMessages("order-bodies", createContainerIfNotExists: false)`.
- **Messages are untrusted input.** The store only reads and deletes blobs in its own container; a message pointing anywhere else is dead-lettered without a request being made. A SAS token on the container client is never written to the queue.

## Testing your code

Depend on `IQueueSender` and `IQueueReceiver` and fake them. A received message can be built in a test: `QueueMessage<T>` has a public constructor that takes a `QueueMessageInfo` (whose optional `QueueName`, `Headers` and `IsLastAttempt` set those properties), the body and an `IQueueMessageOperations` — stub the operations to assert that your code completed or abandoned the message.

To test a handler end to end — send, processor, handler, then complete, retry or dead-letter — swap the real queues for in-memory ones. No storage account, emulator or connection string is needed, even with `ValidateOnStart()`; each client still needs a `QueueName`, or an `EndpointUri` that names the queue, checked by the same rules as in production. The in-memory queues run the same queue code as the real ones, so messages go through each client's serializer, envelope and size check, emit the same spans and metrics, and follow the service's rules: a received message is hidden for its visibility timeout and its dequeue count goes up, an abandoned one stays hidden for its retry delay, and settling with an outdated pop receipt throws a `RequestFailedException` (404). Time comes from the registered `TimeProvider`, so a `FakeTimeProvider` drives polling, retries and expiry without real waits. Clients naming the same queue share it.

```csharp
var time = new FakeTimeProvider();
var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
builder.Services.AddSingleton<TimeProvider>(time);
builder.Services.AddSingleton(fulfilment); // a fake the handler calls, which the test can await
builder.Services.AddAzureStorageQueueClient(queues =>
    queues.AddClient("orders", settings => settings.QueueName = "orders")
          .AddProcessor<Order, OrderHandler>());
builder.Services.AddInMemoryQueues(); // before or after the registration; with WebApplicationFactory, in ConfigureTestServices

using var host = builder.Build();
await host.Services.GetRequiredKeyedService<IQueueSender>("orders").SendAsync(new Order(42));
await host.StartAsync();
await fulfilment.Called;  // the handler ran
await host.StopAsync();   // lets the delivery finish settling

var orders = host.Services.GetRequiredKeyedService<IStorageQueue>("orders");
var remaining = (await orders.GetPropertiesAsync()).ApproximateMessagesCount; // 0 once completed
var poisoned = await host.Services.GetRequiredKeyedService<IDeadLetterQueue>("orders").PeekAsync();
```

Messages sent before the host starts are picked up by the processor's first receive, so the example needs no clock at all. To drive retries, advance the clock while the processor waits on it: a failed message stays hidden for its retry delay (5 s, then 10 s, ...), and an idle processor polls again after `EmptyQueueBackoffMin`. An advance made before the processor has started waiting does not wake it, so step the clock in time with the polls rather than in one jump.

The in-memory queues differ from the service in a few ways: every queue exists without being created, and `ClientOptions` is ignored apart from `MessageEncoding`, which counts toward the size check as it would in production.

## Health checks

The `AzureStorage.QueueService.HealthChecks` package adds two ASP.NET Core health checks.

```csharp
builder.Services.AddHealthChecks()
    .AddAzureStorageQueueProcessors(tags: ["live"])
    .AddAzureStorageQueue("orders", tags: ["ready"], configure: o => o.DegradedWhenPoisonMessages = true);

app.MapHealthChecks("/health/live", new() { Predicate = c => c.Tags.Contains("live") });
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") });
```

- **`AddAzureStorageQueueProcessors`** reports on the hosted processors from `IQueueProcessorController.GetStatus()`. It makes no storage calls, so it is free to run on every probe. It is **Unhealthy** when a processor's last 3 receives all failed (`UnhealthyAfterReceiveFailures`), and **Degraded** when a processor is paused (`PausedStatus`) or a message has been in its handler longer than `MaxHandlerAge` (off by default). A processor still waiting for the host to start counts as healthy.
- **`AddAzureStorageQueue(clientName)`** checks the queue can be reached by reading its properties. With `DegradedWhenPoisonMessages` it also reports **Degraded** while the poison queue holds messages. **Each run is a billable storage call** (two with the poison option), so keep it off high-frequency probes. A run longer than `Timeout` (10 s) counts as failed.

## Telemetry

Traces and metrics use OpenTelemetry's standard .NET APIs:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddAzureStorageQueueTracing())
    .WithMetrics(m => m.AddAzureStorageQueueMetrics());
```

The processor emits a `Queue.Process` span (kind: consumer) per message, tagged with the queue name, message id, dequeue count and outcome, with events when the message is renewed, completed, abandoned or dead-lettered. `Queue.Read` (client) and `Queue.Send` (producer) spans for individual receives and sends are off by default because they are noisy; turn them on with `ConfigureQueueServiceTelemetry`. A span whose operation failed is marked with an error status and `error.type`. Spans also carry the [OpenTelemetry messaging attributes](https://opentelemetry.io/docs/specs/semconv/messaging/) — `messaging.system` (`azure.storage.queue`), `messaging.destination.name`, `messaging.operation.type` / `.name`, `messaging.message.id`, `messaging.batch.message_count`, `server.address` / `server.port` — so App Insights and Grafana's messaging views recognise them.

**Metrics,** all tagged with `queue.name`:

| Metric | What it says |
|---|---|
| `queue_messages_received_total`, `queue_messages_sent_total`, `queue_messages_processed_total`, `queue_messages_failed_total` | Counts, as in 3.0. |
| `queue_message_processing_duration_seconds` | Histogram, tagged `queue.outcome`. Buckets from 5 ms to an hour. |
| `queue_message_size_bytes` | Histogram of what the serializer wrote, before any envelope. Buckets up to 64 KiB. |
| `queue_message_time_in_queue_seconds` | Histogram: how long a message waited before a processor started on it, measured from when it was first sent (retries included). The best "are we keeping up" signal, and it costs nothing. |
| `queue_receive_requests_total` | Receive calls, tagged `queue.receive_result`: `message`, `empty` or `error`. Every one is a billable transaction, empty polls included. |
| `queue_processor_active_handlers` | Handlers running now; compare with `MaxConcurrentMessages` for saturation. |
| `queue_processor_oldest_handler_age_seconds` | Gauge: the longest-running handler. A value that keeps growing is a hung handler. |
| `queue_lease_renewals_total`, `queue_lease_renewal_failures_total` | Renewals, and renewals that failed (that message will be redelivered). |
| `queue_messages_dead_lettered_total` | Dead-lettered messages, tagged `queue.dead_letter_reason`. |
| `queue_approximate_messages` | Gauge, tagged `queue.kind` (`main` or `poison`), when `queues.AddQueueDepthMetrics(interval)` is on. Each read is a billable call per queue, so pick the interval with that in mind (default one minute). |

**Standard metric names (opt-in).** `m.AddAzureStorageQueueMessagingMetrics()` subscribes to a second meter, `AzureStorage.QueueService.Messaging`, with the conventions' own names: `messaging.client.sent.messages`, `messaging.client.consumed.messages`, `messaging.process.duration` and `messaging.client.operation.duration` (sends, receives and settlements). It leaves the metrics above, and their history, untouched.

**Metrics in tests.** When the container has an `IMeterFactory` (the generic host registers one; add it yourself with `services.AddMetrics()`), the library creates its meters through it, so a `MetricCollector<T>` scoped to that factory sees exactly that host's measurements.

**Exemplars** link a slow duration bucket to the trace that put it there. Turn them on in the OpenTelemetry SDK with `.WithMetrics(m => m.SetExemplarFilter(ExemplarFilterType.TraceBased))`; the processing-duration histogram is recorded inside the `Queue.Process` span, so its exemplars point at that message's trace.

**Alerts worth having:**

- *Falling behind:* p95 of `queue_message_time_in_queue_seconds` above what your users tolerate.
- *Poison messages:* `queue_approximate_messages{queue_kind="poison"} > 0`, or any increase in `queue_messages_dead_lettered_total`.
- *Failing handlers:* `queue_messages_failed_total` as a share of `queue_messages_received_total` over 15 minutes.
- *Wasted polls:* the share of `queue_receive_requests_total{queue_receive_result="empty"}`; raise `EmptyQueueBackoffMax` if it is high and latency allows.
- *Lost leases:* any `queue_lease_renewal_failures_total`, which means duplicates are coming.
- *Hung handlers:* `queue_processor_oldest_handler_age_seconds` above your slowest legitimate handler.

**Scaling with KEDA.** KEDA's `azure-queue` scaler reads the same approximate count as `GetPropertiesAsync`, which includes messages that are hidden — being processed, or waiting out a retry delay. A queue whose messages keep failing therefore keeps its count up and holds replicas that have nothing to receive. Set `queueLength` with that in mind, keep `MaxDequeueCount` modest so failing messages leave for the poison queue, and give the poison queue no scaler.

## Wire format

A message is either exactly what the serializer wrote, or an envelope: a JSON object whose first property is the marker `"$azqs-envelope"` with the format version, `1`. The other properties are optional and may come in any order:

| Property | Holds |
|---|---|
| `traceparent`, `tracestate` | The sender's W3C trace context. |
| `headers` | An object of string headers. |
| `encoding` | `gzip` when the body is compressed. Applies to `body64` or the blob, never to `body`. |
| `deadLetter` | On the poison queue only: `reason`, `category`, `messageId`, `dequeueCount`, `queue`, `at`. |
| `body` / `body64` / `blob` | Exactly one: the serializer's output as is when it is JSON; any other bytes, Base64-encoded; or a reference to a body kept by a large message store. |

The queue's `MessageEncoding` applies to the whole envelope, as it does to a plain body. The rules that keep different versions from misreading each other: a reader skips any property it does not know, so later 3.x releases can add optional properties without changing the version; anything that changes how the body must be read uses a new `encoding` value, a new kind of body, or a new version number. A reader that meets a version or an encoding it does not know, or an envelope that breaks these rules, does not guess: the message comes back with `Error` set, a processor dead-letters it unchanged, and it can be replayed once the reader is upgraded.

## Trimming and native AOT

The library is annotated for trimming and native AOT, and an app using it publishes trimmed without warnings, the Azure SDK included. Two things need doing in such an app:

- **JSON:** reflection-based serialization is off, so give the serializer your source-generated metadata: `UseJsonSerializer(MyJsonContext.Default)` on each client, with every message type (and `CloudEvent`, if you use the CloudEvent processor) listed in the context.
- **Configuration:** `AddClient(name, IConfiguration)` binds by reflection and is marked as not trim-safe; set the settings in code with `AddClient(name, settings => ...)` instead. Processor options bound with `AddProcessor<,>(IConfiguration)` are source-generated and safe.

Lambda handlers are fine: their parameters are read from the delegate itself.

## Upgrading from 3.0 to 3.1

3.1 adds to the API without removing anything. Recompile and these are the behaviour changes to know about:

- **The default serializer reads JSON property names case-insensitively.** A camelCase message used to fill none of a PascalCase type's properties, and was completed as if it were fine; now it fills them. Pass your own `JsonSerializerOptions` to keep the old strictness. Output is unchanged.
- **Poison messages no longer expire after 7 days.** They used to take the service's default time to live and could disappear unseen; now they stay until deleted or replayed, or for `PoisonTimeToLive`.
- **A poison message records why it was dead-lettered**, so the copy on the poison queue is now an envelope around the original body. A 3.1 reader (and `IDeadLetterQueue`) reads it; a tool that read poison messages as plain JSON sees the envelope. Moving one back by hand still works for 3.1 receivers, but `ReplayAsync` is the clean way: it strips the record.
- **One receive loop per processor** instead of one per handler slot. An idle processor makes one request per interval rather than `MaxConcurrentMessages` of them, and a burst after a quiet spell is picked up at the next poll instead of whenever each sleeping worker woke.
- **Quieter logs.** Healthy messages log at Debug, not Information. A failed poll is logged once, not twice. A handler failure is a warning while retries remain and an error on the last attempt.
- **Arguments are checked before calling the service:** `ReceiveAsync`, `AbandonAsync`, `RenewLockAsync` and `SendOptions` throw `ArgumentOutOfRangeException` where they used to get a 400 back, and a send over 64 KiB throws `QueueMessageTooLargeException` instead of a 413.
- **In-memory queues behave more like the service:** they enforce the size limit and emit spans and metrics.
- **`IStorageQueue`, `IQueueSender` and `IQueueReceiver` are always registered,** with or without a default client, so injecting one without a default client fails with a message naming the registered clients. Code that checked `GetService<IStorageQueue>() is null` to detect a default client should check its configuration instead.
- **The processing-duration and size histograms have explicit buckets** (seconds and bytes) and units. Dashboards built on the old default buckets will see the bucket boundaries change once.

## Upgrading from 2.x

**3.0 requires .NET 10.** A project on .NET 8 cannot install it; upgrade the runtime first, or stay on the [2.x line](https://github.com/jasonshave/AzureStorage.QueueService/tree/2.x#readme), which remains on .NET 8 and keeps receiving fixes.

3.0 is a clean break: the 2.x client (`AzureStorageQueueClient`, `IAzureStorageQueueClient`, `IMessageConverter`, `GetQueueClient()`) is gone, not obsolete. Since moving to 3.0 already means a runtime upgrade, carrying the old API alongside the new one bought little and cost real complexity. The mapping:

| 2.x | 3.0 |
|---|---|
| `IQueueClientFactory.GetQueueClient()` → `AzureStorageQueueClient` | `GetQueue()` → `IStorageQueue`, or inject `IStorageQueue` / `IQueueSender` / `IQueueReceiver` |
| `ReceiveMessagesAsync(handle, handleException, ...)` | `AddProcessor<TMessage, THandler>()`, or `ReadAllAsync<T>` / `ReceiveAsync<T>` in your own loop |
| `SendMessageAsync(message, visibility, ttl)` | `SendAsync(message, new SendOptions(visibility, ttl))` |
| `PeekMessagesAsync<T>(n)` → `IEnumerable<T>` | `PeekAsync<T>(n)` → `PeekedQueueMessage<T>` with identity and `Error` |
| `IMessageConverter` | `IQueueMessageSerializer` |
| `QueueClientSettings.SerializerOptions` | `.UseJsonSerializer(options)` on the client builder |
| Base64 sniffing on receive | `ClientOptions.MessageEncoding = QueueMessageEncoding.Base64` |
| `x.AddClient("a", ...).AddClient("b", ...)` | Two statements: `AddClient` now returns a builder for that one client |
| `ApplicationException` on configuration errors | `InvalidOperationException` |

Also removed: the synchronous `PeekMessages`, `MetricExtensions.ProcessWithMetricsAsync`, `QueueClientSettings.SectionName`, `TagListExtensions`, and the unused `queue_depth` metric. `LoggerMessageExtensions` is now internal: its methods were the library's own log messages, not an API.

A queue name longer than 56 characters now needs `PoisonQueueName` set, even on a client that only sends, because the default `<queue>-poison` would exceed Azure's 63-character limit. The error message says so.

## License

This project is licensed under the MIT License - see the [LICENSE.md](license.md) file for details.

The logo is the Unifinity Software Corporation brand mark, taken unchanged from the Unifinity brand kit. It is not covered by the MIT licence.
