# AzureStorage.QueueService.HealthChecks

Health checks for [AzureStorage.QueueService](https://github.com/jasonshave/AzureStorage.QueueService), for use with
ASP.NET Core's `AddHealthChecks()` and `MapHealthChecks()`.

```
dotnet add package AzureStorage.QueueService.HealthChecks
```

## The two checks

**`AddAzureStorageQueueProcessors()`** reports whether your hosted queue processors are working. It reads their
in-memory state only, so it makes no storage calls and costs nothing to run on every probe.

- **Unhealthy** when a processor's last 3 receives all failed (it cannot reach its queue, the queue is missing, or it
  lost access). Change the count with `UnhealthyAfterReceiveFailures`.
- **Degraded** when a processor is paused (change with `PausedStatus`), or when a message has been in its handler
  longer than `MaxHandlerAge` (off unless you set it; a sign of a stuck handler).
- **Healthy** otherwise, including while the host is still starting.

**`AddAzureStorageQueue(clientName)`** checks that a queue can be reached by reading its properties. With
`DegradedWhenPoisonMessages` it also reports Degraded while the poison queue (where messages go after failing too many
times) holds any messages. **Each run is a billable Azure Storage call** (two with the poison option), so keep it off
a probe that runs every few seconds. A run that takes longer than `Timeout` (10 seconds by default) is reported as
failed rather than holding the probe open.

## Example

```csharp
builder.Services.AddAzureStorageQueueClient(queues =>
    queues.AddClient("orders", settings => builder.Configuration.Bind("Queues:Orders", settings))
          .AddProcessor<Order, OrderHandler>());

builder.Services.AddHealthChecks()
    .AddAzureStorageQueueProcessors(tags: ["live"], configure: o => o.MaxHandlerAge = TimeSpan.FromMinutes(10))
    .AddAzureStorageQueue("orders", tags: ["ready"], configure: o => o.DegradedWhenPoisonMessages = true);

var app = builder.Build();
app.MapHealthChecks("/health/live", new() { Predicate = check => check.Tags.Contains("live") });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") });
```

By default `MapHealthChecks` answers 200 for Healthy and Degraded and 503 for Unhealthy. Each result's description
says which processor or queue has a problem and why, and its data carries the details (state, receive failures, last
error, message counts) for a response writer to show.
