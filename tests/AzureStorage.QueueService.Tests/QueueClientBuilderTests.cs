using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AzureStorage.QueueService.Tests;

public class QueueClientBuilderTests
{
    private sealed class OrderHandler : IQueueMessageHandler<TestObject>
    {
        public ValueTask HandleAsync(QueueMessage<TestObject> message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Fact]
    public void AddClient_ReturnsBuilderForThatClient()
    {
        var services = new ServiceCollection();
        IQueueClientBuilder? builder = null;

        services.AddAzureStorageQueueClient(x => builder = x.AddClient("orders", y => y.ConnectionString = "UseDevelopmentStorage=true"));

        builder!.Name.Should().Be("orders");
        builder.Services.Should().BeSameAs(services);
    }

    [Fact]
    public void AddDefaultClient_ReturnsBuilderWithDefaultName()
    {
        var services = new ServiceCollection();
        IQueueClientBuilder? builder = null;

        services.AddAzureStorageQueueClient(x => builder = x.AddDefaultClient(y => y.ConnectionString = "UseDevelopmentStorage=true"));

        builder!.Name.Should().Be(Options.DefaultName);
    }

    [Fact]
    public void UseJsonSerializer_SetsAPerClientSerializer()
    {
        var services = new ServiceCollection();
        services.AddAzureStorageQueueClient(x =>
            x.AddClient("orders", y => y.ConnectionString = "UseDevelopmentStorage=true")
             .UseJsonSerializer(new JsonSerializerOptions()));

        var settings = services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<QueueClientSettings>>().Get("orders");

        // The options are used as given: System.Text.Json's plain defaults write PascalCase, unlike the library's default.
        settings.Serializer.Should().NotBeNull();
        settings.Serializer!.Serialize(new TestObject { Property1 = "v" }).ToString().Should().Contain("\"Property1\"");
    }

    [Fact]
    public void Configure_RunsAfterTheRegistrationDelegate()
    {
        var services = new ServiceCollection();
        services.AddAzureStorageQueueClient(x =>
            x.AddClient("orders", y => y.QueueName = "first").Configure(y => y.QueueName = "second"));

        services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<QueueClientSettings>>().Get("orders").QueueName.Should().Be("second");
    }

    [Fact]
    public void AddProcessor_RegistersAHostedServicePerCall_AndAScopedHandler()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(x =>
        {
            x.AddClient("orders", y => y.ConnectionString = "UseDevelopmentStorage=true").AddProcessor<TestObject, OrderHandler>();
            x.AddClient("returns", y => y.ConnectionString = "UseDevelopmentStorage=true").AddProcessor<TestObject, OrderHandler>(o => o.MaxConcurrentMessages = 3);
        });
        var provider = services.BuildServiceProvider();

        provider.GetServices<IHostedService>().Should().HaveCount(2).And.AllBeOfType<QueueProcessor<TestObject, OrderHandler>>();
        services.Should().Contain(d => d.ServiceType == typeof(OrderHandler) && d.Lifetime == ServiceLifetime.Scoped);

        var options = provider.GetRequiredService<IOptionsMonitor<QueueProcessorOptions>>();
        options.Get($"returns|{typeof(OrderHandler).FullName}").MaxConcurrentMessages.Should().Be(3);
        options.Get($"orders|{typeof(OrderHandler).FullName}").MaxConcurrentMessages.Should().Be(1);
    }

    [Fact]
    public async Task ValidateOnStart_FailsHostStartup_WhenAClientIsNotConfigured()
    {
        using var host = Host.CreateDefaultBuilder().ConfigureServices(services =>
            services.AddAzureStorageQueueClient(x =>
            {
                x.AddClient("orders", y => y.QueueName = "orders"); // no connection string or endpoint
                x.ValidateOnStart();
            })).Build();

        await host.Invoking(h => h.StartAsync()).Should().ThrowAsync<OptionsValidationException>().WithMessage("*'orders'*");
    }

    [Fact]
    public async Task ValidateOnStart_AppliesToClientsRegisteredAfterIt()
    {
        using var host = Host.CreateDefaultBuilder().ConfigureServices(services =>
            services.AddAzureStorageQueueClient(x =>
            {
                x.ValidateOnStart();
                x.AddDefaultClient(y => y.QueueName = "orders");
            })).Build();

        await host.Invoking(h => h.StartAsync()).Should().ThrowAsync<OptionsValidationException>().WithMessage("*default*");
    }

    [Fact]
    public async Task ValidateOnStart_StillRuns_WhenTheAppRegisteredItsOwnValidator()
    {
        using var host = Host.CreateDefaultBuilder().ConfigureServices(services =>
        {
            services.AddOptions<QueueClientSettings>("orders").Validate(_ => true); // registers another IValidateOptions<QueueClientSettings>
            services.AddAzureStorageQueueClient(x =>
            {
                x.AddClient("orders", y => y.QueueName = "orders");
                x.ValidateOnStart();
            });
        }).Build();

        await host.Invoking(h => h.StartAsync()).Should().ThrowAsync<OptionsValidationException>().WithMessage("*'orders'*");
    }

