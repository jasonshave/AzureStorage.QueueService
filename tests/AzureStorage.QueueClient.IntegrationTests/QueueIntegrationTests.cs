using Azure.Messaging;
using AzureStorage.QueueService;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AzureStorage.QueueClient.IntegrationTests;

public class QueueIntegrationTests : IClassFixture<InfrastructureFixture>
{
    private readonly AzureStorageQueueClient _client;

    public QueueIntegrationTests(InfrastructureFixture fixture)
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        serviceCollection.AddAzureStorageQueueClient(options =>
        {
            options.AddDefaultClient(y =>
            {
                y.ConnectionString = fixture.ConnectionString;
                y.QueueName = "testing";
                y.CreateIfNotExists = true;
            });
        });
        var services = serviceCollection.BuildServiceProvider();

        var clientFactory = services.GetRequiredService<IQueueClientFactory>();
        _client = clientFactory.GetQueueClient();
    }

    [Fact]
    public async Task AzureStorageQueueClient_ShouldSendMessage_WhenValidInputProvided()
    {
        // arrange        
        var data = new
        {
            Property1 = "test",
            Property2 = true,
            Property3 = 1
        };

        var cloudEvent = new CloudEvent("source", "myClass", data, data.GetType())
        {
            Id = "myCustomId",
        };

        // act
        var sendReceipt = await _client.SendMessageAsync(cloudEvent);

        // receive message
        await _client.ReceiveMessagesAsync<CloudEvent>(HandleMessage, HandleException);

        async ValueTask HandleMessage(CloudEvent? message, IDictionary<string, string>? metadata)
        {
            // assert
            message.Should().NotBeNull();
            message.Id.Should().Be(cloudEvent.Id);
        }

        async ValueTask HandleException(Exception ex, IDictionary<string, string>? metadata)
        {
            // no-op
        }

        // assert
        sendReceipt.Should().NotBeNull();
    }

    [Fact]
    public async Task AzureStorageQueueClient_ShouldDeleteSentMessage_WhenValidSendReceiptDataUsed()
    {
        // arrange        
        var data = new
        {
            Property1 = "test",
            Property2 = true,
            Property3 = 1
        };

        var cloudEvent = new CloudEvent("source", "myClass", data, data.GetType())
        {
            Id = "myCustomId",
        };

        // act
        var sendReceipt = await _client.SendMessageAsync(cloudEvent, TimeSpan.FromMinutes(10));

        // delete message
        await Task.Delay(500);
        var result = await _client.DeleteMessageAsync(sendReceipt.MessageId, sendReceipt.PopReceipt);

        // assert
        sendReceipt.Should().NotBeNull();
        result.Should().BeTrue();
    }
}