using Azure.Storage.Queues;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AzureStorage.QueueService.Tests;

/// <summary>
/// The 3.1 queue features through the public API on in-memory queues, which run the same queue code as the service:
/// guardrails before a send, what travels with a message, the poison queue's record and replay, and reading.
/// </summary>
public class QueueFeatureTests
{
    private static ServiceProvider Services(Action<QueueServiceBuilder> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        services.AddAzureStorageQueueClient(configure);
        services.AddInMemoryQueues();
        return services.BuildServiceProvider();
    }

    private static IStorageQueue Orders(ServiceProvider provider) => provider.GetRequiredKeyedService<IStorageQueue>("orders");

    [Fact(DisplayName = "Send refuses, before calling the service, a message over 64 KiB as it would go on the queue, Base64 growth included, and a delay the message would expire in")]
    public async Task Send_Guardrails()
    {
        using var provider = Services(queues =>
        {
            queues.AddClient("orders", s => s.QueueName = "orders");
            queues.AddClient("base64", s => { s.QueueName = "base64"; s.ClientOptions = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 }; });
        });
        var fitsPlain = new TestObject { Property1 = new string('x', 55_000) };

        await Orders(provider).Invoking(q => q.SendAsync(new TestObject { Property1 = new string('x', 70_000) }).AsTask())
            .Should().ThrowAsync<QueueMessageTooLargeException>().WithMessage("*UseBlobStorageForLargeMessages*");
        await Orders(provider).SendAsync(fitsPlain);
        await provider.GetRequiredKeyedService<IQueueSender>("base64").Invoking(q => q.SendAsync(fitsPlain).AsTask())
            .Should().ThrowAsync<QueueMessageTooLargeException>("Base64 makes it a third bigger");
        await Orders(provider).Invoking(q => q.SendAsync(fitsPlain, new SendOptions(TimeToLive: TimeSpan.FromHours(1)) { Delay = TimeSpan.FromHours(2) }).AsTask())
            .Should().ThrowAsync<ArgumentOutOfRangeException>().WithMessage("*expire before*");
    }

    [Fact(DisplayName = "Receive, abandon and renew check their timeouts before calling the service")]
    public async Task Timeouts_Are_Checked()
    {
        using var provider = Services(queues => queues.AddClient("orders", s => s.QueueName = "orders"));
        await Orders(provider).SendAsync(new TestObject());

        await Orders(provider).Invoking(q => q.ReceiveAsync<TestObject>(visibilityTimeout: TimeSpan.FromMilliseconds(500)).AsTask()).Should().ThrowAsync<ArgumentOutOfRangeException>();
        var message = (await Orders(provider).ReceiveAsync<TestObject>())[0];
        await message.Invoking(m => m.AbandonAsync(TimeSpan.FromDays(8)).AsTask()).Should().ThrowAsync<ArgumentOutOfRangeException>();
        await message.Invoking(m => m.RenewVisibilityAsync(TimeSpan.FromDays(8)).AsTask()).Should().ThrowAsync<ArgumentOutOfRangeException>();
        message.IsSettled.Should().BeFalse("nothing reached the queue");
    }

    [Fact(DisplayName = "Headers and a compressed body travel with the message and come back as sent, whatever the receiving client's settings")]
    public async Task Headers_And_Compression_Round_Trip()
    {
        using var provider = Services(queues =>
        {
            queues.AddClient("sender", s => s.QueueName = "orders").UseCompression(thresholdBytes: 100);
            queues.AddClient("orders", s => s.QueueName = "orders");
        });
        var body = new TestObject { Property1 = new string('a', 5_000) };

        await provider.GetRequiredKeyedService<IQueueSender>("sender").SendAsync(body, new SendOptions { Headers = new Dictionary<string, string> { ["tenant"] = "contoso" } });
        var received = (await Orders(provider).ReceiveAsync<TestObject>())[0];

        received.Body!.Property1.Should().Be(body.Property1);
        received.Headers.Should().Contain("tenant", "contoso");
        received.WireBody.ToString().Should().Contain("\"encoding\":\"gzip\"");
        received.WireBody.ToMemory().Length.Should().BeLessThan(1_000, "5,000 repeated characters compress well");
    }

    [Fact(DisplayName = "The default serializer writes camelCase JSON and still reads PascalCase from older senders")]
    public void Default_Serializer_Writes_CamelCase()
    {
        var serializer = new JsonQueueMessageSerializer();

        serializer.Serialize(new TestObject { Property1 = "to JavaScript" }).ToString().Should().Contain("\"property1\":\"to JavaScript\"");
        serializer.Deserialize<TestObject>(BinaryData.FromString("{\"Property1\":\"from 3.0\",\"Property3\":3}"))
            .Should().BeEquivalentTo(new { Property1 = "from 3.0", Property3 = 3 });
    }

    [Fact(DisplayName = "A message in a later version's envelope comes back with Error set, so a processor dead-letters it intact for replay after upgrading")]
    public async Task Later_Version_Envelope_Is_Not_Misread()
    {
        using var provider = Services(queues => queues.AddClient("orders", s => s.QueueName = "orders"));
        await Orders(provider).SendAsync<object>(System.Text.Json.Nodes.JsonNode.Parse("{\"$azqs-envelope\":2,\"body\":{\"Property1\":\"x\"}}")!);

        var message = (await Orders(provider).ReceiveAsync<TestObject>())[0];

        message.Body.Should().BeNull();
        message.Error!.Message.Should().Contain("newer version");
    }

    [Fact(DisplayName = "The poison queue records why each message was dead-lettered, and a filtered replay moves only the matching ones back as they were sent")]
    public async Task Dead_Letter_Queue_Peek_And_Replay()
    {
        using var provider = Services(queues => queues.AddClient("orders", s => s.QueueName = "orders"));
        var orders = Orders(provider);
        await orders.SendAsync(new TestObject { Property1 = "retry me" });
        await orders.SendAsync(new TestObject { Property1 = "leave me" });
        foreach (var message in await orders.ReceiveAsync<TestObject>(maxMessages: 2))
            await message.DeadLetterAsync(message.Body!.Property1 == "retry me" ? "downstream was down" : "bad data");
        var deadLetters = provider.GetRequiredKeyedService<IDeadLetterQueue>("orders");

        var peeked = await deadLetters.PeekAsync();
        peeked.Should().HaveCount(2).And.OnlyContain(m => m.SourceQueueName == "orders" && m.DequeueCount == 1 && m.OriginalMessageId != null);
        peeked.Select(m => m.GetBody<TestObject>()!.Property1).Should().BeEquivalentTo("retry me", "leave me");

        (await deadLetters.ReplayAsync(filter: m => m.Reason == "downstream was down")).Should().Be(1);

        var replayed = (await orders.ReceiveAsync<TestObject>()).Should().ContainSingle().Subject;
        replayed.Body!.Property1.Should().Be("retry me");
        replayed.DequeueCount.Should().Be(1, "a replayed message starts again");
        replayed.WireBody.ToString().Should().NotContain("deadLetter", "the record is stripped on replay");
        (await deadLetters.PeekAsync()).Should().ContainSingle("the skipped one stays, visible").Which.Reason.Should().Be("bad data");
    }

    [Fact(DisplayName = "ReadAllAsync reports a failed receive, backs off on errors and empty polls, and yields messages as they arrive")]
    public async Task ReadAllAsync_Backs_Off_And_Yields()
    {
        var time = new TimerCountingClock();
        var receiver = new ScriptedReceiver();
        var errors = new List<Exception>();
        using var stop = new CancellationTokenSource();
        await using var reading = receiver.ReadAllAsync<TestObject>(new ReadAllOptions { TimeProvider = time, OnReceiveError = errors.Add }, stop.Token).GetAsyncEnumerator();

        var next = reading.MoveNextAsync();     // the first receive throws, then the loop waits
        await time.WaitForTimersAsync(1);
        errors.Should().ContainSingle();
        time.Advance(TimeSpan.FromSeconds(1));  // the second receive is empty, then the loop waits again
        await time.WaitForTimersAsync(2);
        time.Advance(TimeSpan.FromSeconds(1));  // the third has a message

        (await next.AsTask().WaitAsync(TimeSpan.FromSeconds(10))).Should().BeTrue();
        reading.Current.MessageId.Should().Be("m1");
        stop.Cancel();
        (await reading.MoveNextAsync()).Should().BeFalse("cancellation ends the loop without throwing");
    }

    private sealed class ScriptedReceiver : IQueueReceiver
    {
        private int _calls;

        public string QueueName => "orders";

        public ValueTask<IReadOnlyList<QueueMessage<T>>> ReceiveAsync<T>(int maxMessages = 1, TimeSpan? visibilityTimeout = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Interlocked.Increment(ref _calls) switch
            {
                1 => throw new TimeoutException("storage is down"),
                3 => new([new QueueMessage<T>(new QueueMessageInfo("m1", "r", 1, null, null, null), BinaryData.FromString("{}"), default, null, new NoOperations())]),
                _ => new(Array.Empty<QueueMessage<T>>()),
            };
        }

        public ValueTask<IReadOnlyList<PeekedQueueMessage<T>>> PeekAsync<T>(int maxMessages = 1, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoOperations : IQueueMessageOperations
    {
        public ValueTask DeleteAsync(string messageId, string popReceipt, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<VisibilityReceipt> UpdateVisibilityAsync(string messageId, string popReceipt, TimeSpan visibilityTimeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DeadLetterAsync(string messageId, string popReceipt, long dequeueCount, BinaryData rawBody, string? reason, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>A fake clock that reports timer registrations, so the test advances only once the loop is waiting.</summary>
    private sealed class TimerCountingClock : FakeTimeProvider
    {
        private int _timers;
        private readonly Signal _created = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timers);
            _created.Set();
            return timer;
        }

        public Task WaitForTimersAsync(int count) => _created.WaitUntilAsync(() => Volatile.Read(ref _timers) >= count);
    }
}