    [Fact]
    public async Task ValidateOnStart_FailsHostStartup_WhenThePoisonQueueNameIsInvalid()
    {
        using var host = Host.CreateDefaultBuilder().ConfigureServices(services =>
            services.AddAzureStorageQueueClient(x =>
            {
                x.AddClient("orders", y => { y.ConnectionString = "UseDevelopmentStorage=true"; y.QueueName = "orders"; y.PoisonQueueName = "Bad_Name"; });
                x.ValidateOnStart();
            })).Build();

        await host.Invoking(h => h.StartAsync()).Should().ThrowAsync<OptionsValidationException>().WithMessage("*PoisonQueueName*");
    }

    [Fact]
    public async Task ValidateOnStart_FailsHostStartup_WhenTheDerivedPoisonQueueNameIsTooLong()
    {
        // R6: a 60-character queue name is valid, but "<name>-poison" is 67; a send-only host must find out at startup.
        using var host = Host.CreateDefaultBuilder().ConfigureServices(services =>
            services.AddAzureStorageQueueClient(x =>
            {
                x.AddClient("orders", y => { y.ConnectionString = "UseDevelopmentStorage=true"; y.QueueName = new string('a', 60); });
                x.ValidateOnStart();
            })).Build();

        await host.Invoking(h => h.StartAsync()).Should().ThrowAsync<OptionsValidationException>().WithMessage("*poison*").WithMessage("*PoisonQueueName*");
    }

    [Fact]
    public async Task ValidateOnStart_FailsHostStartup_WhenThePoisonQueueIsTheSourceQueue()
    {
        using var host = Host.CreateDefaultBuilder().ConfigureServices(services =>
            services.AddAzureStorageQueueClient(x =>
            {
                x.AddDefaultClient(y => { y.EndpointUri = new Uri("https://account.queue.core.windows.net/orders"); y.TokenCredential = new Azure.Identity.DefaultAzureCredential(); y.PoisonQueueName = "orders"; });
                x.ValidateOnStart();
            })).Build();

        await host.Invoking(h => h.StartAsync()).Should().ThrowAsync<OptionsValidationException>().WithMessage("*equal to its own queue name*");
    }

    [Fact]
    public async Task ValidateOnStart_PassesForAConfiguredClient()
    {
        using var host = Host.CreateDefaultBuilder().ConfigureServices(services =>
            services.AddAzureStorageQueueClient(x =>
            {
                x.AddClient("orders", y => { y.ConnectionString = "UseDevelopmentStorage=true"; y.QueueName = "orders"; });
                x.ValidateOnStart();
            })).Build();

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task UseTokenCredential_LetsEndpointOnlyClientsPassValidateOnStartAndResolve()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Queues:Orders:EndpointUri"] = "https://account.queue.core.windows.net/orders" })
            .Build();
        var credential = new Azure.Identity.DefaultAzureCredential();

        // One client registered before the call and one after, so both directions are covered.
        using var host = Host.CreateDefaultBuilder().ConfigureServices(services =>
            services.AddAzureStorageQueueClient(x =>
            {
                x.AddClient("orders", configuration.GetSection("Queues:Orders"));
                x.UseTokenCredential(credential).ValidateOnStart();
                x.AddDefaultClient(y => y.EndpointUri = new Uri("https://account.queue.core.windows.net/notifications"));
            })).Build();

        await host.StartAsync();

        var factory = host.Services.GetRequiredService<IQueueClientFactory>();
        factory.GetQueue("orders").Should().NotBeNull();
        factory.GetQueue().Should().NotBeNull();
        host.Services.GetRequiredService<IOptionsMonitor<QueueClientSettings>>().Get("orders").TokenCredential.Should().BeSameAs(credential);

        await host.StopAsync();
    }

    [Fact]
    public void UseTokenCredential_LeavesAClientsOwnCredentialAndConnectionStringClientsAlone()
    {
        var own = new Azure.Identity.DefaultAzureCredential();
        var shared = new Azure.Identity.DefaultAzureCredential();
        var services = new ServiceCollection();
        services.AddAzureStorageQueueClient(x =>
        {
            x.UseTokenCredential(shared);
            x.AddClient("own", y => { y.EndpointUri = new Uri("https://account.queue.core.windows.net/own"); y.TokenCredential = own; });
            x.AddClient("connection", y => { y.ConnectionString = "UseDevelopmentStorage=true"; y.QueueName = "connection"; });
        });

        var settings = services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<QueueClientSettings>>();

        settings.Get("own").TokenCredential.Should().BeSameAs(own);
        settings.Get("connection").TokenCredential.Should().BeNull();
    }
}
