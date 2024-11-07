using AzureStorage.QueueService;
using AzureStorage.QueueService.Tests.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAzureStorageQueueClient(x => x.AddDefaultClient(settings =>
{
    settings.ConnectionString = builder.Configuration["Storage:ConnectionString"];
    settings.QueueName = builder.Configuration["Storage:QueueName"];
    settings.CreateIfNotExists = true;
}));

var app = builder.Build();

app.UseHttpsRedirection();

app.MapEndpoints();

app.Run();

public partial class Program
{ }
