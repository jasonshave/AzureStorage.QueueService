using Azure.Storage.Queues.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Xunit.Abstractions;

namespace AzureStorage.QueueService.Tests;

/// <summary>
/// Drives the processor against a scripted queue and a fake clock. Nothing here waits on real time: every await is
/// on a signal the processor raises (a receive, a settle, a handler start, a timer registration), with a generous
/// real-time cap only as a safety net against a hung test.
/// </summary>
public class QueueProcessorTests(ITestOutputHelper output)
{
    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(10);

    // ---------------------------------------------------------------------------------------------------------
    // Test doubles
    // ---------------------------------------------------------------------------------------------------------

    private sealed class ScopeMarker;

    private sealed class HandlerState
    {
        public Func<QueueMessage<TestObject>, ScopeMarker, CancellationToken, ValueTask> Behaviour { get; set; } = (_, _, _) => ValueTask.CompletedTask;
        public bool ThrowOnConstruct { get; set; }
        public List<(string MessageId, ScopeMarker Scope)> Invocations { get; } = new();
        public Signal Started { get; } = new();
    }

    private sealed class RecordingHandler : IQueueMessageHandler<TestObject>
    {
        private readonly HandlerState _state;
        private readonly ScopeMarker _scope;

        public RecordingHandler(HandlerState state, ScopeMarker scope)
        {
            if (state.ThrowOnConstruct) throw new InvalidOperationException("a dependency is missing");
            _state = state;
            _scope = scope;
        }

        public async ValueTask HandleAsync(QueueMessage<TestObject> message, CancellationToken cancellationToken)
        {
            lock (_state.Invocations) _state.Invocations.Add((message.MessageId, _scope));
            _state.Started.Set();
            await _state.Behaviour(message, _scope, cancellationToken);
        }
    }

    /// <summary>
    /// A "something happened" signal. WaitUntilAsync checks the condition, and if it is not yet true waits for the
    /// next Set() and checks again — grabbing the pending task *before* checking, so a Set() that lands between the
    /// check and the wait is never missed.
    /// </summary>
    private sealed class Signal
    {
        private TaskCompletionSource _tcs = New();
        private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Set() => Interlocked.Exchange(ref _tcs, New()).TrySetResult();

        public async Task WaitUntilAsync(Func<bool> condition)
        {
            while (true)
            {
                var next = Volatile.Read(ref _tcs).Task;
                if (condition()) return;
                await next.WaitAsync(SafetyNet);
            }
        }
    }

    /// <summary>Each receive hands out the next scripted batch (one message, or empty once the script runs out).</summary>
    private sealed class FakeQueue : IStorageQueue
    {
        private readonly Queue<Func<QueueMessage<TestObject>[]>> _batches = new();
        public List<(int Take, TimeSpan? Visibility)> Receives { get; } = new();
        public Signal Received { get; } = new();
        public string QueueName => "orders";

        public FakeQueue Then(QueueMessage<TestObject> message) { lock (_batches) _batches.Enqueue(() => new[] { message }); return this; }
        public FakeQueue ThenThrow(Exception exception) { lock (_batches) _batches.Enqueue(() => throw exception); return this; }

        public Task WaitForReceivesAsync(int count) => Received.WaitUntilAsync(() => { lock (Receives) return Receives.Count >= count; });

        public ValueTask<IReadOnlyList<QueueMessage<T>>> ReceiveAsync<T>(int maxMessages = 1, TimeSpan? visibilityTimeout = null, CancellationToken cancellationToken = default)
        {
            lock (Receives) Receives.Add((maxMessages, visibilityTimeout));
            Func<QueueMessage<TestObject>[]>? next;
            lock (_batches) next = _batches.Count > 0 ? _batches.Dequeue() : null;
            try
            {
                var batch = next is null ? Array.Empty<QueueMessage<TestObject>>() : next();
                return new((IReadOnlyList<QueueMessage<T>>)(object)batch);
            }
            finally
            {
                Received.Set();
            }
        }

