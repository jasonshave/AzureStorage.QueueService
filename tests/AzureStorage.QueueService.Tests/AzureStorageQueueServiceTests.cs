using AutoFixture;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using FluentAssertions;
using JasonShave.AzureStorage.QueueService.Interfaces;
using JasonShave.AzureStorage.QueueService.Models;
using JasonShave.AzureStorage.QueueService.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace JasonShave.AzureStorage.QueueService.Tests;

public class AzureStorageQueueServiceTests : BaseTestHost
{
    private readonly QueueClientSettings _queueClientSettings;

    public AzureStorageQueueServiceTests()
    {
        _queueClientSettings = new();
        Configuration.Bind(nameof(QueueClientSettings), _queueClientSettings);
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

        var queueClient = new Mock<QueueClient>();
        queueClient.Setup(x => x.PeekMessagesAsync(It.IsAny<int>(), CancellationToken.None)).ReturnsAsync(response);

        var mockMessageConverter = new Mock<IMessageConverter>();
        mockMessageConverter.Setup(x => x.Convert<TestObject>(It.IsAny<string>())).Returns(fixture.Create<TestObject>());

        var mockLogger = new Mock<ILogger<AzureStorageQueueClient>>();

        var subject = new AzureStorageQueueClient(mockMessageConverter.Object, queueClient.Object, mockLogger.Object);

        // act
        var messages = await subject.PeekMessages<TestObject>(It.IsAny<int>());

        // assert
        messages.Should().NotBeEmpty();
        messages.Count().Should().Be(1);
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

        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClient.Setup(x => x.ReceiveMessagesAsync(CancellationToken.None)).ReturnsAsync(response);
        mockQueueClient.Setup(x => x.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), CancellationToken.None));

        var mockMessageConverter = new Mock<IMessageConverter>();
        mockMessageConverter.Setup(x => x.Convert<TestObject>(It.IsAny<string>())).Returns(fixture.Create<TestObject>());

        var mockLogger = new Mock<ILogger<AzureStorageQueueClient>>();

        var subject = new AzureStorageQueueClient(mockMessageConverter.Object, mockQueueClient.Object, mockLogger.Object);

        // act/assert
        await subject.ReceiveMessagesAsync<TestObject>(HandleMessage, HandleException);

        ValueTask HandleMessage(TestObject? testObject) => ValueTask.CompletedTask;
        ValueTask HandleException(Exception exception) => ValueTask.CompletedTask;
    }

    [Fact(DisplayName = "Peek messages returns message collection")]
    public async Task Receive_Messages_Throws_Collection()
    {
        // arrange
        var fixture = new Fixture();
        var mockResponse = Mock.Of<Response>();
        var queueMessage = QueuesModelFactory.QueueMessage("1", "2", "test_text", 1);
        QueueMessage[] peekedMessages = { queueMessage };
        var response = Response.FromValue(peekedMessages, mockResponse);

        var mockQueueClient = new Mock<QueueClient>(_queueClientSettings.ConnectionString, _queueClientSettings.QueueName);
        mockQueueClient.Setup(x => x.ReceiveMessagesAsync(CancellationToken.None)).ReturnsAsync(response);
        mockQueueClient.Setup(x => x.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), CancellationToken.None));

        var mockMessageConverter = new Mock<IMessageConverter>();
        mockMessageConverter.Setup(x => x.Convert<TestObject>(It.IsAny<string>())).Returns(fixture.Create<TestObject>());

        var mockLogger = new Mock<ILogger<AzureStorageQueueClient>>();

        var subject = new AzureStorageQueueClient(mockMessageConverter.Object, mockQueueClient.Object, mockLogger.Object);

        // act/assert
        await subject.ReceiveMessagesAsync<TestObject>(
            _ => throw new Exception("Hello from Handler"),
            x =>
            {
                x.Message.Should().Be("Hello from Handler");
                return ValueTask.CompletedTask;
            });
    }

    [Fact(DisplayName = "Can send message")]
    public async ValueTask Can_Send_Message()
    {
        // arrange
        var fixture = new Fixture();
        var testObject = fixture.Create<TestObject>();

        var mockQueueClient = new Mock<QueueClient>(_queueClientSettings.ConnectionString, _queueClientSettings.QueueName);
        var mockResponse = Mock.Of<Response>();
        var mockMessageConverter = new Mock<IMessageConverter>();

        mockMessageConverter.Setup(x => x.Convert(testObject)).Returns(It.IsAny<BinaryData>());

        SendReceipt sendReceipt =
            QueuesModelFactory.SendReceipt("1", DateTimeOffset.Now, DateTimeOffset.Now, "2", DateTimeOffset.Now);
        var response = Response.FromValue(sendReceipt, mockResponse);
        mockQueueClient.Setup(x => x.SendMessageAsync(It.IsAny<BinaryData>(), null, null, CancellationToken.None)).ReturnsAsync(response);

        var mockLogger = new Mock<ILogger<AzureStorageQueueClient>>();

        var subject = new AzureStorageQueueClient(mockMessageConverter.Object, mockQueueClient.Object, mockLogger.Object);

        // act/assert
        await subject.Invoking(async x => await x.SendMessageAsync(testObject)).Should().NotThrowAsync();
    }
}