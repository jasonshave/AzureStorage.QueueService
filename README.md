# Azure Storage Queue Service library

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="./images/logo-dark.svg">
  <img src="./images/logo-light.svg" alt="Unifinity" width="75" />
</picture>

[![.NET](https://github.com/jasonshave/AzureStorage.QueueService/actions/workflows/dotnet.yml/badge.svg)](https://github.com/jasonshave/AzureStorage.QueueService/actions/workflows/dotnet.yml)
[![Nuget](https://github.com/jasonshave/AzureStorage.QueueService/actions/workflows/nuget.yml/badge.svg)](https://github.com/jasonshave/AzureStorage.QueueService/actions/workflows/nuget.yml)

Typed sending, receiving and processing for Azure Storage queues on .NET 10 and later, registered the way `IHttpClientFactory` registers clients: configure named queues once at startup, then inject them or run a processor against them.

Still on .NET 8? Use the 2.x package line, which stays on .NET 8 and keeps receiving fixes. Its documentation is in the [2.x README](https://github.com/jasonshave/AzureStorage.QueueService/tree/2.x#readme).

- **Send** with `IQueueSender`: your object goes in, JSON goes on the queue.
- **Receive** with `IQueueReceiver`: each message comes back typed, with its identity, and you complete, abandon or dead-letter it.
- **Process** with `AddProcessor<TMessage, THandler>()`: the library runs the loop — backoff, lease renewal, retries, a poison queue, cancellation and graceful shutdown — and hands each message to a handler class resolved from its own dependency-injection scope.

Upgrading from 2.x? 3.0 is a clean break — see [Upgrading from 2.x](#upgrading-from-2x) for the mapping.

## Install

Add the NuGet package [`AzureStorage.QueueService`](https://www.nuget.org/packages/AzureStorage.QueueService).

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
| `EndpointUri` + `TokenCredential` | Connect with an identity instead. The URI includes the queue name; a `QueueName` that says something different is rejected. |
| `CreateIfNotExists` | Create the queue on first use if it does not exist. Happens on the first operation, not at startup. |
| `VisibilityTimeout` | How long a message received with `ReceiveAsync` stays hidden when the call does not say. Default 30 seconds. A processor uses its own setting. |
| `PoisonQueueName` | Where dead-lettered messages go. Default: the queue name plus `-poison`. Set it when the queue name is longer than 56 characters (Azure's limit is 63) or to share one poison queue. |
| `ClientOptions` | `QueueClientOptions` for the Azure SDK client: retry policy, diagnostics, and `MessageEncoding`. |

Registering the same name twice adds to its configuration. `AddClient` and `AddDefaultClient` return a builder for that one client with `Configure(...)`, `UseSerializer(...)`, `UseJsonSerializer(...)` and `AddProcessor<,>(...)`; other packages can add their own extension methods to it.

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

## Get a client

Inject the default client as `IStorageQueue` (everything), `IQueueSender` (send only) or `IQueueReceiver` (receive only). Named clients are keyed services:

```csharp
public class OrderService(
    IQueueSender notifications,                                   // the default client
    [FromKeyedServices("orders")] IStorageQueue orders)           // a named client
{
}
```

Or ask the factory: `IQueueClientFactory.GetQueue()` / `GetQueue("orders")`. Clients are created once and shared for the life of the process; the SDK client underneath is thread-safe.

## Send

```csharp
await notifications.SendAsync(new Notification("hello"));
await notifications.SendAsync(new Notification("later"), new SendOptions(VisibilityTimeout: TimeSpan.FromMinutes(5)));
```

The return value is the SDK's `SendReceipt`. Failures are logged and thrown.

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

A processor is `MaxConcurrentMessages` workers; each receives one message, delivers it, and goes back for the next. What it does for you:

- **Polls with backoff.** An idle worker polls at a growing interval (1 s doubling to 30 s by default) that resets as soon as a message arrives. A failed receive — storage down, the queue missing, no permission — is logged and backed off the same way. Nothing at runtime stops the host.
- **Keeps the lease alive.** While your handler runs, the message's visibility timeout is renewed, which closes the avoidable window where a slow handler lets another consumer take the same message. Delivery is still at-least-once — a lease can lapse, a completion can fail after the work is done — so handlers should tolerate seeing a message twice.
- **Retries with a delay.** A handler that throws abandons the message with a growing delay (5 s, 10 s, 20 s, ... up to 5 minutes), logged with the message id and attempt count. A handler whose constructor throws is treated the same way, not swallowed.
- **Dead-letters poison messages.** A message delivered more than `MaxDequeueCount` times, or one whose body cannot be decoded or deserialized, is moved to the poison queue — body unchanged, so it can be moved straight back to replay it — without calling the handler. If that move fails, the message is abandoned instead, never dropped.
- **Lets the handler take over.** Call `message.CompleteAsync()`, `AbandonAsync()` or `DeadLetterAsync()` yourself and the processor leaves it alone; `message.Settlement` says what happened.
- **Shuts down cleanly.** On host shutdown it stops receiving, gives running handlers `ShutdownDrainTimeout` (30 s) to finish, then cancels their token and stops renewing their leases. A handler that ignores its token gets the same time again, after which shutdown stops waiting for it. All of that happens even if the host's own shutdown deadline cuts the drain short. Unfinished messages become visible again when their lease lapses.
- **Starts safely.** A processor whose client is misconfigured fails host startup, on purpose. Polling begins only when the host reports that the whole application has started, so a slow startup does not hold leases and a startup that fails part-way never polls at all.

| Option | Default | What it does |
|---|---|---|
| `MaxConcurrentMessages` | 1 | Workers, each handling one message at a time in its own scope. |
| `VisibilityTimeout` | 1 minute | How long each received message is hidden (1 second to 7 days, whole seconds); renewed while the handler runs. |
| `RenewVisibility` | true | Renew the lease at half the timeout while the handler runs. |
| `AutoComplete` | true | Complete on return, abandon on throw. Off, the handler must settle every message. |
| `MaxDequeueCount` | 5 | Dead-letter a message delivered more times than this. |
| `DeadLetterOnDeserializationFailure` | true | Dead-letter a message whose body cannot be read. |
| `RetryDelay` | 5 s × 2ⁿ, max 5 min | How long an abandoned message stays hidden before the next attempt. |
| `EmptyQueueBackoffMin` / `Max` | 1 s / 30 s | Polling interval on an empty queue. |
| `ErrorBackoffMax` | 1 minute | Longest wait after a failed receive. |
| `ShutdownDrainTimeout` | 30 s | How long shutdown waits for running handlers. |

## Receive in your own loop

If you want the loop, inject `IQueueReceiver` and own it. You get every message's identity and settle it yourself; backoff, renewal, retries and shutdown are yours to handle, and a failed receive throws so your loop can react.

```csharp
public class OrderPoller(IQueueReceiver receiver, ILogger<OrderPoller> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var message in await receiver.ReceiveAsync<Order>(maxMessages: 10, cancellationToken: stoppingToken))
                {
                    if (message.Error is not null)
                    {
                        await message.DeadLetterAsync(message.Error.Message, stoppingToken);
                        continue;
                    }

                    await Fulfil(message.Body!);
                    await message.CompleteAsync();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Polling failed."); }

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }
}
```

`QueueMessage<T>` carries `MessageId`, `PopReceipt`, `DequeueCount`, `InsertedOn`, `ExpiresOn`, `NextVisibleOn`, the `RawBody`, the typed `Body`, and `Error` when the body could not be decoded or deserialized. `CompleteAsync` deletes it; `AbandonAsync(delay)` makes it visible again now or later; `RenewLockAsync(timeout)` extends the lease; `DeadLetterAsync(reason)` moves it to the poison queue; `Settlement` records which of those happened. `PeekAsync<T>` returns `PeekedQueueMessage<T>`, which has no pop receipt and so nothing to settle.

## Testing your code

Depend on `IQueueSender` and `IQueueReceiver` and fake them. A received message can be built in a test: `QueueMessage<T>` has a public constructor that takes a `QueueMessageInfo`, the body and an `IQueueMessageOperations` — stub the operations to assert that your code completed or abandoned the message.

To test a handler end to end — send, processor, handler, then complete, retry or dead-letter — swap the real queues for in-memory ones. No storage account, emulator or connection string is needed, even with `ValidateOnStart()`; each client still needs a `QueueName`. Messages go through each client's serializer and follow the service's rules: a received message is hidden for its visibility timeout and its dequeue count goes up, an abandoned one stays hidden for its retry delay, and settling with an outdated pop receipt throws a `RequestFailedException` (404). Time comes from the registered `TimeProvider`, so a `FakeTimeProvider` drives polling, retries and expiry without real waits. Clients naming the same queue share it, so to see what was dead-lettered, register a client for the poison queue and peek it:

```csharp
var time = new FakeTimeProvider();
var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
builder.Services.AddSingleton<TimeProvider>(time);
builder.Services.AddSingleton(fulfilment); // a fake the handler calls, which the test can await
builder.Services.AddAzureStorageQueueClient(queues =>
{
    queues.AddClient("orders", settings => settings.QueueName = "orders")
          .AddProcessor<Order, OrderHandler>();
    queues.AddClient("orders-poison", settings => settings.QueueName = "orders-poison");
});
builder.Services.AddInMemoryQueues(); // before or after the registration; with WebApplicationFactory, in ConfigureTestServices

using var host = builder.Build();
await host.Services.GetRequiredKeyedService<IQueueSender>("orders").SendAsync(new Order(42));
await host.StartAsync();
await fulfilment.Called;  // the handler ran
await host.StopAsync();   // lets the delivery finish settling

var orders = host.Services.GetRequiredKeyedService<IStorageQueue>("orders");
var remaining = (await orders.GetPropertiesAsync()).ApproximateMessagesCount; // 0 once completed
var poisoned = await host.Services.GetRequiredKeyedService<IQueueReceiver>("orders-poison").PeekAsync<Order>(32);
```

To drive retries, advance the clock while the processor waits on it: a failed message stays hidden for its retry delay (5 s, then 10 s, ...), and an idle worker polls again after `EmptyQueueBackoffMin`. An advance made before the processor has started waiting does not wake it, so step the clock in time with the polls rather than in one jump.

The in-memory queues differ from the service in a few ways: every queue exists without being created, `ClientOptions` (including Base64 `MessageEncoding`) is ignored, the 64 KiB message size limit is not enforced, and sends and receives emit no spans or metrics of their own (the processor's still do).

## Telemetry

Traces and metrics use OpenTelemetry's standard .NET APIs:

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddAzureStorageQueueTracing())
    .WithMetrics(m => m.AddAzureStorageQueueMetrics());
```

The processor emits a `Queue.Process` span (kind: consumer) per message, tagged with the queue name, message id, dequeue count and outcome. `Queue.Read` and `Queue.Send` spans for individual receives and sends are off by default because they are noisy; turn them on with `ConfigureQueueServiceTelemetry`.

Metrics, all tagged with `queue.name`: `queue_messages_received_total`, `queue_messages_sent_total`, `queue_messages_processed_total`, `queue_messages_failed_total`, `queue_message_processing_duration_seconds` (also tagged with `queue.outcome`), `queue_message_size_bytes`.

## Upgrading from 2.x

**3.0 requires .NET 10.** A project on .NET 8 cannot install it; upgrade the runtime first, or stay on the [2.x line](https://github.com/jasonshave/AzureStorage.QueueService/tree/2.x#readme), which remains on .NET 8 and keeps receiving fixes.

3.0 is a clean break: the 2.x client (`AzureStorageQueueClient`, `IAzureStorageQueueClient`, `IMessageConverter`, `GetQueueClient()`) is gone, not obsolete. Since moving to 3.0 already means a runtime upgrade, carrying the old API alongside the new one bought little and cost real complexity. The mapping:

| 2.x | 3.0 |
|---|---|
| `IQueueClientFactory.GetQueueClient()` → `AzureStorageQueueClient` | `GetQueue()` → `IStorageQueue`, or inject `IStorageQueue` / `IQueueSender` / `IQueueReceiver` |
| `ReceiveMessagesAsync(handle, handleException, ...)` | `AddProcessor<TMessage, THandler>()`, or `ReceiveAsync<T>` in your own loop |
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
