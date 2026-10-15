# Azure Storage Queue Service library

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="./images/logo-dark.svg">
  <img src="./images/logo-light.svg" alt="Unifinity" width="75" />
</picture>

[![.NET](https://github.com/jasonshave/AzureStorage.QueueService/actions/workflows/dotnet.yml/badge.svg)](https://github.com/jasonshave/AzureStorage.QueueService/actions/workflows/dotnet.yml)
[![Nuget](https://github.com/jasonshave/AzureStorage.QueueService/actions/workflows/nuget.yml/badge.svg)](https://github.com/jasonshave/AzureStorage.QueueService/actions/workflows/nuget.yml)

This project abstracts away the complexities of using the Azure Storage account's queue feature targeting .NET 6 and higher.

## Pre-requisites

You will need to create an Azure Storage account in the Azure portal using a unique name, then create a queue, and finally obtain your connection string.

1. [Create a storage account](https://docs.microsoft.com/en-us/azure/storage/common/storage-account-create?tabs=azure-portal) in your Azure portal.
2. [Obtain your connection string](https://docs.microsoft.com/en-us/azure/storage/common/storage-account-keys-manage?tabs=azure-portal) from the Azure portal.

## Message handling behavior

- Sending a message will automatically serialize the payload to `BinaryData`.
- By default one message is pulled when `ReceiveMessagesAsync<T>` is called, however this is configurable.
- If your handler does not throw, messages are automatically removed from the queue otherwise the message is returned to the queue for delivery again.
- `ReceiveMessagesAsync<T>` never throws for failures in the queue read, the deserialization, your handler, the delete, or your exception handler. Every failure is logged and passed to your exception handler, and the remaining messages in the batch are still processed. The only exception that escapes is `OperationCanceledException` for your own cancellation token, so a transient storage error cannot take down your host.
- A received message is hidden from other consumers for the *visibility timeout* (30 seconds unless you set `VisibilityTimeout`). If your handler runs longer than that, the message becomes visible again, another consumer can pick it up, and the delete on this side fails and is reported to your exception handler. Set `VisibilityTimeout` longer than your slowest handler multiplied by the number of messages you receive per call.
- A message that can never be processed (for example, invalid JSON) is redelivered every time its visibility timeout expires. Watch for repeated `DeserializationException`s in your exception handler and in the log, which includes the message's dequeue count.
- Automatic deserialization uses the `System.Text.Json` deserialization behavior. This can be overridden by specifying your own `JsonSerializerOptions` as seen below.
- You can 'peek' messages using `PeekMessages<T>` which returns a collection but doesn't remove them from the queue.

## Configuration

1. Add the NuGet package [`AzureStorage.QueueService`](https://www.nuget.org/packages/AzureStorage.QueueService) to your .NET project
2. Set your `ConnectionString` and `QueueName` properties in your [.NET User Secrets store](https://docs.microsoft.com/en-us/aspnet/core/security/app-secrets?view=aspnetcore-6.0&tabs=windows), `appsettings.json`, or anywhere your `IConfiguration` provider can look for the `QueueClientSettings`. For example:

    ```json
    {
        "QueueClientSettings" : {
            "ConnectionString": "[your_connection_string]",
            "QueueName": "[your_queue_name]",
            "CreateIfNotExists": true,
            "VisibilityTimeout": "00:02:00"
        }
    }
    ```

    or set the endpoint URI which inclues the queue name and use a Token Credential during startup:

   ```json
   {
       "QueueClientSettings": {
           "EndpointUri": "https://somestorageaccount.queue.corewindows.net/myqueuename",           
       }
   }
   ```

   ```csharp
   services.AddAzureStorageQueueClient(x => 
    x.AddDefaultClient(y => 
    {
        y.EndpointUri = new Uri(builder.Configuration["SomeKey:EndpointUri"]),
        y.TokenCredential = new DefaultAzureCredential(),
    }));
   ```

4. You can create your queue in advance or allow the library to create it during runtime by setting the `CreateIfNotExists` property to `true`. The queue is created the first time the client is used, not when it is resolved from dependency injection.

### Per-client settings

Every client has its own `QueueClientSettings`:

| Setting | What it does |
|---|---|
| `ConnectionString` + `QueueName` | Connect with a storage account connection string. |
| `EndpointUri` + `TokenCredential` | Connect with an identity instead. The URI includes the queue name. |
| `CreateIfNotExists` | Create the queue on first use if it does not exist. |
| `VisibilityTimeout` | How long a received message is hidden from other consumers while your handler runs. Defaults to 30 seconds. |
| `SerializerOptions` | `JsonSerializerOptions` for this client only, used for both sending and receiving. |
| `ClientOptions` | `QueueClientOptions` for the underlying Azure SDK client: retry policy, diagnostics, and `MessageEncoding`. Set `MessageEncoding = QueueMessageEncoding.Base64` for queues shared with Azure Functions or older SDKs. |

## Configure dependency injection

The library has been updated to handle both a default client and a named-client experience similar to how the .NET `IHttpClientFactory` works. This section outlines both ways you can configure dependency injection to either use the default client or a named client in case you have more than one queue to pull from.

### Add the default client

Use the `AddAzureStorageQueueClient()` method and specify the settings for the `ConnectionString` and the `QueueName` or use the `IConfiguration` binder to bind with a JSON configuration as shown above.

```csharp
services.AddAzureStorageQueueClient(x => 
    x.AddDefaultClient(y => 
    {
        y.ConnectionString = "[your_connection_string]";
        y.QueueName = "[your_queue_name]";    
    }));
```

```csharp
// get configuration from IConfiguration binder
services.AddAzureStorageQueueClient(x => 
    x.AddDefaultClient(y => Configuration.Bind(nameof(QueueClientSettings), y)));
```

### Add a named client

Use the `AddAzureStorageQueueClient()` method with the `AddClient()` method to add and configure different queue clients which can be obtained using the `IQueueClientFactory` and the `GetQueueClient()` method.

```csharp
services.AddAzureStorageQueueClient(x => 
    x.AddClient("MyClient1", y => 
    {
        y.ConnectionString = "[your_connection_string]";
        y.QueueName = "[your_queue_name]";    
    }));
```

```csharp
// get configuration from IConfiguration binder
services.AddAzureStorageQueueClient(x => 
    x.AddClient("MyClient1", y => Configuration.Bind(nameof(QueueClientSettings), y)));

// or bind a configuration section directly
services.AddAzureStorageQueueClient(x => 
    x.AddClient("MyClient1", Configuration.GetSection("Queues:MyClient1")));
```

```csharp
// add multiple named clients and a default client
services.AddAzureStorageQueueClient(x =>
{
    x.AddClient("MyClient1", y => Configuration.Bind(nameof(QueueClientSettings), y));
    x.AddClient("MyClient2", y =>
    {
        y.ConnectionString = "[your_connection_string]";
        y.QueueName = "[your_queue_name]";
    });
    x.AddDefaultClient(y => Configuration.Bind(nameof(DefaultQueueClientSettings), y));
});
```

## Using the IQueueClientFactory

### Example 1: Get a default queue client

```csharp
// inject the IQueueClientFactory and get a default client
public class MyClass
{
    private readonly AzureStorageQueueClient _queueClient;

    public MyClass(IQueueClientFactory queueClientFactory)
    {
        _queueClient = queueClientFactory.GetQueueClient();
    }
}
```

### Example 2: Get a named client

```csharp
// inject the IQueueClientFactory and get a named client
public class MyClass
{
    private readonly AzureStorageQueueClient _queueClient;

    public MyClass(IQueueClientFactory queueClientFactory)
    {
        _queueClient = queueClientFactory.GetQueueClient("MyClient1");
    }
}
```

### Example 3: Inject a client directly

Registered clients are also available from dependency injection without going through the factory. The default client is a plain singleton; named clients are keyed services.

```csharp
public class MyClass
{
    public MyClass(
        IAzureStorageQueueClient defaultClient,
        [FromKeyedServices("MyClient1")] IAzureStorageQueueClient namedClient)
    {
    }
}
```

Depend on `IAzureStorageQueueClient` rather than the concrete `AzureStorageQueueClient` if you want to substitute a fake in your own unit tests.

## Sending messages to an Azure storage account queue

The following example shows the .NET Worker Service template where a `BackgroundService` sends a message every five seconds. Use `BackgroundService.ExecuteAsync` for loops like this rather than `IHostedService.StartAsync`: the host waits for `StartAsync` to return before it finishes starting, so a loop there blocks startup.

1. Inject the `IQueueClientFactory` interface and use as follows:

    ```csharp
    public class Sender : BackgroundService
    {
        private readonly AzureStorageQueueClient _queueClient;
        
        public Sender(IQueueClientFactory queueClientFactory) => _queueClient = queueClientFactory.GetQueueClient();
        
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var myMessage = new MyMessage("Test");
                await _queueClient.SendMessageAsync(myMessage, cancellationToken: stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }    
    }
    ```

## Receiving and handling messages from an Azure storage account queue

The following example shows the .NET Worker Service template where a `BackgroundService` polls the queue repeatedly. Your message handler receives the deserialized payload and the queue's metadata; your exception handler receives anything that went wrong while receiving, deserializing, handling or deleting a message.

`ReceiveMessagesAsync` reports failures through your exception handler instead of throwing, so the loop below keeps running through transient storage errors. The `try`/`catch` is still worth having: it protects you from anything thrown by your own code outside the handlers, and an unhandled exception in `ExecuteAsync` stops the whole host by default in .NET 6 and later.

1. Inject the `IQueueClientFactory` interface and use as follows:

    ```csharp
    public class MySubscriber : BackgroundService
    {
        private readonly AzureStorageQueueClient _queueClient;
        private readonly IMyMessageHandler _myMessageHandler; // your own handler
        private readonly ILogger<MySubscriber> _logger;
    
        public MySubscriber(IQueueClientFactory queueClientFactory, IMyMessageHandler myMessageHandler, ILogger<MySubscriber> logger)
        {
            // get the default queue client
            _queueClient = queueClientFactory.GetQueueClient();
            _myMessageHandler = myMessageHandler;
            _logger = logger;
        }
            
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _queueClient.ReceiveMessagesAsync<MyMessage>(
                        (message, metadata) => _myMessageHandler.HandleAsync(message),
                        (exception, metadata) => _myMessageHandler.HandleExceptionAsync(exception),
                        cancellationToken: stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error while polling the queue.");
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }
    ```

### Handling multiple messages

The library allows you to pull multiple messages by specifying the `numMessages` count in the `ReceiveMessagesAsync<T>()` method (the service allows at most 32). These are sent to the handler one at a time but pulled from the queue as a batch, and every message in the batch is hidden from other consumers for the visibility timeout from the moment the batch is received. Set `VisibilityTimeout` so that it covers the whole batch, otherwise the last messages in the batch may become visible again before you reach them.

```csharp
await _queueClient.ReceiveMessagesAsync<MyMessage>(HandleMessage, HandleException, numMessages: 10, cancellationToken: stoppingToken);
```

## OpenTelemetry Support

The library supports OpenTelemetry logs, traces, and metrics and can be configured using the standard OTEL library as follows:

```csharp
builder.Services.AddOpenTelemetry()
  .WithTracing(t => t.AddAzureStorageQueueTracing())
  .WithMetrics(m => m.AddAzureStorageQueueMetrics());
```

The library inclues two flags to control traces for sending messages and the creation of an Activity (span) for each query to retrieve a message. These two activities are:
- Queue.Read
- Queue.Send

>NOTE: To observe the above spans, you need to configure the library to create them. Be cautious on enabling the `Queue.Read` span though as it creates a lot of data in your telemetry system; particularly if you read with a high frequency (i.e. every 1 second, etc.).

```csharp
builder.Services.ConfigureQueueServiceTelemetry(t =>
{
  t.CreateNewActivityOnMessageRetrieval = true; // Queue.Read trace
  t.CreateNewActivityOnMessageSend = true; // Queue.Send trace
});
```

The following metrics have also been configured and read through tools like Azure Monitor or Grafana:
- `queue_messages_received_total`
- `queue_messages_sent_total`
- `queue_messages_processed_total`
- `queue_messages_failed_total`
- `queue_message_processing_duration_seconds` (tagged with `queue.process_success`)
- `queue_message_size_bytes`

All metrics are tagged with `queue.name`, so clients for different queues can be told apart.

## Upgrading to 2.1

No code changes are required, but note these behaviour changes:

- `ReceiveMessagesAsync<T>` no longer throws when the queue read fails, when your exception handler throws, or when a message cannot be deleted. All of these now go to your exception handler (with `null` metadata when the read itself failed) and are logged. If you relied on catching a failed read in your own loop, that catch block will no longer fire.
- A queue configured with `CreateIfNotExists` is created on the first send, receive or peek instead of when the client is resolved from dependency injection, so startup no longer makes a blocking network call.
- Client configuration delegates run when the client is first requested rather than when `AddAzureStorageQueueClient` is called, and registering the same client name twice now adds to its configuration instead of ignoring the second registration.
- Custom `JsonSerializerOptions` are now applied when sending messages as well as when receiving them. If you configured a naming policy, messages you send will change shape accordingly.
- `Azure.Identity` is no longer a dependency of this package. Add it to your own project if you use `DefaultAzureCredential`.
- The package now depends on `Azure.Storage.Queues` 12.28 and `Microsoft.Extensions.*` 10.0. The 10.0 packages still target .NET 8, so no framework change is needed, but any `Microsoft.Extensions.*` packages you reference directly will be raised to 10.0 as well. This floor comes from the Azure SDK itself (`Azure.Core` 1.60 requires it), so it applies to any project on the current Azure SDK.

## License

This project is licensed under the MIT License - see the [LICENSE.md](license.md) file for details.

The logo is the Unifinity Software Corporation brand mark, taken unchanged from the Unifinity brand kit. It is not covered by the MIT licence.
