using System.Diagnostics;
using Azure;
using Azure.Storage.Queues.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit.Abstractions;

namespace AzureStorage.QueueService.Tests;

/// <summary>
/// <c>AddInMemoryQueues</c> the way a consumer would use it: a real host, the real processor, a handler, and a fake
/// clock — no storage account, no emulator, and no waiting on real time beyond a safety net against a hung test.
/// </summary>
public class InMemoryQueueTests(ITestOutputHelper output)
{
    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private sealed class HandlerState
    {
        public Func<QueueMessage<TestObject>, ValueTask> Behaviour { get; init; } = _ => ValueTask.CompletedTask;
        public List<(string? Property1, long DequeueCount, DateTimeOffset At)> Attempts { get; } = new();
        public TaskCompletionSource FirstAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RecordingHandler(HandlerState state, TimeProvider time) : IQueueMessageHandler<TestObject>
    {
        public ValueTask HandleAsync(QueueMessage<TestObject> message, CancellationToken cancellationToken)
        {
            lock (state.Attempts) state.Attempts.Add((message.Body?.Property1, message.DequeueCount, time.GetUtcNow()));
            state.FirstAttempt.TrySetResult();
            return state.Behaviour(message);
        }
    }

    /// <summary>
    /// A fake clock that reports each time an idle worker starts waiting to poll again, so the test advances time only
    /// once the processor is actually waiting. Advancing earlier would do nothing and leave the test to thread timing.
    /// </summary>
    private sealed class PollingClock : FakeTimeProvider
    {
        private int _polls;
        private readonly Signal _idle = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            // Only the empty-queue backoff; lease renewal timers have a different interval.
            if (dueTime == PollInterval)
            {
                Interlocked.Increment(ref _polls);
                _idle.Set();
            }
            return timer;
        }

        /// <summary>Waits until the <paramref name="count"/>th idle wait (counted from the start) has begun.</summary>
        public Task WaitForIdleAsync(int count) => _idle.WaitUntilAsync(() => Volatile.Read(ref _polls) >= count);
    }

