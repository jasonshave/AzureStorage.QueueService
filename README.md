# Azure Storage Queue Service library

[![.NET](https://github.com/jasonshave/JasonShave.AzureStorage.QueueService/actions/workflows/dotnet.yml/badge.svg)](https://github.com/jasonshave/JasonShave.AzureStorage.QueueService/actions/workflows/dotnet.yml)
[![Nuget](https://github.com/jasonshave/JasonShave.AzureStorage.QueueService/actions/workflows/nuget.yml/badge.svg)](https://github.com/jasonshave/JasonShave.AzureStorage.QueueService/actions/workflows/nuget.yml)

This project produces a library which abstracts away the complexities of using the Azure Storage account's "queue" feature targeting .NET 6 and higher.

## Pre-requisites

You will need to create an Azure Storage account in the Azure portal using a unique name, then create a queue, and finally obtain your connection string.

1. [Create a storage account](https://docs.microsoft.com/en-us/azure/storage/common/storage-account-create?tabs=azure-portal) in your Azure portal.
2. [Obtain your connection string](https://docs.microsoft.com/en-us/azure/storage/common/storage-account-keys-manage?tabs=azure-portal) from the Azure portal.

## Usage

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
    // get QueueClientSettings using this method
    services.AddAzureStorageQueueServices(options =>
    {
        options.ConnectionString = configuration["QueueClientSettings:ConnectionString"];
        options.QueueName = configuration["QueueClientSettings:QueueName"];
        options.CreateIfNotExists = true;
    });
    
    // or use the IConfiguration binder
    services.AddAzureStorageQueueServices(options => configuration.Bind(nameof(QueueClientSettings), options));
    ```

4. Inject the `IQueueService` interface and use as follows:

    ```csharp
    public class Worker : IHostedService
    {
        private readonly IQueueService _queueService;
        private readonly IMyMessageHandler _myMessageHandler;
    
        public Worker(IQueueService queueService, IMyMessageHandler myMessageHandler)
        {
            _queueService = queueService;
            _myMessageHandler = myMessageHandler;
        }
            
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _queueService.ReceiveMessagesAsync<MyMessage>(
                    message => _eventGridMessageHandler.HandleAsync(message),
                    exception => _eventGridMessageHandler.HandleAsync(exception),
                    cancellationToken);
            }
        }
    }
    ```

## License

This project is licensed under the MIT License - see the [LICENSE.md](license.md) file for details.
