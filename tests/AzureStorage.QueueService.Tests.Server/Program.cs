using Azure.Identity;
using AzureStorage.QueueService;
using AzureStorage.QueueService.Tests.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAzureStorageQueueClient(x => x.AddDefaultClient(settings =>
{
    settings.TokenCredential = new DefaultAzureCredential();
    settings.EndpointUri = new Uri(builder.Configuration["Storage:EndpointUri"]);
}));

var app = builder.Build();

app.UseHttpsRedirection();

app.MapEndpoints();

app.Run();

public partial class Program
{ }
