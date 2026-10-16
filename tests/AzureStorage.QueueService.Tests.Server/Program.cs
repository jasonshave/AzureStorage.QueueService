using Azure.Storage.Queues;
using AzureStorage.QueueService;
using AzureStorage.QueueService.Tests.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAzureStorageQueueClient(x =>
{
    x.AddDefaultClient(settings =>
    {
        settings.ConnectionString = builder.Configuration["Storage:ConnectionString"];
        settings.QueueName = builder.Configuration["Storage:QueueName"];
        settings.CreateIfNotExists = true;
    });

    // A queue whose name is as long as the default "-poison" suffix allows (56 + 7 = 63 characters).
    x.AddClient("long", settings =>
    {
        settings.ConnectionString = builder.Configuration["Storage:ConnectionString"];
        settings.QueueName = "integration-" + new string('x', 44);
        settings.CreateIfNotExists = true;
    });

    // A queue that carries Base64-encoded messages, as Azure Functions and older SDKs write them.
    x.AddClient("base64", settings =>
    {
        settings.ConnectionString = builder.Configuration["Storage:ConnectionString"];
        settings.QueueName = "integration-base64-queue";
        settings.CreateIfNotExists = true;
        settings.ClientOptions = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
    });
});

var app = builder.Build();

app.UseHttpsRedirection();

app.MapEndpoints();

app.Run();

public partial class Program
{ }