        public ValueTask<IReadOnlyList<PeekedQueueMessage<T>>> PeekAsync<T>(int maxMessages = 1, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SendReceipt> SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask CreateIfNotExistsAsync(IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask ClearAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<QueueProperties> GetPropertiesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Records every settle operation and raises a signal for each, so tests await outcomes instead of sleeping.</summary>
    private sealed class Operations : IQueueMessageOperations
    {
        public List<(string Op, string MessageId, string Receipt, TimeSpan? Timeout, string? Reason)> Calls { get; } = new();
        public Signal Called { get; } = new();
        public Exception? DeadLetterFailure { get; set; }
        /// <summary>When set, visibility updates block on this until it is completed — simulates a renewal request in flight.</summary>
        public TaskCompletionSource? HoldUpdates { get; set; }
        private int _receipts;

        public ValueTask DeleteAsync(string messageId, string popReceipt, CancellationToken cancellationToken)
        {
            Record("delete", messageId, popReceipt, null, null);
            return ValueTask.CompletedTask;
        }

        public async ValueTask<VisibilityReceipt> UpdateVisibilityAsync(string messageId, string popReceipt, TimeSpan visibilityTimeout, CancellationToken cancellationToken)
        {
            Record("update", messageId, popReceipt, visibilityTimeout, null);
            if (HoldUpdates is { } hold) await hold.Task;
            return new VisibilityReceipt($"r{Interlocked.Increment(ref _receipts)}", DateTimeOffset.UtcNow + visibilityTimeout);
        }

        public ValueTask DeadLetterAsync(string messageId, string popReceipt, long dequeueCount, BinaryData rawBody, string? reason, CancellationToken cancellationToken)
        {
            Record("deadletter", messageId, popReceipt, null, reason);
            if (DeadLetterFailure is not null) throw DeadLetterFailure;
            return ValueTask.CompletedTask;
        }

        private void Record(string op, string id, string receipt, TimeSpan? timeout, string? reason)
        {
            lock (Calls) Calls.Add((op, id, receipt, timeout, reason));
            Called.Set();
        }

        public Task WaitForAsync(string op, int count = 1) =>
            Called.WaitUntilAsync(() => { lock (Calls) return Calls.Count(c => c.Op == op) >= count; });

        public int Count(string op) { lock (Calls) return Calls.Count(c => c.Op == op); }
    }

    /// <summary>
    /// A fake clock that reports timer registrations, so a test can wait until the processor is actually waiting on a
    /// timer before advancing time past it. Advancing before the processor's background thread has registered its
    /// timer does nothing, and the test would then pass or fail on thread timing.
    /// </summary>
    private sealed class ObservableTimeProvider : FakeTimeProvider
    {
        private int _timers;
        private readonly Signal _timerCreated = new();

        public int Timers => Volatile.Read(ref _timers);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timers);
            _timerCreated.Set();
            return timer;
        }

        public Task WaitForTimersAsync(int count) => _timerCreated.WaitUntilAsync(() => Timers >= count);
    }

    private sealed class Harness : IAsyncDisposable
    {
        public FakeQueue Queue { get; } = new();
        public Operations Ops { get; } = new();
        public HandlerState State { get; } = new();
        public ObservableTimeProvider Time { get; } = new();
        public QueueProcessor<TestObject, RecordingHandler> Processor { get; }

        public Harness(ITestOutputHelper output, Action<QueueProcessorOptions>? configure = null, IStorageQueue? queue = null)
        {
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.AddProvider(new XUnitLoggerProvider(output)));
            services.AddSingleton(State);
            services.AddScoped<ScopeMarker>();
            services.AddScoped<RecordingHandler>();
            services.Configure<QueueProcessorOptions>("opts", o =>
            {
                o.EmptyQueueBackoffMin = TimeSpan.FromSeconds(1);
                o.EmptyQueueBackoffMax = TimeSpan.FromSeconds(8);
                configure?.Invoke(o);
            });
            var provider = services.BuildServiceProvider();

            var factory = new Mock<IQueueClientFactory>();
            factory.Setup(x => x.GetQueue("orders")).Returns(queue ?? Queue);

            Processor = new QueueProcessor<TestObject, RecordingHandler>(
                "orders", "opts", factory.Object,
                provider.GetRequiredService<IServiceScopeFactory>(),
                provider.GetRequiredService<IOptionsFactory<QueueProcessorOptions>>(),
                provider.GetRequiredService<ILoggerFactory>(),
                Time);
        }

        public QueueMessage<TestObject> Message(string id, long dequeueCount = 1, DeserializationException? error = null) =>
            new(new QueueMessageInfo(id, "r0", dequeueCount, null, null, null), BinaryData.FromString("{}"), error is null ? new TestObject { Property1 = id } : null, error, Ops);

