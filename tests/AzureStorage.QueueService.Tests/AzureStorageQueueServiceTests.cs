using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using AzureStorage.QueueService.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AzureStorage.QueueService.Tests;

public class AzureStorageQueueServiceTests
{
    private static readonly IOptions<QueueServiceTelemetrySettings> TelemetrySettings = Options.Create(new QueueServiceTelemetrySettings());
    private static readonly ILoggerFactory LoggerFactory = new ServiceCollection().AddLogging().BuildServiceProvider().GetRequiredService<ILoggerFactory>();
    private const string ValidJson = "{\"Property1\":\"test\",\"Property2\":true,\"Property3\":1}";

    private static Mock<QueueClient> QueueClientReturning(params string[] messageIds)
    {
        var messages = messageIds.Select(id => QueuesModelFactory.QueueMessage(id, $"receipt-{id}", ValidJson, 1)).ToArray();
        var mock = new Mock<QueueClient>();
        mock.Setup(x => x.ReceiveMessagesAsync(It.IsAny<int?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(messages, Mock.Of<Response>()));
        return mock;
    }

    private static AzureStorageQueueClient CreateSubject(Mock<QueueClient> queueClient, TimeSpan? visibilityTimeout = null) =>
        new(new JsonQueueMessageConverter(), queueClient.Object, LoggerFactory, TelemetrySettings, visibilityTimeout);

    private static ValueTask Ignore(Exception exception, IDictionary<string, string>? metadata) => ValueTask.CompletedTask;

    [Fact(DisplayName = "Peek messages returns message collection")]
    public async Task Peek_Messages_Returns_Collection()
    {
        // arrange
        var peekedMessages = new[] { QueuesModelFactory.PeekedMessage("1", ValidJson, 1) };
        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClient.Setup(x => x.PeekMessagesAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(peekedMessages, Mock.Of<Response>()));

        // act
        var messages = await CreateSubject(mockQueueClient).PeekMessagesAsync<TestObject>(1);

        // assert
        messages.Should().ContainSingle().Which.Property1.Should().Be("test");
    }

    [Fact(DisplayName = "Receive handles every message in the batch and deletes each one")]
    public async Task Receive_Handles_And_Deletes_Each_Message()
    {
        // arrange
        var mockQueueClient = QueueClientReturning("1", "2");
        var handled = new List<TestObject?>();

        // act
        await CreateSubject(mockQueueClient).ReceiveMessagesAsync<TestObject>(
            (message, _) => { handled.Add(message); return ValueTask.CompletedTask; },
            Ignore,
            numMessages: 2);

        // assert
        handled.Should().HaveCount(2);
        mockQueueClient.Verify(x => x.DeleteMessageAsync("1", "receipt-1", It.IsAny<CancellationToken>()), Times.Once);
        mockQueueClient.Verify(x => x.DeleteMessageAsync("2", "receipt-2", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact(DisplayName = "A throwing handler leaves its message on the queue and the rest of the batch is still processed")]
    public async Task Receive_When_Handler_Throws_Leaves_Message_And_Continues()
    {
        // arrange
        var mockQueueClient = QueueClientReturning("1", "2");
        var reported = new List<Exception>();
        var calls = 0;

        // act
        await CreateSubject(mockQueueClient).ReceiveMessagesAsync<TestObject>(
            (_, _) => ++calls == 1 ? throw new InvalidOperationException("Hello from Exception") : ValueTask.CompletedTask,
            (exception, _) => { reported.Add(exception); return ValueTask.CompletedTask; },
            numMessages: 2);

        // assert
        calls.Should().Be(2);
        reported.Should().ContainSingle().Which.Message.Should().Be("Hello from Exception");
        mockQueueClient.Verify(x => x.DeleteMessageAsync("1", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        mockQueueClient.Verify(x => x.DeleteMessageAsync("2", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact(DisplayName = "A failed receive is reported to the exception handler instead of thrown")]
    public async Task Receive_When_Receive_Fails_Reports_Instead_Of_Throwing()
    {
        // arrange
        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClient.Setup(x => x.ReceiveMessagesAsync(It.IsAny<int?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(500, "storage is having a bad day"));
        var reported = new List<Exception>();

        // act
        Func<Task> act = async () => await CreateSubject(mockQueueClient).ReceiveMessagesAsync<TestObject>(
            (_, _) => throw new Exception("handler should not run"),
            (exception, _) => { reported.Add(exception); return ValueTask.CompletedTask; });

        // assert
        await act.Should().NotThrowAsync();
        reported.Should().ContainSingle().Which.Should().BeOfType<RequestFailedException>();
    }

    [Fact(DisplayName = "A throwing exception handler does not escape and the rest of the batch is still processed")]
    public async Task Receive_When_Exception_Handler_Throws_Does_Not_Escape()
    {
        // arrange
        var mockQueueClient = QueueClientReturning("1", "2");
        var handlerCalls = 0;

        // act
        Func<Task> act = async () => await CreateSubject(mockQueueClient).ReceiveMessagesAsync<TestObject>(
            (_, _) => { handlerCalls++; throw new InvalidOperationException("handler failed"); },
            (_, _) => throw new InvalidOperationException("exception handler failed too"),
            numMessages: 2);

        // assert
        await act.Should().NotThrowAsync();
        handlerCalls.Should().Be(2);
    }

    [Fact(DisplayName = "A delete that fails after the handler succeeded is reported to the exception handler")]
    public async Task Receive_When_Delete_Fails_Reports_To_Exception_Handler()
    {
        // arrange
        var mockQueueClient = QueueClientReturning("1");
        mockQueueClient.Setup(x => x.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "pop receipt mismatch"));
        var reported = new List<Exception>();

        // act
        await CreateSubject(mockQueueClient).ReceiveMessagesAsync<TestObject>(
            (_, _) => ValueTask.CompletedTask,
            (exception, _) => { reported.Add(exception); return ValueTask.CompletedTask; });

        // assert
        reported.Should().ContainSingle().Which.Should().BeOfType<RequestFailedException>();
    }

    [Fact(DisplayName = "A message that cannot be deserialized is reported and left on the queue")]
    public async Task Receive_When_Conversion_Fails_Reports_And_Leaves_Message()
    {
        // arrange
        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClient.Setup(x => x.ReceiveMessagesAsync(It.IsAny<int?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(new[] { QueuesModelFactory.QueueMessage("1", "receipt-1", "this is not json", 3) }, Mock.Of<Response>()));
        var reported = new List<Exception>();

        // act
        await CreateSubject(mockQueueClient).ReceiveMessagesAsync<TestObject>(
            (_, _) => throw new Exception("handler should not run"),
            (exception, _) => { reported.Add(exception); return ValueTask.CompletedTask; });

        // assert
        reported.Should().ContainSingle().Which.Should().BeOfType<DeserializationException>()
            .Which.InnerException.Should().NotBeNull();
        mockQueueClient.Verify(x => x.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact(DisplayName = "The caller's own cancellation still propagates")]
    public async Task Receive_When_Caller_Cancels_Throws_OperationCanceled()
    {
        // arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClient.Setup(x => x.ReceiveMessagesAsync(It.IsAny<int?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));
        var exceptionHandlerCalled = false;

        // act
        Func<Task> act = async () => await CreateSubject(mockQueueClient).ReceiveMessagesAsync<TestObject>(
            (_, _) => ValueTask.CompletedTask,
            (_, _) => { exceptionHandlerCalled = true; return ValueTask.CompletedTask; },
            cancellationToken: cts.Token);

        // assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        exceptionHandlerCalled.Should().BeFalse();
    }

    [Fact(DisplayName = "The configured visibility timeout is passed to the receive call")]
    public async Task Receive_Passes_Configured_Visibility_Timeout()
    {
        // arrange
        var mockQueueClient = QueueClientReturning();
        var visibilityTimeout = TimeSpan.FromMinutes(5);

        // act
        await CreateSubject(mockQueueClient, visibilityTimeout).ReceiveMessagesAsync<TestObject>((_, _) => ValueTask.CompletedTask, Ignore);

        // assert
        mockQueueClient.Verify(x => x.ReceiveMessagesAsync(1, visibilityTimeout, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact(DisplayName = "Can send message")]
    public async Task Can_Send_Message()
    {
        // arrange
        var testObject = new TestObject { Property1 = "sent", Property2 = true, Property3 = 1 };
        var sendReceipt = QueuesModelFactory.SendReceipt("1", DateTimeOffset.Now, DateTimeOffset.Now, "2", DateTimeOffset.Now);
        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClient.Setup(x => x.SendMessageAsync(It.IsAny<BinaryData>(), null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(sendReceipt, Mock.Of<Response>()));

        // act
        var receipt = await CreateSubject(mockQueueClient).SendMessageAsync(testObject);

        // assert
        receipt.Should().BeSameAs(sendReceipt);
        mockQueueClient.Verify(x => x.SendMessageAsync(It.Is<BinaryData>(b => b.ToString().Contains("\"Property1\":\"sent\"")), null, null, It.IsAny<CancellationToken>()), Times.Once);
    }
}
