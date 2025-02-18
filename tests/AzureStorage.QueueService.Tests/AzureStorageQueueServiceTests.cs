using AutoFixture;
using Azure;
using Azure.Messaging;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using AzureStorage.QueueService.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AzureStorage.QueueService.Tests;

public class AzureStorageQueueServiceTests : BaseTestHost
{
    private readonly QueueClientSettings _queueClientSettings;
    private readonly IOptions<QueueServiceTelemetrySettings> _telemetrySettings = Options.Create(new QueueServiceTelemetrySettings());
    private readonly IServiceProvider _serviceProvider;

    public AzureStorageQueueServiceTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();        

        _serviceProvider = services.BuildServiceProvider();

        _queueClientSettings = new();
        Configuration.Bind(nameof(QueueClientSettings), _queueClientSettings);

        var data = new TestObject
        {
            Property1 = "test",
            Property2 = true,
            Property3 = 1
        };
        var cloudEvent = new CloudEvent("source", "myClass", data, typeof(TestObject))
        {
            Id = "myCustomId",
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

        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClient.Setup(x => x.PeekMessagesAsync(It.IsAny<int>(), CancellationToken.None)).ReturnsAsync(response);

        var mockMessageConverter = new Mock<IMessageConverter>();
        mockMessageConverter.Setup(x => x.Convert<TestObject>(It.IsAny<string>())).Returns(fixture.Create<TestObject>());

        var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();

        var subject = new AzureStorageQueueClient(mockMessageConverter.Object, mockQueueClient.Object, loggerFactory, _telemetrySettings);

        // act
        var messages = await subject.PeekMessagesAsync<TestObject>(It.IsAny<int>());

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
        mockQueueClient.Setup(x => x.ReceiveMessagesAsync(1, null, CancellationToken.None)).ReturnsAsync(response);
        mockQueueClient.Setup(x => x.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), CancellationToken.None));

        var mockMessageConverter = new Mock<IMessageConverter>();
        mockMessageConverter.Setup(x => x.Convert<TestObject>(It.IsAny<string>())).Returns(fixture.Create<TestObject>());

        var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();

        var subject = new AzureStorageQueueClient(mockMessageConverter.Object, mockQueueClient.Object, loggerFactory, _telemetrySettings);

        // act/assert
        await subject.ReceiveMessagesAsync<TestObject>(HandleMessage, HandleException);

        ValueTask HandleMessage(TestObject? testObject, IDictionary<string, string>? metadata) => ValueTask.CompletedTask;
        ValueTask HandleException(Exception exception, IDictionary<string, string>? metadata) => ValueTask.CompletedTask;
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
        mockQueueClient.Setup(x => x.ReceiveMessagesAsync(1, null, CancellationToken.None)).ReturnsAsync(response);
        mockQueueClient.Setup(x => x.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), CancellationToken.None));

        var mockMessageConverter = new Mock<IMessageConverter>();
        mockMessageConverter.Setup(x => x.Convert<TestObject>(It.IsAny<string>())).Returns(fixture.Create<TestObject>());

        var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();

        var subject = new AzureStorageQueueClient(mockMessageConverter.Object, mockQueueClient.Object, loggerFactory, _telemetrySettings);

        // act/assert
        await subject.ReceiveMessagesAsync<TestObject>(
            (message, metadata) => throw new Exception("Hello from Exception"),
            (exception, metadata) =>
            {
                exception.Message.Should().Be("Hello from Exception");
                return ValueTask.CompletedTask;
            });
    }

    [Fact(DisplayName = "Can send message")]
    public async Task Can_Send_Message()
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

        var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();

        var subject = new AzureStorageQueueClient(mockMessageConverter.Object, mockQueueClient.Object, loggerFactory, _telemetrySettings);

        // act/assert
        await subject.Invoking(async x => await x.SendMessageAsync(testObject)).Should().NotThrowAsync();
    }
}