        public async Task StartAsync()
        {
            await Processor.StartAsync(CancellationToken.None);
            await Processor.StartedAsync(CancellationToken.None);
        }

        public Task StopAsync() => Processor.StopAsync(CancellationToken.None).WaitAsync(SafetyNet);

        public Task WaitForHandlerStartAsync(int count = 1) =>
            State.Started.WaitUntilAsync(() => { lock (State.Invocations) return State.Invocations.Count >= count; });

        /// <summary>
        /// Waits until the processor has registered its <paramref name="timerNumber"/>th timer (counted from the start
        /// of the test), then moves the clock forward. Absolute numbers, because "the next timer" is ambiguous when the
        /// processor may already have registered it.
        /// </summary>
        public async Task AdvancePastTimerAsync(int timerNumber, TimeSpan by)
        {
            await Time.WaitForTimersAsync(timerNumber);
            Time.Advance(by);
        }

        public async ValueTask DisposeAsync()
        {
            try { await StopAsync(); } catch (Exception) { /* a test that cancelled shutdown on purpose */ }
        }
    }

    // ---------------------------------------------------------------------------------------------------------
    // Delivery
    // ---------------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "Each worker receives one message, handles it in its own scope, and completes it")]
    public async Task Each_Worker_Handles_One_Message_In_Its_Own_Scope()
    {
        await using var h = new Harness(output, o => o.MaxConcurrentMessages = 2);
        h.Queue.Then(h.Message("1")).Then(h.Message("2"));

        await h.StartAsync();
        await h.Ops.WaitForAsync("delete", count: 2);

        h.State.Invocations.Select(i => i.MessageId).Should().BeEquivalentTo("1", "2");
        h.State.Invocations.Select(i => i.Scope).Distinct().Should().HaveCount(2, "every message gets a fresh scope");
        h.Queue.Receives.Should().OnlyContain(r => r.Take == 1 && r.Visibility == TimeSpan.FromMinutes(1));
    }

    [Fact(DisplayName = "A throwing handler abandons the message with the retry delay for its dequeue count")]
    public async Task Throwing_Handler_Abandons_With_Retry_Delay()
    {
        await using var h = new Harness(output);
        h.State.Behaviour = (_, _, _) => throw new InvalidOperationException("boom");
        h.Queue.Then(h.Message("1", dequeueCount: 3));

        await h.StartAsync();
        await h.Ops.WaitForAsync("update");

        h.Ops.Calls.Should().ContainSingle(c => c.Op == "update").Which.Timeout.Should().Be(TimeSpan.FromSeconds(20), "5 s doubled twice for the third attempt");
        h.Ops.Count("delete").Should().Be(0);
    }

    [Fact(DisplayName = "A message over the dequeue limit is dead-lettered without calling the handler")]
    public async Task Over_Dequeue_Limit_Is_Dead_Lettered()
    {
        await using var h = new Harness(output, o => o.MaxDequeueCount = 5);
        h.Queue.Then(h.Message("1", dequeueCount: 6));

        await h.StartAsync();
        await h.Ops.WaitForAsync("deadletter");

        h.Ops.Calls.Should().ContainSingle(c => c.Op == "deadletter").Which.Reason.Should().Contain("6");
        h.State.Invocations.Should().BeEmpty();
    }

    [Fact(DisplayName = "A message that failed to decode or deserialize is dead-lettered without calling the handler")]
    public async Task Deserialization_Failure_Is_Dead_Lettered()
    {
        await using var h = new Harness(output);
        h.Queue.Then(h.Message("1", error: new DeserializationException("bad json")));

        await h.StartAsync();
        await h.Ops.WaitForAsync("deadletter");

        h.Ops.Calls.Should().ContainSingle(c => c.Op == "deadletter").Which.Reason.Should().Contain("bad json");
        h.State.Invocations.Should().BeEmpty();
    }

    [Fact(DisplayName = "When dead-lettering fails the message is abandoned instead of lost")]
    public async Task Dead_Letter_Failure_Falls_Back_To_Abandon()
    {
        await using var h = new Harness(output);
        h.Ops.DeadLetterFailure = new UnauthorizedAccessException("no permission to create the poison queue");
        h.Queue.Then(h.Message("1", dequeueCount: 6));

        await h.StartAsync();
        await h.Ops.WaitForAsync("update");

        h.Ops.Calls.Select(c => c.Op).Should().ContainInOrder("deadletter", "update");
    }