    private IHost BuildHost(TimeProvider time, HandlerState state)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.AddProvider(new XUnitLoggerProvider(output));
        builder.Services.AddSingleton(time);
        builder.Services.AddSingleton(state);
        builder.Services.AddAzureStorageQueueClient(queues =>
        {
            // No connection details anywhere, and ValidateOnStart still passes: in-memory queues need none.
            queues.AddClient("orders", settings => settings.QueueName = "orders")
                  .AddProcessor<TestObject, RecordingHandler>(options =>
                  {
                      options.MaxDequeueCount = 2;
                      options.EmptyQueueBackoffMin = PollInterval;
                      options.EmptyQueueBackoffMax = PollInterval;
                  });
            // How a test reads the poison queue: a client for it, sharing the in-memory queue of that name.
            queues.AddClient("orders-poison", settings => settings.QueueName = "orders-poison");
            queues.ValidateOnStart();
        });
        builder.Services.AddInMemoryQueues();
        return builder.Build();
    }

    [Fact(DisplayName = "In memory: a message sent while the processor is idle is handled after the next poll and completed")]
    public async Task Sent_Message_Is_Handled_And_Completed()
    {
        var time = new PollingClock();
        var state = new HandlerState();
        using var host = BuildHost(time, state);
        await host.StartAsync();

        await time.WaitForIdleAsync(1);
        await host.Services.GetRequiredKeyedService<IQueueSender>("orders").SendAsync(new TestObject { Property1 = "hello" });
        time.Advance(PollInterval);
        await state.FirstAttempt.Task.WaitAsync(SafetyNet);
        await host.StopAsync(); // drains: the delivery, completion included, finishes before this returns

        state.Attempts.Should().ContainSingle().Which.Property1.Should().Be("hello");
        var orders = host.Services.GetRequiredKeyedService<IStorageQueue>("orders");
        (await orders.GetPropertiesAsync()).ApproximateMessagesCount.Should().Be(0, "the processor completed the message");
    }

    [Fact(DisplayName = "In memory: a handler that always throws is retried after the retry delay, then dead-lettered past MaxDequeueCount")]
    public async Task Failing_Handler_Ends_Up_In_The_Poison_Queue()
    {
        var time = new PollingClock();
        var state = new HandlerState { Behaviour = _ => throw new InvalidOperationException("always fails") };
        using var host = BuildHost(time, state);
        await host.Services.GetRequiredKeyedService<IQueueSender>("orders").SendAsync(new TestObject { Property1 = "doomed" });
        var start = time.GetUtcNow();
        await host.StartAsync();

        // Each time the worker goes idle, move the clock on by one poll, until the message reaches the poison queue.
        var poison = host.Services.GetRequiredKeyedService<IQueueReceiver>("orders-poison");
        for (var idle = 1; (await poison.PeekAsync<TestObject>()).Count == 0; idle++)
        {
            await time.WaitForIdleAsync(idle);
            time.Advance(PollInterval);
        }
        await host.StopAsync();

        state.Attempts.Select(a => (a.DequeueCount, a.At - start)).Should().Equal(
            new[] { (1L, TimeSpan.Zero), (2L, TimeSpan.FromSeconds(5)) },
            "the abandoned message stays hidden for the 5 second retry delay, and a third delivery is over the limit");
        var deadLettered = (await poison.PeekAsync<TestObject>()).Should().ContainSingle().Subject;
        deadLettered.Body!.Property1.Should().Be("doomed");
        deadLettered.DequeueCount.Should().Be(0, "a dead-lettered message starts afresh in the poison queue");
        var orders = host.Services.GetRequiredKeyedService<IStorageQueue>("orders");
        (await orders.GetPropertiesAsync()).ApproximateMessagesCount.Should().Be(0);
    }

    [Fact(DisplayName = "In memory: once a lease lapses and another consumer receives the message, the first receipt no longer settles it")]
    public async Task Stale_Pop_Receipt_Fails_Like_The_Service()
    {
        var time = new FakeTimeProvider();
        var services = new ServiceCollection().AddLogging().AddSingleton<TimeProvider>(time);
        services.AddAzureStorageQueueClient(queues => queues.AddDefaultClient(settings => settings.QueueName = "orders"));
        services.AddInMemoryQueues();
        using var provider = services.BuildServiceProvider();
        var queue = provider.GetRequiredService<IStorageQueue>();

        await queue.SendAsync(new TestObject { Property1 = "a" });
        var first = (await queue.ReceiveAsync<TestObject>(visibilityTimeout: TimeSpan.FromSeconds(30))).Single();
        (await queue.ReceiveAsync<TestObject>()).Should().BeEmpty("the message is hidden while the first consumer holds it");

        time.Advance(TimeSpan.FromSeconds(30));
        var second = (await queue.ReceiveAsync<TestObject>()).Single();

        second.MessageId.Should().Be(first.MessageId);
        second.DequeueCount.Should().Be(2);
        var stale = await first.Invoking(m => m.CompleteAsync().AsTask()).Should().ThrowAsync<RequestFailedException>();
        stale.Which.Status.Should().Be(404);
        stale.Which.ErrorCode.Should().Be(QueueErrorCode.MessageNotFound.ToString());
        await second.CompleteAsync();
        (await queue.GetPropertiesAsync()).ApproximateMessagesCount.Should().Be(0);
    }

    [Fact(DisplayName = "In memory: PropagateTraceContext wraps what is sent, receive unwraps it, and dead-lettering keeps the envelope for replay")]
    public async Task Trace_Context_Envelope_Matches_Production()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAzureStorageQueueClient(queues =>
        {
            queues.AddDefaultClient(settings => { settings.QueueName = "orders"; settings.PropagateTraceContext = true; });
            queues.AddClient("orders-poison", settings => settings.QueueName = "orders-poison");
        });
        services.AddInMemoryQueues();
        using var provider = services.BuildServiceProvider();
        var queue = provider.GetRequiredService<IStorageQueue>();

        ActivityTraceId traceId;
        using (var caller = new Activity("caller").Start())
        {
            traceId = caller.TraceId;
            await queue.SendAsync(new TestObject { Property1 = "traced" });
        }
        var received = (await queue.ReceiveAsync<TestObject>()).Single();

        received.Body!.Property1.Should().Be("traced");
        received.CreationContext!.Value.TraceId.Should().Be(traceId);
        received.RawBody.ToString().Should().NotContain("$azqs-envelope", "the handler sees the message the sender serialized");
        await received.DeadLetterAsync("test");

        var replayed = (await provider.GetRequiredKeyedService<IStorageQueue>("orders-poison").ReceiveAsync<TestObject>()).Single();
        replayed.Body!.Property1.Should().Be("traced");
        replayed.CreationContext!.Value.TraceId.Should().Be(traceId);
    }
}
