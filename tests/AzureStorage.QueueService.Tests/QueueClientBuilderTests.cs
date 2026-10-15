using Azure.Identity;
using FluentAssertions;

namespace AzureStorage.QueueService.Tests;

public class QueueClientBuilderTests
{
    private const string Description = "Queue client 'test'";

    [Fact]
    public void CreateClient_WithEndpointAndCredential_ReturnsQueueClient()
    {
        // arrange
        var settings = new QueueClientSettings()
        {
            TokenCredential = new DefaultAzureCredential(),
            EndpointUri = new Uri("https://fake.com/queue1"),
        };
        var subject = new QueueClientBuilder();

        // act
        var client = subject.CreateClient(Description, settings);

        // assert
        client.Name.Should().Be("queue1");
    }

    [Fact]
    public void CreateClient_WithConnectionString_ReturnsQueueClient()
    {
        // arrange
        var settings = new QueueClientSettings()
        {
            ConnectionString = "UseDevelopmentStorage=true",
            QueueName = "queue1",
        };
        var subject = new QueueClientBuilder();

        // act
        var client = subject.CreateClient(Description, settings);

        // assert
        client.Name.Should().Be("queue1");
    }

    [Fact]
    public void CreateClient_WithoutUri_Throws()
    {
        // arrange
        var settings = new QueueClientSettings()
        {
            TokenCredential = new DefaultAzureCredential(),
            QueueName = "queue1",
        };
        var subject = new QueueClientBuilder();

        // act & assert
        subject.Invoking(x => x.CreateClient(Description, settings)).Should().Throw<ApplicationException>().WithMessage($"{Description}*");
    }

    [Fact]
    public void CreateClient_WithConnectionStringButNoQueueName_Throws()
    {
        // arrange
        var settings = new QueueClientSettings()
        {
            ConnectionString = "UseDevelopmentStorage=true",
        };
        var subject = new QueueClientBuilder();

        // act & assert
        subject.Invoking(x => x.CreateClient(Description, settings)).Should().Throw<ApplicationException>().WithMessage("*QueueName*");
    }

    [Fact]
    public void CreateClient_WithNothingConfigured_Throws()
    {
        // arrange
        var settings = new QueueClientSettings()
        {
            QueueName = "queue1",
        };
        var subject = new QueueClientBuilder();

        // act & assert
        subject.Invoking(x => x.CreateClient(Description, settings)).Should().Throw<ApplicationException>();
    }
}