    [Fact(DisplayName = "A handler that settles the message itself is left alone")]
    public async Task Handler_That_Settles_Itself_Is_Left_Alone()
    {
        await using var h = new Harness(output);
        h.State.Behaviour = async (message, _, _) => await message.DeadLetterAsync("handler decided");
        h.Queue.Then(h.Message("1"));

        await h.StartAsync();
        await h.Ops.WaitForAsync("deadletter");
        await h.Queue.WaitForReceivesAsync(2); // the worker has moved on, so delivery of message 1 is finished

        h.Ops.Count("deadletter").Should().Be(1);
        h.Ops.Count("delete").Should().Be(0, "the processor must not complete a message the handler dead-lettered");
    }

    [Fact(DisplayName = "A handler that completes the message and then throws keeps the completion; the message is not abandoned")]
    public async Task Handler_That_Settles_Then_Throws_Keeps_The_Settlement()
    {
        await using var h = new Harness(output);
        h.State.Behaviour = async (message, _, _) =>
        {
            await message.CompleteAsync();
            throw new InvalidOperationException("failed while cleaning up");
        };
        h.Queue.Then(h.Message("1"));

        await h.StartAsync();
        await h.Ops.WaitForAsync("delete");
        await h.Queue.WaitForReceivesAsync(2); // the worker has moved on, so delivery of message 1 is finished

        h.Ops.Count("delete").Should().Be(1);
        h.Ops.Count("update").Should().Be(0, "a completed message must not be abandoned");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Lease renewal
    // ---------------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "The lease is renewed at half the visibility timeout while the handler runs, and completion uses the renewed receipt")]
    public async Task Lease_Is_Renewed_While_Handler_Runs()
    {
        await using var h = new Harness(output, o => o.VisibilityTimeout = TimeSpan.FromMinutes(1));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.State.Behaviour = async (_, _, _) => await release.Task;
        h.Queue.Then(h.Message("1"));

        await h.StartAsync();
        await h.WaitForHandlerStartAsync();

        await h.AdvancePastTimerAsync(1, TimeSpan.FromSeconds(30)); // the renewal timer
        await h.Ops.WaitForAsync("update");
        var renewal = h.Ops.Calls.Should().ContainSingle(c => c.Op == "update").Subject;
        renewal.Timeout.Should().Be(TimeSpan.FromMinutes(1));
        renewal.Receipt.Should().Be("r0");

        release.SetResult();
        await h.Ops.WaitForAsync("delete");
        h.Ops.Calls.Should().ContainSingle(c => c.Op == "delete").Which.Receipt.Should().Be("r1", "the delete must use the receipt the renewal returned");
    }

    [Fact(DisplayName = "R2: lease renewal is armed before the message is deserialized, so a slow serializer cannot let the lease lapse")]
    public async Task Renewal_Is_Armed_Before_Deserialization()
    {
        using var block = new ManualResetEventSlim(false);
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = true;
        var client = new Mock<Azure.Storage.Queues.QueueClient>();
        client.Setup(x => x.Name).Returns("orders");
        client.Setup(x => x.ReceiveMessagesAsync(It.IsAny<int?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var batch = first ? new[] { QueuesModelFactory.QueueMessage("1", "r0", "{}", 1) } : Array.Empty<Azure.Storage.Queues.Models.QueueMessage>();
                first = false;
                return Azure.Response.FromValue(batch, Mock.Of<Azure.Response>());
            });
        client.Setup(x => x.UpdateMessageAsync("1", "r0", (string?)null, TimeSpan.FromMinutes(1), It.IsAny<CancellationToken>()))
            .Callback(() => updated.TrySetResult())
            .ReturnsAsync(Azure.Response.FromValue(QueuesModelFactory.UpdateReceipt("r1", DateTimeOffset.UtcNow.AddMinutes(1)), Mock.Of<Azure.Response>()));
        client.Setup(x => x.DeleteMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => deleted.TrySetResult())
            .ReturnsAsync(Mock.Of<Azure.Response>());

        var loggerFactory = new ServiceCollection().AddLogging().BuildServiceProvider().GetRequiredService<ILoggerFactory>();
        var queue = new StorageQueue(client.Object, new BlockingSerializer(block), loggerFactory, new Telemetry.QueueServiceTelemetrySettings(), null, false, null);
        await using var h = new Harness(output, o => o.VisibilityTimeout = TimeSpan.FromMinutes(1), queue);

        await h.StartAsync();
        await h.Time.WaitForTimersAsync(1); // the renewal timer exists...
        block.IsSet.Should().BeFalse("...while deserialization has not been allowed to finish");

        h.Time.Advance(TimeSpan.FromSeconds(30)); // a renewal fires during deserialization
        await updated.Task.WaitAsync(SafetyNet);

        block.Set();
        await deleted.Task.WaitAsync(SafetyNet);
        client.Verify(x => x.DeleteMessageAsync("1", "r1", It.IsAny<CancellationToken>()), Times.Once, "completion uses the receipt from the renewal that ran during deserialization");
    }

    private sealed class BlockingSerializer(ManualResetEventSlim block) : IQueueMessageSerializer
    {
        public BinaryData Serialize<T>(T value) => BinaryData.FromString("{}");

        public T? Deserialize<T>(BinaryData data)
        {
            block.Wait(SafetyNet);
            return (T)(object)new TestObject { Property1 = "slow" };
        }
    }

    [Fact(DisplayName = "F3: finishing the handler while a renewal is in flight waits for it instead of cancelling it")]
    public async Task Completion_Waits_For_In_Flight_Renewal()
    {
        await using var h = new Harness(output, o => o.VisibilityTimeout = TimeSpan.FromMinutes(1));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.State.Behaviour = async (_, _, _) => await release.Task;
        h.Ops.HoldUpdates = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Queue.Then(h.Message("1"));

        await h.StartAsync();
        await h.WaitForHandlerStartAsync();

        await h.AdvancePastTimerAsync(1, TimeSpan.FromSeconds(30));
        await h.Ops.WaitForAsync("update"); // the renewal request is now "on the wire" and blocked

        release.SetResult();              // the handler finishes while the renewal is still pending
        await Task.Yield();
        h.Ops.Count("delete").Should().Be(0, "completion must wait for the renewal's receipt");

        h.Ops.HoldUpdates.SetResult();    // the service answers with the new receipt
        await h.Ops.WaitForAsync("delete");
        h.Ops.Calls.Should().ContainSingle(c => c.Op == "delete").Which.Receipt.Should().Be("r1");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Polling
    // ---------------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "An empty queue is polled with exponential backoff")]
    public async Task Empty_Queue_Backs_Off_Exponentially()
    {
        await using var h = new Harness(output);

        await h.StartAsync();
        await h.Queue.WaitForReceivesAsync(1);

        await h.AdvancePastTimerAsync(1, TimeSpan.FromSeconds(1)); // first backoff: 1 s
        await h.Queue.WaitForReceivesAsync(2);

        await h.AdvancePastTimerAsync(2, TimeSpan.FromSeconds(1)); // second backoff: 2 s, so 1 s is not enough
        h.Queue.Receives.Should().HaveCount(2);
        h.Time.Advance(TimeSpan.FromSeconds(1));
        await h.Queue.WaitForReceivesAsync(3);
    }

    [Fact(DisplayName = "A failed receive is logged, backed off, and polling continues")]
    public async Task Receive_Failure_Backs_Off_And_Continues()
    {
        await using var h = new Harness(output);
        h.Queue.ThenThrow(new Azure.RequestFailedException(503, "storage is busy")).Then(h.Message("1"));

        await h.StartAsync();
        await h.Queue.WaitForReceivesAsync(1);

        await h.AdvancePastTimerAsync(1, TimeSpan.FromSeconds(1)); // the error backoff
        await h.Ops.WaitForAsync("delete");

        h.State.Invocations.Should().ContainSingle();
    }

    [Fact(DisplayName = "F4: a handler that blocks synchronously does not hold up the other workers")]
    public async Task Blocking_Handler_Does_Not_Hold_Up_Other_Workers()
    {
        await using var h = new Harness(output, o => { o.MaxConcurrentMessages = 2; o.RenewVisibility = false; });
        using var block = new ManualResetEventSlim(false);
        h.State.Behaviour = (message, _, _) =>
        {
            if (message.MessageId == "1") block.Wait(SafetyNet); // synchronous, thread-blocking work
            return ValueTask.CompletedTask;
        };
        h.Queue.Then(h.Message("1")).Then(h.Message("2"));

        await h.StartAsync();
        await h.Ops.WaitForAsync("delete"); // message 2 completes while message 1's handler is still blocked

        h.Ops.Calls.Should().ContainSingle(c => c.Op == "delete").Which.MessageId.Should().Be("2");
        block.Set();
        await h.Ops.WaitForAsync("delete", count: 2);
    }

    // ---------------------------------------------------------------------------------------------------------
    // Failures outside the handler
    // ---------------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "F5: a handler whose constructor throws is logged and its message abandoned for retry")]
    public async Task Handler_Construction_Failure_Is_Abandoned()
    {
        await using var h = new Harness(output);
        h.State.ThrowOnConstruct = true;
        h.Queue.Then(h.Message("1"));

        await h.StartAsync();
        await h.Ops.WaitForAsync("update");
        await h.Queue.WaitForReceivesAsync(2); // the worker carries on

        h.Ops.Calls.Should().ContainSingle(c => c.Op == "update").Which.Timeout.Should().Be(TimeSpan.FromSeconds(5));
        h.Ops.Count("delete").Should().Be(0);
        h.State.Invocations.Should().BeEmpty();
    }

    [Fact(DisplayName = "F5: a throwing RetryDelay callback falls back to five minutes instead of losing the message")]
    public async Task Throwing_RetryDelay_Falls_Back()
    {
        await using var h = new Harness(output, o => o.RetryDelay = _ => throw new InvalidOperationException("bad callback"));
        h.State.Behaviour = (_, _, _) => throw new InvalidOperationException("boom");
        h.Queue.Then(h.Message("1"));

        await h.StartAsync();
        await h.Ops.WaitForAsync("update");

        h.Ops.Calls.Should().ContainSingle(c => c.Op == "update").Which.Timeout.Should().Be(TimeSpan.FromMinutes(5));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Shutdown
    // ---------------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "Shutdown waits for a running handler and completes its message")]
    public async Task Shutdown_Waits_For_Running_Handler()
    {
        await using var h = new Harness(output, o => o.RenewVisibility = false);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.State.Behaviour = async (_, _, _) => await release.Task;
        h.Queue.Then(h.Message("1"));

        await h.StartAsync();
        await h.WaitForHandlerStartAsync();

        var stopping = h.Processor.StopAsync(CancellationToken.None);
        stopping.IsCompleted.Should().BeFalse("the handler is still running");

        release.SetResult();
        await stopping.WaitAsync(SafetyNet);

        h.Ops.Calls.Should().ContainSingle(c => c.Op == "delete");
    }

    [Fact(DisplayName = "Shutdown cancels a handler that outlives the drain timeout, and its message is returned to the queue")]
    public async Task Shutdown_Cancels_Handler_After_Drain_Timeout()
    {
        await using var h = new Harness(output, o => { o.RenewVisibility = false; o.ShutdownDrainTimeout = TimeSpan.FromSeconds(30); });
        h.State.Behaviour = async (_, _, ct) => await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        h.Queue.Then(h.Message("1"));

        await h.StartAsync();
        await h.WaitForHandlerStartAsync();

        var stopping = h.Processor.StopAsync(CancellationToken.None);
        await h.AdvancePastTimerAsync(1, TimeSpan.FromSeconds(30)); // the drain timer is the only one: no renewal, and the worker is inside the handler
        await stopping.WaitAsync(SafetyNet);

        h.Ops.Calls.Should().ContainSingle(c => c.Op == "update").Which.Timeout.Should().Be(TimeSpan.Zero, "another instance should be able to pick it up right away");
        h.Ops.Count("delete").Should().Be(0);
    }

    [Fact(DisplayName = "F1: when the host's shutdown deadline cancels StopAsync, it returns without throwing and still cancels handlers and stops renewal")]
    public async Task Host_Deadline_Still_Cancels_Handlers_And_Renewal()
    {
        await using var h = new Harness(output, o => o.VisibilityTimeout = TimeSpan.FromMinutes(1));
        h.State.Behaviour = async (_, _, ct) => await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        h.Queue.Then(h.Message("1"));

        await h.StartAsync();
        await h.WaitForHandlerStartAsync();
        await h.Time.WaitForTimersAsync(1); // the renewal timer is armed

        using var hostDeadline = new CancellationTokenSource();
        hostDeadline.Cancel();
        await h.Processor.Invoking(p => p.StopAsync(hostDeadline.Token)).Should().NotThrowAsync("the host reports a throwing StopAsync as a failed shutdown");

        await h.Ops.WaitForAsync("update"); // the handler was cancelled and its message abandoned
        h.Ops.Calls.Should().ContainSingle(c => c.Op == "update").Which.Timeout.Should().Be(TimeSpan.Zero);

        h.Time.Advance(TimeSpan.FromMinutes(5));
        h.Ops.Calls.Should().NotContain(c => c.Op == "update" && c.Timeout == TimeSpan.FromMinutes(1), "renewal must stop once StopAsync has exited");
    }

    [Fact(DisplayName = "R3: at the drain deadline, renewal stops even for a handler that ignores cancellation, and shutdown stops waiting after a second drain timeout")]
    public async Task Drain_Deadline_Stops_Renewal_For_Uncooperative_Handler()
    {
        await using var h = new Harness(output, o => { o.VisibilityTimeout = TimeSpan.FromMinutes(10); o.ShutdownDrainTimeout = TimeSpan.FromSeconds(30); });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.State.Behaviour = async (_, _, _) => await release.Task; // ignores its cancellation token
        h.Queue.Then(h.Message("1"));

        await h.StartAsync();
        await h.WaitForHandlerStartAsync();
        await h.Time.WaitForTimersAsync(1); // the renewal timer (5 minutes)

        var stopping = h.Processor.StopAsync(CancellationToken.None);
        await h.AdvancePastTimerAsync(2, TimeSpan.FromSeconds(30)); // the drain deadline passes; the handler is cancelled but ignores it
        await h.Time.WaitForTimersAsync(3);                           // the second, bounded wait is armed

        h.Time.Advance(TimeSpan.FromMinutes(10)); // long past when a renewal would have fired, and past the second wait
        await stopping.WaitAsync(SafetyNet);

        h.Ops.Count("update").Should().Be(0, "renewal must stop at the drain deadline even though the handler is still running");
        release.SetResult();
    }

    [Theory(DisplayName = "R4: durations the timer or the service would refuse fail at startup, not at the first wait")]
    [InlineData("EmptyQueueBackoffMax")]
    [InlineData("ErrorBackoffMax")]
    [InlineData("ShutdownDrainTimeout")]
    [InlineData("VisibilityTimeout")]
    public async Task Oversized_Durations_Fail_At_Startup(string option)
    {
        await using var h = new Harness(output, o =>
        {
            switch (option)
            {
                case "EmptyQueueBackoffMax": o.EmptyQueueBackoffMax = TimeSpan.FromDays(60); break;
                case "ErrorBackoffMax": o.ErrorBackoffMax = TimeSpan.FromDays(60); break;
                case "ShutdownDrainTimeout": o.ShutdownDrainTimeout = TimeSpan.FromDays(60); break;
                case "VisibilityTimeout": o.VisibilityTimeout = TimeSpan.FromDays(8); break;
            }
        });

        await h.Processor.Invoking(p => p.StartAsync(CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>().WithMessage($"*{option}*");
    }

    [Theory(DisplayName = "S3: a processor VisibilityTimeout under one second or with a fraction of a second is rejected at startup; whole seconds are accepted")]
    [InlineData(0.5, false)]
    [InlineData(1.5, false)]
    [InlineData(1, true)]
    public async Task Sub_Second_Visibility_Fails_At_Startup(double seconds, bool valid)
    {
        await using var h = new Harness(output, o => o.VisibilityTimeout = TimeSpan.FromSeconds(seconds));

        var act = h.Processor.Invoking(p => p.StartAsync(CancellationToken.None));
        if (valid) await act.Should().NotThrowAsync();
        else await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*VisibilityTimeout*");
    }

    [Fact(DisplayName = "Invalid options fail at startup, not at the first message")]
    public async Task Invalid_Options_Fail_At_Startup()
    {
        await using var h = new Harness(output, o => o.MaxConcurrentMessages = 0);

        await h.Processor.Invoking(p => p.StartAsync(CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>().WithMessage("*MaxConcurrentMessages*");
    }
}
