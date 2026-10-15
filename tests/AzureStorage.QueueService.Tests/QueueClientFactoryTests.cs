using Azure.Storage.Queues;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace AzureStorage.QueueService.Tests;

public class QueueClientFactoryTests
{
    private const string ConnectionString = "UseDevelopmentStorage=true";

    /// <summary>
    /// A service collection whose SDK client construction is stubbed out so no settings validation or network
    /// access happens. Every call to the builder is recorded in <c>created</c>.
    /// </summary>
    private static (IServiceCollection Services, List<(string Description, QueueClientSettings Settings)> Created) CreateServices()
    {
        var created = new List<(string, QueueClientSettings)>();
        var builder = new Mock<IQueueClientBuilder>();
        builder.Setup(x => x.CreateClient(It.IsAny<string>(), It.IsAny<QueueClientSettings>()))
            .Returns((string description, QueueClientSettings settings) =>
            {
                created.Add((description, settings));
                return new Mock<QueueClient>().Object;
            });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(builder.Object); // registered first so the library's TryAdd keeps it

        return (services, created);
    }

    [Fact]
    public void GetNamedClient_ReturnsSameClientEachTime()
    {
        // arrange
        var (services, created) = CreateServices();
        services.AddAzureStorageQueueClient(x => x.AddClient("foo", y => { y.ConnectionString = ConnectionString; y.QueueName = "foo-queue"; }));
        var factory = services.BuildServiceProvider().GetRequiredService<IQueueClientFactory>();

        // act
        var client1 = factory.GetQueueClient("foo");
        var client2 = factory.GetQueueClient("foo");

        // assert
        client1.Should().BeSameAs(client2);
        created.Should().ContainSingle().Which.Settings.QueueName.Should().Be("foo-queue");
    }

    [Fact]
    public void RegisterMultipleNamedClients_EachGetsItsOwnSettings()
    {
        // arrange
        var (services, created) = CreateServices();
        services.AddAzureStorageQueueClient(x =>
        {
            x.AddClient("foo", y => { y.ConnectionString = ConnectionString; y.QueueName = "foo-queue"; });
            x.AddClient("bar", y => { y.ConnectionString = ConnectionString; y.QueueName = "bar-queue"; });
        });
        var factory = services.BuildServiceProvider().GetRequiredService<IQueueClientFactory>();

        // act
        var foo = factory.GetQueueClient("foo");
        var bar = factory.GetQueueClient("bar");

        // assert
        foo.Should().NotBeSameAs(bar);
        created.Select(c => c.Settings.QueueName).Should().BeEquivalentTo("foo-queue", "bar-queue");
    }

    [Fact]
    public void AddClientTwiceForSameName_ComposesConfiguration()
    {
        // arrange
        var (services, created) = CreateServices();
        services.AddAzureStorageQueueClient(x => x.AddClient("foo", y => y.ConnectionString = ConnectionString));
        services.AddAzureStorageQueueClient(x => x.AddClient("foo", y => y.QueueName = "foo-queue"));
        var factory = services.BuildServiceProvider().GetRequiredService<IQueueClientFactory>();

        // act
        factory.GetQueueClient("foo");

        // assert
        var settings = created.Should().ContainSingle().Which.Settings;
        settings.ConnectionString.Should().Be(ConnectionString);
        settings.QueueName.Should().Be("foo-queue");
    }

    [Fact]
    public void SeparateServiceCollections_DoNotShareSettings()
    {
        // arrange
        var (servicesA, createdA) = CreateServices();
        var (servicesB, createdB) = CreateServices();
        servicesA.AddAzureStorageQueueClient(x => x.AddDefaultClient(y => { y.ConnectionString = ConnectionString; y.QueueName = "queue-a"; }));
        servicesB.AddAzureStorageQueueClient(x => x.AddDefaultClient(y => { y.ConnectionString = ConnectionString; y.QueueName = "queue-b"; }));

        // act
        servicesA.BuildServiceProvider().GetRequiredService<IQueueClientFactory>().GetQueueClient();
        servicesB.BuildServiceProvider().GetRequiredService<IQueueClientFactory>().GetQueueClient();

        // assert
        createdA.Should().ContainSingle().Which.Settings.QueueName.Should().Be("queue-a");
        createdB.Should().ContainSingle().Which.Settings.QueueName.Should().Be("queue-b");
    }

