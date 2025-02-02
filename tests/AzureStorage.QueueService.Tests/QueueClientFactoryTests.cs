using Azure.Identity;
using Azure.Storage.Queues;
using AzureStorage.QueueService.Telemetry;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AzureStorage.QueueService.Tests;

public class QueueClientFactoryTests : BaseTestHost
{
    private readonly IOptions<QueueServiceTelemetrySettings> _telemetrySettings = Options.Create(new QueueServiceTelemetrySettings());

    [Fact]
    public void QueueClientFactory_GetNamedClient_ReturnsClient()
    {
        // arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(options => options.AddClient("foo", y => Configuration.Bind("QueueClientSettings", y)));

        var serviceProvider = services.BuildServiceProvider();

        var registry = serviceProvider.GetRequiredService<QueueClientSettingsRegistry>();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var messageConverter = serviceProvider.GetRequiredService<IMessageConverter>();
        var mockQueueClientBuilder = new Mock<IQueueClientBuilder>();
        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClientBuilder.Setup(x => x.CreateClient(It.IsAny<QueueClientSettings>())).Returns(mockQueueClient.Object);
        IQueueClientFactory subject = new QueueClientFactory(registry, mockQueueClientBuilder.Object, loggerFactory, messageConverter, _telemetrySettings);

        // act
        var queueClient = subject.GetQueueClient("foo");

        // assert
        queueClient.Should().NotBeNull();
    }

    [Fact]
    public void QueueClientFactory_AddStorageQueueCalledTwice_ReturnsClient()
    {
        // arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(options => options.AddClient("foo", y => Configuration.Bind("QueueClientSettings", y)));
        services.AddAzureStorageQueueClient(options => options.AddClient("foo", y => Configuration.Bind("QueueClientSettings", y)));

        var serviceProvider = services.BuildServiceProvider();

        var registry = serviceProvider.GetRequiredService<QueueClientSettingsRegistry>();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var messageConverter = serviceProvider.GetRequiredService<IMessageConverter>();
        var mockQueueClientBuilder = new Mock<IQueueClientBuilder>();
        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClientBuilder.Setup(x => x.CreateClient(It.IsAny<QueueClientSettings>())).Returns(mockQueueClient.Object);
        IQueueClientFactory subject = new QueueClientFactory(registry, mockQueueClientBuilder.Object, loggerFactory, messageConverter, _telemetrySettings);

        // act
        var queueClient = subject.GetQueueClient("foo");

        // assert
        queueClient.Should().NotBeNull();
    }

    [Fact]
    public void GetNamedClientTwice_ReturnsSameClient()
    {
        // arrange        
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(options => options.AddClient("foo", y => Configuration.Bind("QueueClientSettings", y)));

        var serviceProvider = services.BuildServiceProvider();

        var registry = serviceProvider.GetRequiredService<QueueClientSettingsRegistry>();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var messageConverter = serviceProvider.GetRequiredService<IMessageConverter>();
        var mockQueueClientBuilder = new Mock<IQueueClientBuilder>();
        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClientBuilder.Setup(x => x.CreateClient(It.IsAny<QueueClientSettings>())).Returns(mockQueueClient.Object);
        IQueueClientFactory subject = new QueueClientFactory(registry, mockQueueClientBuilder.Object, loggerFactory, messageConverter, _telemetrySettings);

        // act
        var queueClient1 = subject.GetQueueClient("foo");
        var queueClient2 = subject.GetQueueClient("foo");

        // assert
        queueClient1.Should().NotBeNull();
        queueClient2.Should().NotBeNull();
        queueClient1.Should().BeSameAs(queueClient2);
    }

    [Fact]
    public void RegisterMultipleNamedClient_ReturnsCorrectClient()
    {
        // arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(options =>
        {
            options.AddClient("foo", y => Configuration.Bind("QueueClientSettings", y));
            options.AddClient("bar", y => Configuration.Bind("QueueClientSettings", y));
        });

        var serviceProvider = services.BuildServiceProvider();

        var registry = serviceProvider.GetRequiredService<QueueClientSettingsRegistry>();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var messageConverter = serviceProvider.GetRequiredService<IMessageConverter>();
        var mockQueueClientBuilder = new Mock<IQueueClientBuilder>();
        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClientBuilder.Setup(x => x.CreateClient(It.IsAny<QueueClientSettings>())).Returns(mockQueueClient.Object);
        IQueueClientFactory subject = new QueueClientFactory(registry, mockQueueClientBuilder.Object, loggerFactory, messageConverter, _telemetrySettings);

        // act
        var queueClient1 = subject.GetQueueClient("foo");
        var queueClient2 = subject.GetQueueClient("bar");

        // assert
        queueClient1.Should().NotBeNull();
        queueClient2.Should().NotBeNull();
    }

    [Fact]
    public void GetDefaultClient_ReturnsValidClient()
    {
        // arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(options => options.AddDefaultClient(y => Configuration.Bind("QueueClientSettings", y)));

        var serviceProvider = services.BuildServiceProvider();

        var registry = serviceProvider.GetRequiredService<QueueClientSettingsRegistry>();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var messageConverter = serviceProvider.GetRequiredService<IMessageConverter>();
        var mockQueueClientBuilder = new Mock<IQueueClientBuilder>();
        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClientBuilder.Setup(x => x.CreateClient(It.IsAny<QueueClientSettings>())).Returns(mockQueueClient.Object);
        IQueueClientFactory subject = new QueueClientFactory(registry, mockQueueClientBuilder.Object, loggerFactory, messageConverter, _telemetrySettings);

        // act
        var queueClient = subject.GetQueueClient();

        // assert
        queueClient.Should().NotBeNull();
    }

    [Fact]
    public void GetUnregisteredClient_Throws()
    {
        // arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(options => options.AddClient("foo", y => Configuration.Bind("QueueClientSettings", y)));

        var serviceProvider = services.BuildServiceProvider();

        var registry = serviceProvider.GetRequiredService<QueueClientSettingsRegistry>();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var messageConverter = serviceProvider.GetRequiredService<IMessageConverter>();
        var mockQueueClientBuilder = new Mock<IQueueClientBuilder>();
        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClientBuilder.Setup(x => x.CreateClient(It.IsAny<QueueClientSettings>())).Returns(mockQueueClient.Object);
        IQueueClientFactory subject = new QueueClientFactory(registry, mockQueueClientBuilder.Object, loggerFactory, messageConverter, _telemetrySettings);

        // act
        subject.Invoking(x => x.GetQueueClient("bar")).Should().Throw<ApplicationException>(because: "The client name wasn't registered");
    }

    [Fact]
    public void CreateClient_WithCorrectSettings_Creates()
    {
        // arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(options => options.AddDefaultClient(y =>
        {
            y.TokenCredential = new DefaultAzureCredential();
            y.EndpointUri = new Uri("https://fake.com");
            y.QueueName = "foo";
            y.CreateIfNotExists = true;
        }));

        var serviceProvider = services.BuildServiceProvider();

        var registry = serviceProvider.GetRequiredService<QueueClientSettingsRegistry>();
        var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
        var messageConverter = serviceProvider.GetRequiredService<IMessageConverter>();
        var mockQueueClientBuilder = new Mock<IQueueClientBuilder>();
        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClientBuilder.Setup(x => x.CreateClient(It.IsAny<QueueClientSettings>())).Returns(mockQueueClient.Object);
        IQueueClientFactory subject = new QueueClientFactory(registry, mockQueueClientBuilder.Object, loggerFactory, messageConverter, _telemetrySettings);

        // act
        var client = subject.GetQueueClient();

        // assert
        client.Should().NotBeNull();
    }
}