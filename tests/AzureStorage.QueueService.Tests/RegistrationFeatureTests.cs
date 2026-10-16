using Azure.Identity;
using Azure.Storage.Queues;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AzureStorage.QueueService.Tests;

/// <summary>3.1 registration: one storage account for every client, configuration binding, and clear errors for a client nobody registered.</summary>
public class RegistrationFeatureTests
{
    private sealed class Handler : IQueueMessageHandler<TestObject>
    {
        public ValueTask HandleAsync(QueueMessage<TestObject> message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private static QueueClientSettings Settings(IServiceProvider provider, string name) =>
        provider.GetRequiredService<IOptionsMonitor<QueueClientSettings>>().Get(name);

    [Fact(DisplayName = "UseStorageAccount connects clients that only name their queue: a connection string, or an account endpoint with the shared credential")]
    public void UseStorageAccount()
    {
        var credential = new DefaultAzureCredential();
        var services = new ServiceCollection();
        services.AddAzureStorageQueueClient(queues =>
        {
            queues.AddClient("orders", s => s.QueueName = "orders");
            queues.AddClient("own", s => { s.ConnectionString = "UseDevelopmentStorage=true"; s.QueueName = "own"; });
            queues.UseStorageAccount("https://acct.queue.core.windows.net/").UseTokenCredential(credential);
        });
        using var provider = services.BuildServiceProvider();

        var orders = Settings(provider, "orders");
        orders.Validate().Should().BeNull();
        orders.QueueUri.Should().Be(new Uri("https://acct.queue.core.windows.net/orders"));
        orders.TokenCredential.Should().BeSameAs(credential);
        orders.EffectivePoisonQueueName.Should().Be("orders-poison");
        QueueClientFactory.ForSiblingQueue(orders, "orders-poison").EndpointUri.Should().Be(new Uri("https://acct.queue.core.windows.net/orders-poison"));
        Settings(provider, "own").EndpointUri.Should().BeNull("a client with its own connection keeps it");

        var withConnectionString = new ServiceCollection();
        withConnectionString.AddAzureStorageQueueClient(queues => queues.UseStorageAccount("UseDevelopmentStorage=true").AddClient("orders", s => s.QueueName = "orders"));
        using var second = withConnectionString.BuildServiceProvider();
        Settings(second, "orders").ConnectionString.Should().Be("UseDevelopmentStorage=true");
    }

    [Theory(DisplayName = "An EndpointUri that is the account's endpoint takes the queue from QueueName, emulator path-style URIs included")]
    [InlineData("https://acct.queue.core.windows.net", "https://acct.queue.core.windows.net/orders")]
    [InlineData("http://127.0.0.1:10001/devstoreaccount1", "http://127.0.0.1:10001/devstoreaccount1/orders")]
    public void Account_Endpoint_With_QueueName(string endpoint, string expected)
    {
        var settings = new QueueClientSettings { EndpointUri = new Uri(endpoint), QueueName = "orders", TokenCredential = new DefaultAzureCredential() };

        settings.Validate().Should().BeNull();
        settings.QueueUri.Should().Be(new Uri(expected));
    }

    [Fact(DisplayName = "Injecting a client nobody registered fails with the factory's message naming the registered clients")]
    public void Unregistered_Clients_Fail_Clearly()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(queues => queues.AddClient("orders", s => { s.ConnectionString = "UseDevelopmentStorage=true"; s.QueueName = "orders"; }));
        using var provider = services.BuildServiceProvider();

        provider.Invoking(p => p.GetRequiredKeyedService<IQueueSender>("ordres")).Should().Throw<InvalidOperationException>().WithMessage("No queue client named 'ordres'*'orders'*");
        provider.Invoking(p => p.GetRequiredService<IQueueReceiver>()).Should().Throw<InvalidOperationException>().WithMessage("No default queue client*'orders'*");
    }

    [Fact(DisplayName = "Processor options and the SDK's ClientOptions bind from configuration")]
    public void Configuration_Binding()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Queues:Orders:QueueName"] = "orders",
            ["Queues:Orders:ConnectionString"] = "UseDevelopmentStorage=true",
            ["Queues:Orders:ClientOptions:MessageEncoding"] = "Base64",
            ["Queues:Orders:PoisonTimeToLive"] = "30.00:00:00",
            ["Queues:Orders:Processor:MaxConcurrentMessages"] = "7",
            ["Queues:Orders:Processor:MaxRenewalDuration"] = "00:10:00",
        }).Build();
        var services = new ServiceCollection();
        services.AddAzureStorageQueueClient(queues => queues
            .AddClient("orders", configuration.GetSection("Queues:Orders"))
            .AddProcessor<TestObject, Handler>(configuration.GetSection("Queues:Orders:Processor"), o => o.MaxDequeueCount = 9));
        using var provider = services.BuildServiceProvider();

        var settings = Settings(provider, "orders");
        settings.ClientOptions!.MessageEncoding.Should().Be(QueueMessageEncoding.Base64);
        settings.PoisonTimeToLive.Should().Be(TimeSpan.FromDays(30));
        var options = provider.GetRequiredService<IOptionsFactory<QueueProcessorOptions>>().Create($"orders|{typeof(Handler).FullName}");
        options.Should().BeEquivalentTo(new { MaxConcurrentMessages = 7, MaxRenewalDuration = TimeSpan.FromMinutes(10), MaxDequeueCount = 9 });
    }
}
