using Azure.Identity;
using FluentAssertions;

namespace AzureStorage.QueueService.Tests;

public class SdkQueueClientFactoryTests
{
    private const string Description = "Queue client 'test'";

    [Fact]
    public void CreateClient_WithEndpointAndCredential_ReturnsQueueClient()
    {
        var settings = new QueueClientSettings
        {
            TokenCredential = new DefaultAzureCredential(),
            EndpointUri = new Uri("https://fake.com/queue1"),
        };

        new SdkQueueClientFactory().CreateClient(Description, settings).Name.Should().Be("queue1");
    }

    [Fact]
    public void CreateClient_WithConnectionString_ReturnsQueueClient()
    {
        var settings = new QueueClientSettings
        {
            ConnectionString = "UseDevelopmentStorage=true",
            QueueName = "queue1",
        };

        new SdkQueueClientFactory().CreateClient(Description, settings).Name.Should().Be("queue1");
    }

    [Fact]
    public void CreateClient_WithoutUri_ThrowsNamingTheClient()
    {
        var settings = new QueueClientSettings
        {
            TokenCredential = new DefaultAzureCredential(),
            QueueName = "queue1",
        };

        new SdkQueueClientFactory().Invoking(x => x.CreateClient(Description, settings))
            .Should().Throw<InvalidOperationException>().WithMessage($"{Description}*");
    }

    [Fact]
    public void CreateClient_WithConnectionStringButNoQueueName_Throws()
    {
        var settings = new QueueClientSettings { ConnectionString = "UseDevelopmentStorage=true" };

        new SdkQueueClientFactory().Invoking(x => x.CreateClient(Description, settings))
            .Should().Throw<InvalidOperationException>().WithMessage("*QueueName*");
    }

    [Fact]
    public void CreateClient_WithNothingConfigured_Throws()
    {
        var settings = new QueueClientSettings { QueueName = "queue1" };

        new SdkQueueClientFactory().Invoking(x => x.CreateClient(Description, settings))
            .Should().Throw<InvalidOperationException>();
    }
}
