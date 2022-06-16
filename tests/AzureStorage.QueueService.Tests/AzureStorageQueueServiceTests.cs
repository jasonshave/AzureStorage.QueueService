using System.Net;
using AutoFixture;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using FluentAssertions;
using JasonShave.AzureStorage.QueueService.Interfaces;
using JasonShave.AzureStorage.QueueService.Models;
using JasonShave.AzureStorage.QueueService.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace AzureStorage.QueueService.Tests;

public class AzureStorageQueueServiceTests
{
    private readonly QueueClientSettings _queueClientSettings;
    public AzureStorageQueueServiceTests()
    {
        _queueClientSettings = new QueueClientSettings()
        {
            QueueName = "Test",
            ConnectionString = "DefaultEndpointsProtocol=https;AccountName=test;AccountKey=FAKE/xd3SoV4C52caAxURkg7Pso+X5QyFprgcAeDCw7joUYCGx3J7B+V+PZ6znEQ0lN/Mvxqdkwi+AStHyBWuA==;EndpointSuffix=core.windows.net"
        };
    }

    [Fact(DisplayName = "Peek messages returns message collection")]
    public async Task Peek_Messages_Returns_Collection()
    {
        // arrange
        var fixture = new Fixture();
        Response mockResponse = Mock.Of<Response>();
        var peekedMessage = QueuesModelFactory.PeekedMessage("1", "test_text", 1);
        PeekedMessage[] peekedMessages = { peekedMessage };
        var response = Response.FromValue(peekedMessages, mockResponse);

        var queueClient = new Mock<QueueClient>(_queueClientSettings.ConnectionString, _queueClientSettings.QueueName);
        queueClient.Setup(x => x.PeekMessagesAsync(It.IsAny<int>(), CancellationToken.None)).ReturnsAsync(response);
        
        var mockQueueClientFactory = new Mock<IQueueClientFactory>();
        mockQueueClientFactory.Setup(x => x.GetQueueClient(It.IsAny<string>())).Returns(queueClient.Object);

        var mockMessageConverter = new Mock<IMessageConverter>();
        mockMessageConverter.Setup(x => x.Convert<TestObject>(It.IsAny<BinaryData>())).Returns(fixture.Create<TestObject>());

        var mockLogger = new Mock<ILogger<AzureStorageQueueService>>();

        var subject = new AzureStorageQueueService(mockQueueClientFactory.Object, mockMessageConverter.Object,
            mockLogger.Object);

        // act
        var messages = await subject.PeekMessages<TestObject>(It.IsAny<string>(), It.IsAny<int>());
        var numMessages = await subject.GetMessageCount(It.IsAny<string>(), It.IsAny<int>());

        // assert
        messages.Should().NotBeEmpty();
        messages.Count().Should().Be(1);
        numMessages.Should().Be(1);
    }

    [Fact(DisplayName = "Peek messages returns message collection")]
    public async Task Receive_Messages_Returns_Collection()
    {
        // arrange
        var fixture = new Fixture();
        Response mockResponse = Mock.Of<Response>();
        var queueMessage = QueuesModelFactory.QueueMessage("1", "2", "test_text", 1);
        QueueMessage[] peekedMessages = { queueMessage };
        var response = Response.FromValue(peekedMessages, mockResponse);

        var queueClient = new Mock<QueueClient>(_queueClientSettings.ConnectionString, _queueClientSettings.QueueName);
        queueClient.Setup(x => x.ReceiveMessagesAsync(CancellationToken.None)).ReturnsAsync(response);
        queueClient.Setup(x => x.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), CancellationToken.None));

        var mockQueueClientFactory = new Mock<IQueueClientFactory>();
        mockQueueClientFactory.Setup(x => x.GetQueueClient(It.IsAny<string>())).Returns(queueClient.Object);

        var mockMessageConverter = new Mock<IMessageConverter>();
        mockMessageConverter.Setup(x => x.Convert<TestObject>(It.IsAny<BinaryData>())).Returns(fixture.Create<TestObject>());

        var mockLogger = new Mock<ILogger<AzureStorageQueueService>>();

        var subject = new AzureStorageQueueService(mockQueueClientFactory.Object, mockMessageConverter.Object,
            mockLogger.Object);

        // act/assert
        await subject.ReceiveMessagesAsync<TestObject>(_queueClientSettings.QueueName, HandleMessage, HandleException);

        Task HandleMessage(TestObject? testObject)
        {
            return Task.CompletedTask;
        }

        Task HandleException(Exception exception)
        {
            return Task.CompletedTask;
        }
    }

    [Fact(DisplayName = "Peek messages returns message collection")]
    public async Task Receive_Messages_Throws_Collection()
    {
        // arrange
        var fixture = new Fixture();
        Response mockResponse = Mock.Of<Response>();
        var queueMessage = QueuesModelFactory.QueueMessage("1", "2", "test_text", 1);
        QueueMessage[] peekedMessages = { queueMessage };
        var response = Response.FromValue(peekedMessages, mockResponse);

        var queueClient = new Mock<QueueClient>(_queueClientSettings.ConnectionString, _queueClientSettings.QueueName);
        queueClient.Setup(x => x.ReceiveMessagesAsync(CancellationToken.None)).ReturnsAsync(response);
        queueClient.Setup(x => x.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), CancellationToken.None));

        var mockQueueClientFactory = new Mock<IQueueClientFactory>();
        mockQueueClientFactory.Setup(x => x.GetQueueClient(It.IsAny<string>())).Returns(queueClient.Object);

        var mockMessageConverter = new Mock<IMessageConverter>();
        mockMessageConverter.Setup(x => x.Convert<TestObject>(It.IsAny<BinaryData>())).Returns(fixture.Create<TestObject>());

        var mockLogger = new Mock<ILogger<AzureStorageQueueService>>();

        var subject = new AzureStorageQueueService(mockQueueClientFactory.Object, mockMessageConverter.Object,
            mockLogger.Object);

        // act/assert
        await subject.ReceiveMessagesAsync<TestObject>(_queueClientSettings.QueueName, HandleMessage, HandleException);

        Task HandleMessage(TestObject? testObject)
        {
            throw new Exception("Hello from handler");
        }

        Task HandleException(Exception exception)
        {
            exception.Message.Should().Be("Hello from handler");
            return Task.CompletedTask;
        }
    }
}