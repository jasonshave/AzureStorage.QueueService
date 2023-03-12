using Azure.Storage.Queues;
using FluentAssertions;
using JasonShave.AzureStorage.QueueService.Extensions;
using JasonShave.AzureStorage.QueueService.Interfaces;
using JasonShave.AzureStorage.QueueService.Models;
using JasonShave.AzureStorage.QueueService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace JasonShave.AzureStorage.QueueService.Tests;

public class QueueClientFactoryTests : BaseTestHost
{
    [Fact]
    public void GetNamedClient_ReturnsValidClient()
    {
        // arrange
        IHost host = Host
            .CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddAzureStorageQueueClient(x =>
                {
                    x.AddClient("foo", y => Configuration.Bind(nameof(QueueClientSettings), y));
                });
            })
            .Build();

        var registry = host.Services.GetRequiredService<QueueClientSettingsRegistry>();
        var mockQueueClientBuilder = new Mock<IQueueClientBuilder>();
        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClientBuilder.Setup(x => x.CreateClient(It.IsAny<QueueClientSettings>())).Returns(mockQueueClient.Object);
        IQueueClientFactory subject = new QueueClientFactory(host.Services, registry, mockQueueClientBuilder.Object);

        // act
        var queueClient = subject.GetQueueClient("foo");

        // assert
        queueClient.Should().NotBeNull();
    }

    [Fact]
    public void GetNamedClientTwice_ReturnsSameClient()
    {
        // arrange
        IHost host = Host
            .CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddAzureStorageQueueClient(x =>
                {
                    x.AddClient("foo", y => Configuration.Bind(nameof(QueueClientSettings), y));
                });
            })
            .Build();

        var registry = host.Services.GetRequiredService<QueueClientSettingsRegistry>();
        var mockQueueClientBuilder = new Mock<IQueueClientBuilder>();
        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClientBuilder.Setup(x => x.CreateClient(It.IsAny<QueueClientSettings>())).Returns(mockQueueClient.Object);
        IQueueClientFactory subject = new QueueClientFactory(host.Services, registry, mockQueueClientBuilder.Object);

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
        IHost host = Host
            .CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddAzureStorageQueueClient(x =>
                {
                    x.AddClient("foo", y => Configuration.Bind(nameof(QueueClientSettings), y));
                    x.AddClient("bar", y => Configuration.Bind(nameof(QueueClientSettings), y));
                });
            })
            .Build();

        var registry = host.Services.GetRequiredService<QueueClientSettingsRegistry>();
        var mockQueueClientBuilder = new Mock<IQueueClientBuilder>();
        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClientBuilder.Setup(x => x.CreateClient(It.IsAny<QueueClientSettings>())).Returns(mockQueueClient.Object);
        IQueueClientFactory subject = new QueueClientFactory(host.Services, registry, mockQueueClientBuilder.Object);

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
        IHost host = Host
            .CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddAzureStorageQueueClient(x =>
                {
                    x.AddClient("MyClient1", y => Configuration.Bind(nameof(QueueClientSettings), y));
                    x.AddClient("MyClient2", y =>
                    {
                        y.ConnectionString = "[your_connection_string]";
                        y.QueueName = "[your_queue_name]";
                    });
                    x.AddDefaultClient(y => Configuration.Bind(nameof(QueueClientSettings), y));
                });
            })
            .Build();

        var registry = host.Services.GetRequiredService<QueueClientSettingsRegistry>();
        var mockQueueClientBuilder = new Mock<IQueueClientBuilder>();
        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClientBuilder.Setup(x => x.CreateClient(It.IsAny<QueueClientSettings>())).Returns(mockQueueClient.Object);
        IQueueClientFactory subject = new QueueClientFactory(host.Services, registry, mockQueueClientBuilder.Object);

        // act
        var queueClient = subject.GetQueueClient();

        // assert
        queueClient.Should().NotBeNull();
    }

    [Fact]
    public void GetUnregisteredClient_Throws()
    {
        // arrange
        IHost host = Host
            .CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddAzureStorageQueueClient(x =>
                {
                    x.AddClient("foo", y => Configuration.Bind(nameof(QueueClientSettings), y));
                });
            })
            .Build();

        var registry = host.Services.GetRequiredService<QueueClientSettingsRegistry>();
        var mockQueueClientBuilder = new Mock<IQueueClientBuilder>();
        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClientBuilder.Setup(x => x.CreateClient(It.IsAny<QueueClientSettings>())).Returns(mockQueueClient.Object);
        IQueueClientFactory subject = new QueueClientFactory(host.Services, registry, mockQueueClientBuilder.Object);

        // act
        subject.Invoking(x => x.GetQueueClient("bar")).Should().Throw<ApplicationException>(because: "The client name wasn't registered");
    }
}