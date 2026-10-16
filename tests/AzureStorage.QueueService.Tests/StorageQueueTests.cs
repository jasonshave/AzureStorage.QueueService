using System.Diagnostics;
using Azure;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using AzureStorage.QueueService.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace AzureStorage.QueueService.Tests;

public class StorageQueueTests
{
    private static readonly ILoggerFactory LoggerFactory = new ServiceCollection().AddLogging().BuildServiceProvider().GetRequiredService<ILoggerFactory>();
    private const string ValidJson = "{\"Property1\":\"test\",\"Property2\":true,\"Property3\":1}";

    private static Mock<QueueClient> ClientReturning(params (string Id, string Body)[] messages)
    {
        var sdkMessages = messages.Select(m => QueuesModelFactory.QueueMessage(m.Id, $"receipt-{m.Id}", m.Body, 2)).ToArray();
        var mock = new Mock<QueueClient>();
        mock.Setup(x => x.Name).Returns("orders");
        mock.Setup(x => x.ReceiveMessagesAsync(It.IsAny<int?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(sdkMessages, Mock.Of<Response>()));
        return mock;
    }

    private static StorageQueue Create(Mock<QueueClient> client, Func<QueueClient>? poisonQueue = null, TimeSpan? visibilityTimeout = null, bool createIfNotExists = false, QueueServiceTelemetrySettings? telemetry = null, bool propagateTraceContext = false) =>
        new(client.Object, new JsonQueueMessageSerializer(), LoggerFactory, telemetry ?? new QueueServiceTelemetrySettings(), QueueServiceMetrics.Fallback, visibilityTimeout, createIfNotExists, poisonQueue, propagateTraceContext);

    /// <summary>Sends succeed with message id <c>sent-1</c>; returns the bodies as they went to the service.</summary>
    private static List<BinaryData> CaptureSends(Mock<QueueClient> client)
    {
        var sent = new List<BinaryData>();
        var receipt = QueuesModelFactory.SendReceipt("sent-1", DateTimeOffset.Now, DateTimeOffset.Now, "r", DateTimeOffset.Now);
        client.Setup(x => x.SendMessageAsync(It.IsAny<BinaryData>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback((BinaryData body, TimeSpan? _, TimeSpan? _, CancellationToken _) => sent.Add(body))
            .ReturnsAsync(Response.FromValue(receipt, Mock.Of<Response>()));
        return sent;
    }

    private static void SetupUpdate(Mock<QueueClient> client, string expectedReceipt, TimeSpan expectedTimeout, string newReceipt) =>
        client.Setup(x => x.UpdateMessageAsync(It.IsAny<string>(), expectedReceipt, (string?)null, expectedTimeout, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(QueuesModelFactory.UpdateReceipt(newReceipt, DateTimeOffset.UtcNow + expectedTimeout), Mock.Of<Response>()));

    [Fact(DisplayName = "Receive maps the message identity and deserializes the body")]
    public async Task Receive_Maps_Identity_And_Body()
    {
        var message = (await Create(ClientReturning(("1", ValidJson))).ReceiveAsync<TestObject>()).Should().ContainSingle().Subject;

        message.MessageId.Should().Be("1");
        message.PopReceipt.Should().Be("receipt-1");
        message.DequeueCount.Should().Be(2);
        message.Body!.Property1.Should().Be("test");
        message.Error.Should().BeNull();
        message.Settlement.Should().Be(MessageSettlement.None);
    }

    [Fact(DisplayName = "A body that cannot be deserialized is returned with Error set instead of throwing")]
    public async Task Receive_Bad_Body_Sets_Error()
    {
        var message = (await Create(ClientReturning(("1", "not json"))).ReceiveAsync<TestObject>()).Should().ContainSingle().Subject;

        message.Body.Should().BeNull();
        message.Error.Should().BeOfType<DeserializationException>();
        message.RawBody.ToString().Should().Be("not json");
    }

    [Fact(DisplayName = "F2: a message the SDK could not Base64-decode comes back with Error set and can be dead-lettered")]
    public async Task Receive_Undecodable_Message_Is_Returned_With_Error()
    {
        // The SDK raises MessageDecodingFailed inside the receive call and drops the message from the batch;
        // simulate exactly that from the mock, on the same async flow.
        var client = new Mock<QueueClient>();
        client.Setup(x => x.Name).Returns("orders");
        var undecodable = QueuesModelFactory.QueueMessage("bad", "receipt-bad", "this is not base64!!", 4);
        var healthy = QueuesModelFactory.QueueMessage("1", "receipt-1", ValidJson, 1);
        client.Setup(x => x.ReceiveMessagesAsync(It.IsAny<int?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                DecodingFailures.TryCollect(new QueueMessageDecodingFailedEventArgs(client.Object, undecodable, null, false, CancellationToken.None)).Should().BeTrue();
                return Task.FromResult(Response.FromValue(new[] { healthy }, Mock.Of<Response>()));
            });
        var poison = new Mock<QueueClient>();

        var messages = await Create(client, () => poison.Object).ReceiveAsync<TestObject>(maxMessages: 10);

        messages.Should().HaveCount(2);
        messages.Single(m => m.Error is null).Body!.Property1.Should().Be("test");
        var bad = messages.Single(m => m.Error is not null);
        bad.MessageId.Should().Be("bad");
        bad.DequeueCount.Should().Be(4);
        bad.RawBody.ToString().Should().Be("this is not base64!!");

        var poisoned = CaptureSends(poison);
        await bad.DeadLetterAsync("undecodable");
        DeadLetterQueue.WithoutDeadLetterRecord(poisoned.Should().ContainSingle().Subject).ToString().Should().Be("this is not base64!!", "the text as it was on the queue is kept");
        client.Verify(x => x.DeleteMessageAsync("bad", "receipt-bad", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact(DisplayName = "Decoding failures are isolated per receive, even when two queues receive at the same moment")]
    public async Task Decoding_Failures_Are_Isolated_Per_Receive()
    {
        // Both receives are in flight together: each waits for the other to start before raising its decoding event.
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        StorageQueue Build(string badId)
        {
            var client = new Mock<QueueClient>();
            client.Setup(x => x.Name).Returns($"queue-{badId}");
            client.Setup(x => x.ReceiveMessagesAsync(It.IsAny<int?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult();
                    await bothStarted.Task;
                    DecodingFailures.TryCollect(new QueueMessageDecodingFailedEventArgs(client.Object, QueuesModelFactory.QueueMessage(badId, "r", "??", 1), null, false, CancellationToken.None)).Should().BeTrue();
                    return Response.FromValue(Array.Empty<Azure.Storage.Queues.Models.QueueMessage>(), Mock.Of<Response>());
                });
            return Create(client);
        }

        var one = Build("bad-1").ReceiveAsync<TestObject>().AsTask();
        var two = Build("bad-2").ReceiveAsync<TestObject>().AsTask();
        await Task.WhenAll(one, two).WaitAsync(TimeSpan.FromSeconds(10));

        (await one).Should().ContainSingle().Which.MessageId.Should().Be("bad-1");
        (await two).Should().ContainSingle().Which.MessageId.Should().Be("bad-2");
    }

    [Theory(DisplayName = "Receive rejects a message count the service would refuse")]
    [InlineData(0)]
    [InlineData(33)]
    public async Task Receive_Rejects_Out_Of_Range_Count(int maxMessages)
    {
        Func<Task> act = async () => await Create(ClientReturning()).ReceiveAsync<TestObject>(maxMessages);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact(DisplayName = "The client's default visibility timeout is used when the caller passes none")]
    public async Task Receive_Uses_Default_Visibility_Timeout()
    {
        var client = ClientReturning();
        await Create(client, visibilityTimeout: TimeSpan.FromMinutes(5)).ReceiveAsync<TestObject>();
        client.Verify(x => x.ReceiveMessagesAsync(1, TimeSpan.FromMinutes(5), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact(DisplayName = "F8: send and receive spans carry their tags, the original ones and the messaging semantic-convention attributes")]
    public async Task Send_And_Receive_Spans_Carry_Their_Tags()
    {
        using var spans = new SpanRecorder();
        var client = ClientReturning(("1", ValidJson));
        client.Setup(x => x.Uri).Returns(new Uri("https://account.queue.core.windows.net/orders"));
        CaptureSends(client);
        var queue = Create(client, telemetry: new QueueServiceTelemetrySettings { CreateNewActivityOnMessageSend = true, CreateNewActivityOnMessageRetrieval = true });

        using var test = new Activity("test").Start();
        await queue.SendAsync(new TestObject());
        await queue.ReceiveAsync<TestObject>(maxMessages: 3);

        var send = await spans.WaitForAsync(QueueServiceDiagnostics.Names.SendingMessage, test.TraceId);
        send.Kind.Should().Be(ActivityKind.Producer);
        send.GetTagItem("queue.name").Should().Be("orders");
        send.GetTagItem("messaging.system").Should().Be("azure.storage.queue");
        send.GetTagItem("messaging.destination.name").Should().Be("orders");
        send.GetTagItem("messaging.operation.type").Should().Be("send");
        send.GetTagItem("messaging.operation.name").Should().Be("send");
        send.GetTagItem("messaging.message.id").Should().Be("sent-1");
        send.GetTagItem("server.address").Should().Be("account.queue.core.windows.net");
        send.GetTagItem("server.port").Should().Be(443);

        var read = await spans.WaitForAsync(QueueServiceDiagnostics.Names.ReadingMessages, test.TraceId);
        read.Kind.Should().Be(ActivityKind.Client);
        read.GetTagItem("queue.name").Should().Be("orders");
        read.GetTagItem("queue.message_count").Should().Be(3);
        read.GetTagItem("messaging.operation.type").Should().Be("receive");
        read.GetTagItem("messaging.destination.name").Should().Be("orders");
        read.GetTagItem("messaging.batch.message_count").Should().Be(1, "the batch form of receive reports how many arrived");
    }

    [Fact(DisplayName = "A receiver unwraps an enveloped message before deserialization whatever its own setting, and a plain one still reads")]
    public async Task Propagation_Unwraps_Envelopes_And_Still_Reads_Plain_Messages()
    {
        var sendClient = new Mock<QueueClient>();
        var sent = CaptureSends(sendClient);
        ActivityContext sender;
        using (var caller = new Activity("caller").Start())
        {
            await Create(sendClient, propagateTraceContext: true).SendAsync(new TestObject { Property1 = "wrapped" });
            sender = caller.Context;
        }

        var wire = sent.Should().ContainSingle().Subject.ToString();
        wire.Should().StartWith($"{{\"$azqs-envelope\":1,\"traceparent\":\"00-{sender.TraceId}-{sender.SpanId}-");

        // The receiver's own setting is off: the setting governs sending only.
        var messages = await Create(ClientReturning(("1", wire), ("2", ValidJson))).ReceiveAsync<TestObject>(maxMessages: 2);

        var wrapped = messages.Single(m => m.MessageId == "1");
        wrapped.Body!.Property1.Should().Be("wrapped");
        wrapped.RawBody.ToString().Should().StartWith("{\"Property1\":\"wrapped\"", "RawBody is the message inside the envelope");
        wrapped.CreationContext!.Value.TraceId.Should().Be(sender.TraceId);
        wrapped.CreationContext!.Value.SpanId.Should().Be(sender.SpanId);

        var plain = messages.Single(m => m.MessageId == "2");
        plain.Body!.Property1.Should().Be("test");
        plain.CreationContext.Should().BeNull();
    }

    [Fact(DisplayName = "Dead-lettering keeps the envelope and records why; replaying strips the record and keeps the trace context")]
    public async Task DeadLetter_Keeps_The_Envelope()
    {
        using var caller = new Activity("caller").Start();
        var sent = new MessageEnvelope();
        sent.SetTraceContext(caller);
        sent.SetBody(BinaryData.FromString(ValidJson));
        var wire = sent.Write().ToString();
        var poison = new Mock<QueueClient>();
        var poisoned = CaptureSends(poison);
        var message = (await Create(ClientReturning(("1", wire)), () => poison.Object).ReceiveAsync<TestObject>())[0];

        await message.DeadLetterAsync("gave up");

        var copy = poisoned.Should().ContainSingle().Subject;
        MessageEnvelope.TryRead(copy, out var record, out _).Should().Be(EnvelopeKind.Envelope);
        record!.TraceParent.Should().Be(sent.TraceParent);
        record.DeadLetter.Should().BeEquivalentTo(new { Reason = "gave up", Category = DeadLetterCategories.Handler, MessageId = "1", DequeueCount = 2L, Queue = "orders" });

        var replayed = (await Create(ClientReturning(("1", DeadLetterQueue.WithoutDeadLetterRecord(copy).ToString()))).ReceiveAsync<TestObject>())[0];
        replayed.Body!.Property1.Should().Be("test");
        replayed.CreationContext!.Value.TraceId.Should().Be(caller.TraceId);
        replayed.WireBody.ToString().Should().Be(wire, "the replayed message is the one that was sent");
    }

    [Fact(DisplayName = "Complete deletes with the current receipt, settles, and refuses a second operation")]
    public async Task Complete_Deletes_And_Settles()
    {
        var client = ClientReturning(("1", ValidJson));
        var message = (await Create(client).ReceiveAsync<TestObject>())[0];

        await message.CompleteAsync();

        client.Verify(x => x.DeleteMessageAsync("1", "receipt-1", It.IsAny<CancellationToken>()), Times.Once);
        message.Settlement.Should().Be(MessageSettlement.Completed);
        await message.Invoking(m => m.CompleteAsync().AsTask()).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact(DisplayName = "Renewing the lock updates the receipt and a later complete uses the new one")]
    public async Task Renew_Then_Complete_Uses_New_Receipt()
    {
        var client = ClientReturning(("1", ValidJson));
        SetupUpdate(client, "receipt-1", TimeSpan.FromMinutes(2), "receipt-2");
        var message = (await Create(client).ReceiveAsync<TestObject>())[0];

        await message.RenewLockAsync(TimeSpan.FromMinutes(2));
        await message.CompleteAsync();

        message.PopReceipt.Should().Be("receipt-2");
        client.Verify(x => x.DeleteMessageAsync("1", "receipt-2", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact(DisplayName = "Abandon makes the message visible immediately and settles it")]
    public async Task Abandon_Makes_Visible_Now()
    {
        var client = ClientReturning(("1", ValidJson));
        SetupUpdate(client, "receipt-1", TimeSpan.Zero, "receipt-2");
        var message = (await Create(client).ReceiveAsync<TestObject>())[0];

        await message.AbandonAsync();

        message.Settlement.Should().Be(MessageSettlement.Abandoned);
        client.Verify(x => x.UpdateMessageAsync("1", "receipt-1", (string?)null, TimeSpan.Zero, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact(DisplayName = "Dead-letter copies the body to the poison queue with a record of why, never to expire, then deletes the original")]
    public async Task DeadLetter_Copies_Body_With_Record_Then_Deletes()
    {
        var client = ClientReturning(("1", ValidJson));
        var poison = new Mock<QueueClient>();
        var poisoned = CaptureSends(poison);
        var message = (await Create(client, () => poison.Object).ReceiveAsync<TestObject>())[0];

        await message.DeadLetterAsync("gave up");

        poison.Verify(x => x.CreateIfNotExistsAsync(It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()), Times.Once);
        poison.Verify(x => x.SendMessageAsync(It.IsAny<BinaryData>(), null, TimeSpan.FromSeconds(-1), It.IsAny<CancellationToken>()), Times.Once);
        MessageEnvelope.TryRead(poisoned.Single(), out var record, out _).Should().Be(EnvelopeKind.Envelope);
        record!.DeadLetter!.Reason.Should().Be("gave up");
        DeadLetterQueue.WithoutDeadLetterRecord(poisoned.Single()).ToString().Should().Be(ValidJson, "a plain message replays as it was sent");
        client.Verify(x => x.DeleteMessageAsync("1", "receipt-1", It.IsAny<CancellationToken>()), Times.Once);
        message.Settlement.Should().Be(MessageSettlement.DeadLettered);
    }

    [Fact(DisplayName = "Dead-letter without a poison queue configured throws instead of losing the message")]
    public async Task DeadLetter_Without_Poison_Queue_Throws()
    {
        var client = ClientReturning(("1", ValidJson));
        var message = (await Create(client).ReceiveAsync<TestObject>())[0];

        await message.Invoking(m => m.DeadLetterAsync().AsTask()).Should().ThrowAsync<InvalidOperationException>();
        client.Verify(x => x.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        message.IsSettled.Should().BeFalse();
    }

    [Fact(DisplayName = "Send serializes with the queue's serializer and passes the options through")]
    public async Task Send_Serializes_And_Passes_Options()
    {
        var client = new Mock<QueueClient>();
        var receipt = QueuesModelFactory.SendReceipt("1", DateTimeOffset.Now, DateTimeOffset.Now, "r", DateTimeOffset.Now);
        client.Setup(x => x.SendMessageAsync(It.IsAny<BinaryData>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(receipt, Mock.Of<Response>()));

        var result = await Create(client).SendAsync(new TestObject { Property1 = "sent" }, new SendOptions(TimeSpan.FromSeconds(10), TimeSpan.FromHours(1)));

        result.Should().BeSameAs(receipt);
        client.Verify(x => x.SendMessageAsync(It.Is<BinaryData>(b => b.ToString().Contains("\"Property1\":\"sent\"")), TimeSpan.FromSeconds(10), TimeSpan.FromHours(1), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory(DisplayName = "Send translates the .NET infinite TTL and preserves other TTL values")]
    [InlineData(null, null)]
    [InlineData(-1, -1000)]
    [InlineData(-1000, -1000)]
    [InlineData(3600000, 3600000)]
    public async Task Send_Normalizes_Infinite_TimeToLive(int? milliseconds, int? expectedMilliseconds)
    {
        var client = new Mock<QueueClient>();
        var receipt = QueuesModelFactory.SendReceipt("1", DateTimeOffset.Now, DateTimeOffset.Now, "r", DateTimeOffset.Now);
        client.Setup(x => x.SendMessageAsync(It.IsAny<BinaryData>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(receipt, Mock.Of<Response>()));
        TimeSpan? timeToLive = milliseconds is { } value ? TimeSpan.FromMilliseconds(value) : null;
        TimeSpan? expected = expectedMilliseconds is { } expectedValue ? TimeSpan.FromMilliseconds(expectedValue) : null;

        await Create(client).SendAsync(new TestObject(), new SendOptions(TimeToLive: timeToLive));

        client.Verify(x => x.SendMessageAsync(It.IsAny<BinaryData>(), null, expected, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact(DisplayName = "Peek returns messages without a receipt or operations")]
    public async Task Peek_Returns_Peeked_Messages()
    {
        var client = new Mock<QueueClient>();
        client.Setup(x => x.PeekMessagesAsync(It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(new[] { QueuesModelFactory.PeekedMessage("1", ValidJson, 3) }, Mock.Of<Response>()));

        var peeked = (await Create(client).PeekAsync<TestObject>()).Should().ContainSingle().Subject;

        peeked.MessageId.Should().Be("1");
        peeked.DequeueCount.Should().Be(3);
        peeked.Body!.Property1.Should().Be("test");
    }

    [Fact(DisplayName = "F7: reading properties creates the queue first when CreateIfNotExists is on")]
    public async Task GetProperties_Creates_Queue_First()
    {
        var client = new Mock<QueueClient>();
        var order = new List<string>();
        client.Setup(x => x.CreateIfNotExistsAsync(It.IsAny<IDictionary<string, string>>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("create")).ReturnsAsync(Mock.Of<Response>());
        client.Setup(x => x.GetPropertiesAsync(It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("properties")).ReturnsAsync(Response.FromValue(QueuesModelFactory.QueueProperties(new Dictionary<string, string>(), 0), Mock.Of<Response>()));

        await Create(client, createIfNotExists: true).GetPropertiesAsync();

        order.Should().Equal("create", "properties");
    }

    [Fact(DisplayName = "A test can build a QueueMessage with stubbed operations")]
    public async Task QueueMessage_Is_Buildable_With_Stubbed_Operations()
    {
        var operations = new Mock<IQueueMessageOperations>();
        var info = new QueueMessageInfo("1", "r", 1, null, null, null);
        var message = new QueueMessage<TestObject>(info, BinaryData.FromString(ValidJson), new TestObject { Property1 = "x" }, null, operations.Object);

        await message.CompleteAsync();

        operations.Verify(x => x.DeleteAsync("1", "r", It.IsAny<CancellationToken>()), Times.Once);
        message.Settlement.Should().Be(MessageSettlement.Completed);
    }
}