    [Fact]
    public void GetUnregisteredClient_ThrowsNamingTheRegisteredClients()
    {
        // arrange
        var (services, _) = CreateServices();
        services.AddAzureStorageQueueClient(x => x.AddClient("foo", y => y.ConnectionString = ConnectionString));
        var factory = services.BuildServiceProvider().GetRequiredService<IQueueClientFactory>();

        // act/assert
        factory.Invoking(x => x.GetQueueClient("bar")).Should().Throw<ApplicationException>()
            .WithMessage("*'bar'*").WithMessage("*'foo'*");
    }

    [Fact]
    public void GetDefaultClient_WhenNoneRegistered_Throws()
    {
        // arrange
        var (services, _) = CreateServices();
        services.AddAzureStorageQueueClient(x => x.AddClient("foo", y => y.ConnectionString = ConnectionString));
        var factory = services.BuildServiceProvider().GetRequiredService<IQueueClientFactory>();

        // act/assert
        factory.Invoking(x => x.GetQueueClient()).Should().Throw<ApplicationException>().WithMessage("*AddDefaultClient*");
    }

    [Fact]
    public void Clients_CanBeInjectedDirectly()
    {
        // arrange
        var (services, _) = CreateServices();
        services.AddAzureStorageQueueClient(x =>
        {
            x.AddClient("foo", y => { y.ConnectionString = ConnectionString; y.QueueName = "foo-queue"; });
            x.AddDefaultClient(y => { y.ConnectionString = ConnectionString; y.QueueName = "default-queue"; });
        });
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IQueueClientFactory>();

        // act/assert
        provider.GetRequiredKeyedService<AzureStorageQueueClient>("foo").Should().BeSameAs(factory.GetQueueClient("foo"));
        provider.GetRequiredKeyedService<IAzureStorageQueueClient>("foo").Should().BeSameAs(factory.GetQueueClient("foo"));
        provider.GetRequiredService<AzureStorageQueueClient>().Should().BeSameAs(factory.GetQueueClient());
        provider.GetRequiredService<IAzureStorageQueueClient>().Should().BeSameAs(factory.GetQueueClient());
    }

    [Fact]
    public void AddClient_FromConfigurationSection_BindsSettings()
    {
        // arrange
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Queues:Foo:ConnectionString"] = ConnectionString,
            ["Queues:Foo:QueueName"] = "foo-queue",
            ["Queues:Foo:VisibilityTimeout"] = "00:05:00",
        }).Build();
        var (services, created) = CreateServices();
        services.AddAzureStorageQueueClient(x => x.AddClient("foo", configuration.GetSection("Queues:Foo")));
        var factory = services.BuildServiceProvider().GetRequiredService<IQueueClientFactory>();

        // act
        factory.GetQueueClient("foo");

        // assert
        var settings = created.Should().ContainSingle().Which.Settings;
        settings.QueueName.Should().Be("foo-queue");
        settings.VisibilityTimeout.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void ConcurrentFirstCalls_CreateOneClient()
    {
        // arrange
        var (services, created) = CreateServices();
        services.AddAzureStorageQueueClient(x => x.AddClient("foo", y => y.ConnectionString = ConnectionString));
        var factory = services.BuildServiceProvider().GetRequiredService<IQueueClientFactory>();

        // act
        var clients = Enumerable.Range(0, 32).AsParallel().Select(_ => factory.GetQueueClient("foo")).ToList();

        // assert
        created.Should().ContainSingle();
        clients.Distinct().Should().ContainSingle();
    }
}
