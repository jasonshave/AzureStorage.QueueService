using Azure.Identity;
using FluentAssertions;

namespace AzureStorage.QueueService.Tests;

public class QueueClientBuilderTests
{
    [Fact]
    public void CreateClient_Returns_QueueClient()
    {
        // arrange
        var settings = new QueueClientSettings()
        {
            TokenCredential = new DefaultAzureCredential(),
            EndpointUri = new Uri("https://fake.com"),
            QueueName = "queue1",
        };
        var subject = new QueueClientBuilder();

        // act
        var client = subject.CreateClient(settings);

        // assert
        client.Should().NotBeNull();
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
        subject.Invoking(x => x.CreateClient(settings)).Should().Throw<ApplicationException>();
    }

    [Fact]
    public void CreateClient_WithoutConnectionString_Throws()
    {
        // arrange
        var settings = new QueueClientSettings()
        {
            QueueName = "queue1",
        };
        var subject = new QueueClientBuilder();

        // act & assert
        subject.Invoking(x => x.CreateClient(settings)).Should().Throw<ApplicationException>();
    }
}