# Azure Storage Queue Service library

[![.NET](https://github.com/jasonshave/JasonShave.AzureStorage.QueueService/actions/workflows/dotnet.yml/badge.svg)](https://github.com/jasonshave/JasonShave.AzureStorage.QueueService/actions/workflows/dotnet.yml)
[![Nuget](https://github.com/jasonshave/JasonShave.AzureStorage.QueueService/actions/workflows/nuget.yml/badge.svg)](https://github.com/jasonshave/JasonShave.AzureStorage.QueueService/actions/workflows/nuget.yml)

This project produces a library which abstracts away the complexities of using the Azure Storage account's "queue" feature targeting .NET 6 and higher.

## Pre-requisites

You will need to create an Azure Storage account in the Azure portal using a unique name, then create a queue, and finally obtain your connection string.

1. [Create a storage account](https://docs.microsoft.com/en-us/azure/storage/common/storage-account-create?tabs=azure-portal) in your Azure portal.
2. [Obtain your connection string](https://docs.microsoft.com/en-us/azure/storage/common/storage-account-keys-manage?tabs=azure-portal) from the Azure portal.

## Message handling behavior

- Sending a message will automatically serialize the payload to `BinaryData`.
- Multiple messages are pulled when `ReceiveMessagesAsync<T>` is called.
- If your handler does not throw, messages are automatically removed from the queue otherwise the message is returned to the queue for delivery again.
- Automatic deserialization uses the `System.Text.Json` deserialization behavior. This can be overridden by specifying your own `JsonSerializerOptions` as seen below.
- You can 'peek' messages using `PeekMessages<T>` which returns a collection but doesn't remove them from the queue.

## Configuration

1. Add the Nuget package `JasonShave.AzureStorage.QueueService` to your .NET project
2. Set your `ConnectionString` and `QueueName` properties in your [.NET User Secrets store](https://docs.microsoft.com/en-us/aspnet/core/security/app-secrets?view=aspnetcore-6.0&tabs=windows), `appsettings.json`, or anywhere your `IConfiguration` provider can look for the `QueueClientSettings`. For example:

    ```json
    {
        "QueueClientSettings" : {
            "ConnectionString": "[your_connection_string]",
            "QueueName": "[your_queue_name]",
            "CreateIfNotExists": true
        }
    }
    ```

    >NOTE: You can create your queue in advance or allow the library to create it during runtime by setting the `CreateIfNotExists` property to `true`.

3. Configure the library from your `Startup.cs` or `Program.cs` file as follows:

    ```csharp
    // get configuration from IConfiguration binder
    services.AddAzureStorageQueueServices(options => configuration.Bind(nameof(QueueClientSettings), options));

    // optionally customize JsonSerializerOptions
    services.AddAzureStorageQueueServices(
        options => hostContext.Configuration.Bind(nameof(QueueClientSettings), options),
        serializationOptions => serializationOptions.AllowTrailingCommas = true);
    ```

## Sending messages to an Azure storage account queue

The following example shows the .NET "Worker Service" template where the class uses the `IHostedService` interface to run a particular code block repeatedly. The application will send the payload to the queue every five seconds.

1. Inject the `IQueueService` interface and use as follows:

    ```csharp
    public class Sender : IHostedService
    {
        private readonly IQueueService _queueService;
        
        public Sender(IQueueService queueService) => _queueService = queueService;
        
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var myMessage = new MyMessage("Test");
                await _queueService.SendMessageAsync<MyMessage>(myMessage, cancellationToken);
                Task.Delay(5000);
            }
        }    
    }
    ```

## Receiving and handling messages from an Azure storage account queue

The following example shows the .NET "Worker Service" template where the class uses the `IHostedService` interface to run a particular code block repeatedly. The application will receive the payload from the queue repeatedly.

1. Inject the `IQueueService` interface and use as follows:

    ```csharp
    public class Receiver : IHostedService
    {
        private readonly IQueueService _queueService;
        private readonly IMyMessageHandler _myMessageHandler; // see optional handler below
    
        public Receiver(IQueueService queueService, IMyMessageHandler myMessageHandler)
        {
            _queueService = queueService;
            _myMessageHandler = myMessageHandler;
        }
            
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _queueService.ReceiveMessagesAsync<MyMessage>(
                    message => _myMessageHandler.HandleAsync(message),
                    exception => _myMessageHandler.HandleExceptionAsync(exception),
                    cancellationToken);
            }
        }
    }
    ```

2. Create your own message handler (optional)

    The library has a single `ReceiveMessagesAsync<T>` method which takes two function delegates which allow you to specify your own message handling and exception handling. The first one handles the `<T>` message type you specify, and the second handles an `Exception` type. These can be implemented as follows:

    ```csharp
    public interface IMyMessageHandler
    {
        Task HandleAsync(MyMessage message);
        Task HandleExceptionAsync(Exception exception);
    }

    public class MyMessageHandler : IMyMessageHandler
    {
        public async Task HandleAsync(MyMessage message) => Console.WriteLine(message);
        public async Task HandleExceptionAsync(Exception exception) => Console.WriteLine(exception);
    }
    ```

## License

This project is licensed under the MIT License - see the [LICENSE.md](license.md) file for details.
