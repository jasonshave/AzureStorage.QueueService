using System.Diagnostics.Metrics;
using Azure;
using Azure.Identity;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using AzureStorage.QueueService.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Moq;

namespace AzureStorage.QueueService.Tests;

public class QueueClientFactoryTests
{
    private const string ConnectionString = "UseDevelopmentStorage=true";

    /// <summary>
    /// A service collection whose SDK client construction is stubbed out so no settings validation or network
    /// access happens. Every call to the SDK factory is recorded in <c>created</c>.
    /// </summary>
    private static (IServiceCollection Services, List<(string Description, QueueClientSettings Settings)> Created) CreateServices()
    {
        var created = new List<(string, QueueClientSettings)>();
        var sdkClients = new Mock<ISdkQueueClientFactory>();
        sdkClients.Setup(x => x.CreateClient(It.IsAny<string>(), It.IsAny<QueueClientSettings>()))
            .Returns((string description, QueueClientSettings settings) =>
            {
                created.Add((description, settings));
                var client = new Mock<QueueClient>();
                // The SDK derives the name from the last URI segment, ignoring a trailing slash.
                client.Setup(x => x.Name).Returns(settings.QueueName ?? settings.EndpointUri?.AbsolutePath.TrimEnd('/').Split('/').Last() ?? "queue");
                // Dead-lettering sends to the poison queue, which reads the receipt the service returns.
                client.Setup(x => x.SendMessageAsync(It.IsAny<BinaryData>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Response.FromValue(QueuesModelFactory.SendReceipt("1", DateTimeOffset.Now, DateTimeOffset.Now, "r", DateTimeOffset.Now), Mock.Of<Response>()));
                return client.Object;
            });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(sdkClients.Object); // registered first so the library's TryAdd keeps it

        return (services, created);
    }

    private static IQueueClientFactory Factory(IServiceCollection services) =>
        services.BuildServiceProvider().GetRequiredService<IQueueClientFactory>();

    private static ValueTask DeadLetter(IStorageQueue queue) =>
        ((IQueueMessageOperations)queue).DeadLetterAsync("1", "r", 5, BinaryData.FromString("{}"), null, CancellationToken.None);

    [Fact]
    public void GetQueue_ReturnsSameInstanceEachTime()
    {
        var (services, created) = CreateServices();
        services.AddAzureStorageQueueClient(x => x.AddClient("foo", y => { y.ConnectionString = ConnectionString; y.QueueName = "foo-queue"; }));
        var factory = Factory(services);

        factory.GetQueue("foo").Should().BeSameAs(factory.GetQueue("foo"));
        factory.GetQueue("foo").QueueName.Should().Be("foo-queue");
        created.Should().ContainSingle().Which.Settings.QueueName.Should().Be("foo-queue");
    }

    [Fact]
    public void RegisterMultipleNamedClients_EachGetsItsOwnSettings()
    {
        var (services, created) = CreateServices();
        services.AddAzureStorageQueueClient(x =>
        {
            x.AddClient("foo", y => { y.ConnectionString = ConnectionString; y.QueueName = "foo-queue"; });
            x.AddClient("bar", y => { y.ConnectionString = ConnectionString; y.QueueName = "bar-queue"; });
        });
        var factory = Factory(services);

        factory.GetQueue("foo").Should().NotBeSameAs(factory.GetQueue("bar"));
        created.Select(c => c.Settings.QueueName).Should().BeEquivalentTo("foo-queue", "bar-queue");
    }

    [Fact]
    public void AddClientTwiceForSameName_ComposesConfiguration()
    {
        var (services, created) = CreateServices();
        services.AddAzureStorageQueueClient(x => x.AddClient("foo", y => y.ConnectionString = ConnectionString));
        services.AddAzureStorageQueueClient(x => x.AddClient("foo", y => y.QueueName = "foo-queue"));

        Factory(services).GetQueue("foo");

        var settings = created.Should().ContainSingle().Which.Settings;
        settings.ConnectionString.Should().Be(ConnectionString);
        settings.QueueName.Should().Be("foo-queue");
    }

    [Fact]
    public void SeparateServiceCollections_DoNotShareSettings()
    {
        var (servicesA, createdA) = CreateServices();
        var (servicesB, createdB) = CreateServices();
        servicesA.AddAzureStorageQueueClient(x => x.AddDefaultClient(y => { y.ConnectionString = ConnectionString; y.QueueName = "queue-a"; }));
        servicesB.AddAzureStorageQueueClient(x => x.AddDefaultClient(y => { y.ConnectionString = ConnectionString; y.QueueName = "queue-b"; }));

        Factory(servicesA).GetQueue();
        Factory(servicesB).GetQueue();

        createdA.Should().ContainSingle().Which.Settings.QueueName.Should().Be("queue-a");
        createdB.Should().ContainSingle().Which.Settings.QueueName.Should().Be("queue-b");
    }

    [Fact]
    public void GetUnregisteredClient_ThrowsNamingTheRegisteredClients()
    {
        var (services, _) = CreateServices();
        services.AddAzureStorageQueueClient(x => x.AddClient("foo", y => y.ConnectionString = ConnectionString));

        Factory(services).Invoking(x => x.GetQueue("bar")).Should().Throw<InvalidOperationException>()
            .WithMessage("*'bar'*").WithMessage("*'foo'*");
    }

    [Fact]
    public void GetDefaultClient_WhenNoneRegistered_Throws()
    {
        var (services, _) = CreateServices();
        services.AddAzureStorageQueueClient(x => x.AddClient("foo", y => y.ConnectionString = ConnectionString));

        Factory(services).Invoking(x => x.GetQueue()).Should().Throw<InvalidOperationException>().WithMessage("*AddDefaultClient*");
    }

    [Fact]
    public void Queues_CanBeInjectedDirectly()
    {
        var (services, _) = CreateServices();
        services.AddAzureStorageQueueClient(x =>
        {
            x.AddClient("foo", y => { y.ConnectionString = ConnectionString; y.QueueName = "foo-queue"; });
            x.AddDefaultClient(y => { y.ConnectionString = ConnectionString; y.QueueName = "default-queue"; });
        });
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IQueueClientFactory>();

        var foo = factory.GetQueue("foo");
        provider.GetRequiredKeyedService<IStorageQueue>("foo").Should().BeSameAs(foo);
        provider.GetRequiredKeyedService<IQueueSender>("foo").Should().BeSameAs(foo);
        provider.GetRequiredKeyedService<IQueueReceiver>("foo").Should().BeSameAs(foo);

        var @default = factory.GetQueue();
        provider.GetRequiredService<IStorageQueue>().Should().BeSameAs(@default);
        provider.GetRequiredService<IQueueSender>().Should().BeSameAs(@default);
        provider.GetRequiredService<IQueueReceiver>().Should().BeSameAs(@default);
    }

    [Fact]
    public void AddClient_FromConfigurationSection_BindsSettings()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Queues:Foo:ConnectionString"] = ConnectionString,
            ["Queues:Foo:QueueName"] = "foo-queue",
            ["Queues:Foo:VisibilityTimeout"] = "00:05:00",
            ["Queues:Foo:PoisonQueueName"] = "foo-dead",
        }).Build();
        var (services, created) = CreateServices();
        services.AddAzureStorageQueueClient(x => x.AddClient("foo", configuration.GetSection("Queues:Foo")));

        Factory(services).GetQueue("foo");

        var settings = created.Should().ContainSingle().Which.Settings;
        settings.QueueName.Should().Be("foo-queue");
        settings.VisibilityTimeout.Should().Be(TimeSpan.FromMinutes(5));
        settings.PoisonQueueName.Should().Be("foo-dead");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Poison queue (F6)
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PoisonQueue_UsesTheSameAccount_WithConnectionString()
    {
        var (services, created) = CreateServices();
        services.AddAzureStorageQueueClient(x => x.AddClient("orders", y => { y.ConnectionString = ConnectionString; y.QueueName = "orders"; }));

        await DeadLetter(Factory(services).GetQueue("orders"));

        created.Should().HaveCount(2);
        created[1].Description.Should().Contain("poison");
        created[1].Settings.ConnectionString.Should().Be(ConnectionString);
        created[1].Settings.QueueName.Should().Be("orders-poison");
    }

    [Theory]
    [InlineData("https://account.queue.core.windows.net/orders")]
    [InlineData("https://account.queue.core.windows.net/orders/")]
    public async Task PoisonQueue_UsesTheSameAccount_WithEndpointUri(string endpoint)
    {
        var (services, created) = CreateServices();
        var credential = new DefaultAzureCredential();
        services.AddAzureStorageQueueClient(x => x.AddClient("orders", y =>
        {
            y.EndpointUri = new Uri(endpoint);
            y.TokenCredential = credential;
        }));

        await DeadLetter(Factory(services).GetQueue("orders"));

        created[1].Settings.EndpointUri.Should().Be(new Uri("https://account.queue.core.windows.net/orders-poison"));
        created[1].Settings.TokenCredential.Should().BeSameAs(credential);
    }

    [Fact]
    public void SiblingQueueUri_KeepsTheQueryString()
    {
        QueueClientFactory.SiblingQueueUri(new Uri("https://account.queue.core.windows.net/orders?sv=2024&sig=abc"), "orders-poison")
            .Should().Be(new Uri("https://account.queue.core.windows.net/orders-poison?sv=2024&sig=abc"));
    }

    [Fact]
    public async Task PoisonQueue_ExplicitName_IsUsed()
    {
        var (services, created) = CreateServices();
        services.AddAzureStorageQueueClient(x => x.AddClient("orders", y => { y.ConnectionString = ConnectionString; y.QueueName = "orders"; y.PoisonQueueName = "dead-letters"; }));

        await DeadLetter(Factory(services).GetQueue("orders"));

        created[1].Settings.QueueName.Should().Be("dead-letters");
    }

    [Fact]
    public void PoisonQueue_DerivedNameTooLong_FailsClearly()
    {
        var longName = new string('a', 57); // valid itself; "-poison" makes it 64
        var (services, _) = CreateServices();
        services.AddAzureStorageQueueClient(x => x.AddClient("orders", y => { y.ConnectionString = ConnectionString; y.QueueName = longName; }));

        Factory(services).Invoking(f => f.GetQueue("orders")).Should().Throw<InvalidOperationException>()
            .WithMessage("*poison*").WithMessage("*PoisonQueueName*");
    }

    [Fact]
    public void PoisonQueue_SameAsSource_IsRejected_WithConnectionString()
    {
        var (services, _) = CreateServices();
        services.AddAzureStorageQueueClient(x => x.AddClient("orders", y => { y.ConnectionString = ConnectionString; y.QueueName = "orders"; y.PoisonQueueName = "orders"; }));

        Factory(services).Invoking(f => f.GetQueue("orders")).Should().Throw<InvalidOperationException>().WithMessage("*equal to its own queue name*");
    }

    [Fact]
    public void PoisonQueue_SameAsSource_IsRejected_WithEndpointUri()
    {
        var (services, _) = CreateServices();
        services.AddAzureStorageQueueClient(x => x.AddClient("orders", y =>
        {
            y.EndpointUri = new Uri("https://account.queue.core.windows.net/orders/");
            y.TokenCredential = new DefaultAzureCredential();
            y.PoisonQueueName = "orders";
        }));

        Factory(services).Invoking(f => f.GetQueue("orders")).Should().Throw<InvalidOperationException>().WithMessage("*equal to its own queue name*");
    }

    [Fact]
    public void ConcurrentFirstCalls_CreateOneClient()
    {
        var (services, created) = CreateServices();
        services.AddAzureStorageQueueClient(x => x.AddClient("foo", y => { y.ConnectionString = ConnectionString; y.QueueName = "foo-queue"; }));
        var factory = Factory(services);

        var queues = Enumerable.Range(0, 32).AsParallel().Select(_ => factory.GetQueue("foo")).ToList();

        created.Should().ContainSingle();
        queues.Distinct().Should().ContainSingle();
    }

    [Fact(DisplayName = "Metrics go through the host's IMeterFactory, so a MetricCollector scoped to it sees them, with exactly their 3.0 tags")]
    public async Task Metrics_Use_The_Hosts_Meter_Factory()
    {
        var client = new Mock<QueueClient>();
        client.Setup(x => x.Name).Returns("orders");
        client.Setup(x => x.SendMessageAsync(It.IsAny<BinaryData>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(QueuesModelFactory.SendReceipt("1", DateTimeOffset.Now, DateTimeOffset.Now, "r", DateTimeOffset.Now), Mock.Of<Response>()));
        var sdkClients = new Mock<ISdkQueueClientFactory>();
        sdkClients.Setup(x => x.CreateClient(It.IsAny<string>(), It.IsAny<QueueClientSettings>())).Returns(client.Object);
        var services = new ServiceCollection().AddLogging().AddMetrics().AddSingleton(sdkClients.Object);
        services.AddAzureStorageQueueClient(x => x.AddDefaultClient(y => { y.ConnectionString = ConnectionString; y.QueueName = "orders"; }));
        await using var provider = services.BuildServiceProvider();

        // Scoped to this container's factory: measurements from the library-owned fallback meter, or another test's host, are not seen.
        using var sent = new MetricCollector<long>(provider.GetRequiredService<IMeterFactory>(), QueueServiceDiagnostics.ServiceName, "queue_messages_sent_total");
        await provider.GetRequiredService<IQueueClientFactory>().GetQueue().SendAsync(new TestObject());

        var measurement = sent.GetMeasurementSnapshot().Should().ContainSingle().Subject;
        measurement.Value.Should().Be(1);
        // Nothing added: a new label would start a new series and break continuity with stored history.
        measurement.Tags.Should().Equal(new Dictionary<string, object?> { ["queue.name"] = "orders" });
    }
}
