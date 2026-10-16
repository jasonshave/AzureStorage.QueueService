using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Azure.Messaging;
using Azure.Storage.Queues.Models;
using AzureStorage.QueueService.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;
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

    /// <summary>Each receive hands out the next scripted batch (one message, or empty once the script runs out).</summary>
    private sealed class FakeQueue : IStorageQueue
    {
        // Batches of any message type; a receive casts to the type its processor asks for.
        private readonly Queue<Func<object>> _batches = new();
        public List<(int Take, TimeSpan? Visibility)> Receives { get; } = new();
        public Signal Received { get; } = new();
        public string QueueName => "orders";

        public FakeQueue Then<T>(QueueMessage<T> message) { lock (_batches) _batches.Enqueue(() => new[] { message }); return this; }
        public FakeQueue ThenThrow(Exception exception) { lock (_batches) _batches.Enqueue(() => throw exception); return this; }

        public Task WaitForReceivesAsync(int count) => Received.WaitUntilAsync(() => { lock (Receives) return Receives.Count >= count; });

        public ValueTask<IReadOnlyList<QueueMessage<T>>> ReceiveAsync<T>(int maxMessages = 1, TimeSpan? visibilityTimeout = null, CancellationToken cancellationToken = default)
        {
            lock (Receives) Receives.Add((maxMessages, visibilityTimeout));
            Func<object>? next;
            lock (_batches) next = _batches.Count > 0 ? _batches.Dequeue() : null;
            try
            {
                var batch = next is null ? Array.Empty<QueueMessage<T>>() : (QueueMessage<T>[])next();
                return new(batch);
            }
            finally
            {
                Received.Set();
            }
        }

        public ValueTask<IReadOnlyList<PeekedQueueMessage<T>>> PeekAsync<T>(int maxMessages = 1, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SendResult> SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask CreateIfNotExistsAsync(IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask ClearAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<QueueStatus> GetPropertiesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
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

        public CapturingLoggerProvider Logs { get; } = new();

        public Harness(ITestOutputHelper output, Action<QueueProcessorOptions>? configure = null, IStorageQueue? queue = null)
        {
            var services = new ServiceCollection();
            services.AddLogging(logging => logging.AddProvider(new XUnitLoggerProvider(output)).AddProvider(Logs));
            services.AddSingleton(State);
            services.AddScoped<ScopeMarker>();
            services.AddScoped<RecordingHandler>();
            services.Configure<QueueProcessorOptions>("opts", o =>
            {
                // Off, so the timers a test counts are the processor's own waits and renewals.
                o.LongRunningHandlerWarning = null;
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
                QueueServiceMetrics.Fallback,
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

    [Fact(DisplayName = "The processor asks for as many messages as it has free handlers and handles each in its own scope")]
    public async Task Each_Worker_Handles_One_Message_In_Its_Own_Scope()
    {
        await using var h = new Harness(output, o => o.MaxConcurrentMessages = 2);
        h.Queue.Then(h.Message("1")).Then(h.Message("2"));

        await h.StartAsync();
        await h.Ops.WaitForAsync("delete", count: 2);

        h.State.Invocations.Select(i => i.MessageId).Should().BeEquivalentTo("1", "2");
        h.State.Invocations.Select(i => i.Scope).Distinct().Should().HaveCount(2, "every message gets a fresh scope");
        h.Queue.Receives[0].Should().Be((2, TimeSpan.FromMinutes(1)), "both handlers are free at the start");
        h.Queue.Receives.Should().OnlyContain(r => r.Take >= 1 && r.Take <= 2 && r.Visibility == TimeSpan.FromMinutes(1));
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
        var queue = new StorageQueue(client.Object, new BlockingSerializer(block), loggerFactory, new QueueServiceTelemetrySettings(), QueueServiceMetrics.Fallback, null, false, null, propagateTraceContext: false);
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

    [Fact(DisplayName = "With trace propagation on, the processing span continues the sender's trace and carries the semantic-convention attributes")]
    public async Task Processing_Span_Continues_The_Senders_Trace()
    {
        using var spans = new SpanRecorder();
        var sent = new List<BinaryData>();
        var client = new Mock<Azure.Storage.Queues.QueueClient>();
        client.Setup(x => x.Name).Returns("orders");
        client.Setup(x => x.SendMessageAsync(It.IsAny<BinaryData>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .Callback((BinaryData body, TimeSpan? _, TimeSpan? _, CancellationToken _) => sent.Add(body))
            .ReturnsAsync(Azure.Response.FromValue(QueuesModelFactory.SendReceipt("1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "r0", DateTimeOffset.UtcNow), Mock.Of<Azure.Response>()));
        var delivered = 0;
        client.Setup(x => x.ReceiveMessagesAsync(It.IsAny<int?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var batch = Interlocked.Exchange(ref delivered, 1) == 0
                    ? new[] { QueuesModelFactory.QueueMessage("1", "r0", sent.Single(), 1) }
                    : Array.Empty<Azure.Storage.Queues.Models.QueueMessage>();
                return Azure.Response.FromValue(batch, Mock.Of<Azure.Response>());
            });
        var loggerFactory = new ServiceCollection().AddLogging().BuildServiceProvider().GetRequiredService<ILoggerFactory>();
        var telemetry = new QueueServiceTelemetrySettings { CreateNewActivityOnMessageSend = true };
        var queue = new StorageQueue(client.Object, new JsonQueueMessageSerializer(), loggerFactory, telemetry, QueueServiceMetrics.Fallback, null, false, null, propagateTraceContext: true);

        ActivityTraceId traceId;
        using (var request = new Activity("incoming request").Start())
        {
            await queue.SendAsync(new TestObject { Property1 = "traced" });
            traceId = request.TraceId;
        }
        var send = await spans.WaitForAsync(QueueServiceDiagnostics.Names.SendingMessage, traceId);

        await using var h = new Harness(output, queue: queue);
        await h.StartAsync();
        var process = await spans.WaitForAsync(QueueServiceDiagnostics.Names.ProcessingMessage, traceId);

        process.ParentSpanId.Should().Be(send.SpanId, "the processor's loop has no ambient span, so the sender's context is the parent");
        process.Links.Should().ContainSingle().Which.Context.SpanId.Should().Be(send.SpanId);
        process.Kind.Should().Be(ActivityKind.Consumer);
        process.GetTagItem("queue.message_id").Should().Be("1");
        process.GetTagItem("messaging.message.id").Should().Be("1");
        process.GetTagItem("messaging.system").Should().Be("azure.storage.queue");
        process.GetTagItem("messaging.destination.name").Should().Be("orders");
        process.GetTagItem("messaging.operation.type").Should().Be("process");
        process.GetTagItem("queue.outcome").Should().Be("completed");
        h.State.Invocations.Should().ContainSingle().Which.MessageId.Should().Be("1");
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

    // ---------------------------------------------------------------------------------------------------------
    // CloudEvents
    // ---------------------------------------------------------------------------------------------------------

    private sealed record OrderCreated(string OrderId, decimal Total);

    private sealed class CloudEventRecorder(List<(QueueMessage<CloudEvent> Message, OrderCreated? Data)> calls) : ICloudEventHandler<OrderCreated>
    {
        public ValueTask HandleAsync(QueueMessage<CloudEvent> message, OrderCreated? data, CancellationToken cancellationToken)
        {
            lock (calls) calls.Add((message, data));
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// A CloudEvent processor registered the way an application does, through <c>AddCloudEventProcessor</c>, so the
    /// client's serializer and the processor options reach the adapter by the real wiring. Only the queue is faked.
    /// </summary>
    private sealed class CloudEventHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        public FakeQueue Queue { get; } = new();
        public Operations Ops { get; } = new();
        public List<(QueueMessage<CloudEvent> Message, OrderCreated? Data)> Calls { get; } = new();
        public IHostedLifecycleService Processor { get; }

        public CloudEventHarness(ITestOutputHelper output, Action<QueueProcessorOptions>? configure = null)
        {
            var factory = new Mock<IQueueClientFactory>();
            factory.Setup(x => x.GetQueue("events")).Returns(Queue);

            var services = new ServiceCollection();
            services.AddLogging(logging => logging.AddProvider(new XUnitLoggerProvider(output)));
            services.AddMetrics(); // so a test can collect this container's measurements alone
            services.AddSingleton(factory.Object); // the library's TryAdd keeps this one
            services.AddSingleton<TimeProvider>(new ObservableTimeProvider()); // so an empty queue never waits on real time
            services.AddSingleton(Calls);
            services.AddAzureStorageQueueClient(x =>
                x.AddClient("events", s => { s.ConnectionString = "UseDevelopmentStorage=true"; s.QueueName = "events"; })
                 // Event Grid writes camelCase data, which System.Text.Json's own defaults would not read, so this also proves
                 // the data is read with the client's serializer.
                 .AddCloudEventProcessor<OrderCreated, CloudEventRecorder>(configure));
            _provider = services.BuildServiceProvider();
            Processor = (IHostedLifecycleService)_provider.GetServices<IHostedService>().Single();
        }

        /// <summary>A received message as a queue would hand it to the processor: the envelope already deserialized.</summary>
        public QueueMessage<CloudEvent> Message(string id, string json)
        {
            var raw = BinaryData.FromString(json);
            return new(new QueueMessageInfo(id, "r0", 1, null, null, null), raw, JsonSerializer.Deserialize<CloudEvent>(raw), null, Ops);
        }

        public IMeterFactory Meters => _provider.GetRequiredService<IMeterFactory>();

        public async Task StartAsync()
        {
            await Processor.StartAsync(CancellationToken.None);
            await Processor.StartedAsync(CancellationToken.None);
        }

        /// <summary>Drains: once this returns, every delivery has finished and recorded its outcome.</summary>
        public Task StopAsync() => Processor.StopAsync(CancellationToken.None).WaitAsync(SafetyNet);

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            await _provider.DisposeAsync();
        }
    }

    private const string OrderData = """{"orderId":"o-1","total":12.5}""";

    private const string OrderEvent = """
        {"specversion":"1.0","type":"Contoso.Orders.Created","source":"/contoso/orders","id":"evt-1","datacontenttype":"application/json","data":{"orderId":"o-1","total":12.5}}
        """;

    [Fact(DisplayName = "A CloudEvent is delivered with its data typed by the client's serializer and its metadata, then completed")]
    public async Task CloudEvent_Is_Delivered_With_Typed_Data_And_Completed()
    {
        await using var h = new CloudEventHarness(output);
        h.Queue.Then(h.Message("1", OrderEvent));

        await h.StartAsync();
        await h.Ops.WaitForAsync("delete");

        var (message, data) = h.Calls.Should().ContainSingle().Subject;
        data.Should().Be(new OrderCreated("o-1", 12.5m));
        message.Body!.Type.Should().Be("Contoso.Orders.Created");
        message.Body.Source.Should().Be("/contoso/orders");
        message.Body.Id.Should().Be("evt-1");
        message.Settlement.Should().Be(MessageSettlement.Completed);
    }

    [Fact(DisplayName = "A CloudEvent whose data does not match the handler's type is dead-lettered by the processor without calling the handler, and counted as a failure")]
    public async Task CloudEvent_With_Unreadable_Data_Is_Dead_Lettered()
    {
        await using var h = new CloudEventHarness(output);
        using var failed = new MetricCollector<long>(h.Meters, QueueServiceDiagnostics.ServiceName, "queue_messages_failed_total");
        using var durations = new MetricCollector<double>(h.Meters, QueueServiceDiagnostics.ServiceName, "queue_message_processing_duration_seconds");
        h.Queue.Then(h.Message("1", OrderEvent.Replace(OrderData, "\"not an order\"")));

        await h.StartAsync();
        await h.Ops.WaitForAsync("deadletter");
        await h.StopAsync();

        h.Ops.Calls.Should().ContainSingle(c => c.Op == "deadletter").Which.Reason.Should().Contain("evt-1").And.Contain(nameof(OrderCreated));
        h.Calls.Should().BeEmpty();
        h.Ops.Count("delete").Should().Be(0);
        // The processor's own unreadable-body path, not a handler's settlement.
        failed.GetMeasurementSnapshot().Should().ContainSingle().Which.Value.Should().Be(1);
        durations.GetMeasurementSnapshot().Should().ContainSingle().Which.Tags[QueueServiceDiagnostics.Names.Outcome].Should().Be("dead_lettered");
    }

    [Fact(DisplayName = "With DeadLetterOnDeserializationFailure off, unreadable CloudEvent data reaches the handler as null with Error set")]
    public async Task CloudEvent_With_Unreadable_Data_Reaches_Handler_When_Dead_Lettering_Is_Off()
    {
        await using var h = new CloudEventHarness(output, o => o.DeadLetterOnDeserializationFailure = false);
        h.Queue.Then(h.Message("1", OrderEvent.Replace(OrderData, "[1, 2]")));

        await h.StartAsync();
        await h.Ops.WaitForAsync("delete");

        var (message, data) = h.Calls.Should().ContainSingle().Subject;
        data.Should().BeNull();
        message.Body!.Id.Should().Be("evt-1");
        message.Error.Should().NotBeNull();
        h.Ops.Count("deadletter").Should().Be(0);
    }

    // ---------------------------------------------------------------------------------------------------------
    // 3.1: log levels, the last attempt, rate limiting, hung handlers
    // ---------------------------------------------------------------------------------------------------------

    [Fact(DisplayName = "A failing handler is a warning while retries remain and an error on the last attempt, which the handler can see")]
    public async Task Handler_Failure_Log_Level_Follows_The_Last_Attempt()
    {
        await using var h = new Harness(output, o => o.MaxDequeueCount = 5);
        var lastAttempts = new List<bool>();
        h.State.Behaviour = (message, _, _) =>
        {
            lock (lastAttempts) lastAttempts.Add(message.IsLastAttempt);
            throw new InvalidOperationException("boom");
        };
        h.Queue.Then(h.Message("early", dequeueCount: 1)).Then(h.Message("last", dequeueCount: 5));

        await h.StartAsync();
        await h.Ops.WaitForAsync("update", count: 2);

        lastAttempts.Should().Equal(false, true);
        h.Logs.Single(7).Level.Should().Be(LogLevel.Warning);
        h.Logs.Single(27).Level.Should().Be(LogLevel.Error);
    }

    [Fact(DisplayName = "With a rate limiter, a receive asks only for as many messages as there are permits")]
    public async Task Rate_Limiter_Caps_The_Receive()
    {
        using var limiter = new System.Threading.RateLimiting.TokenBucketRateLimiter(new System.Threading.RateLimiting.TokenBucketRateLimiterOptions
        {
            TokenLimit = 2,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromHours(1),
            AutoReplenishment = false,
        });
        await using var h = new Harness(output, o => { o.MaxConcurrentMessages = 5; o.RateLimiter = limiter; });
        h.Queue.Then(h.Message("1"));

        await h.StartAsync();
        await h.Queue.WaitForReceivesAsync(1);

        h.Queue.Receives[0].Take.Should().Be(2, "five handlers are free but only two permits are");
    }

    [Fact(DisplayName = "With a rate limiter that has a permit for every free handler, messages are still received and handled")]
    public async Task Rate_Limiter_With_Permits_To_Spare()
    {
        using var limiter = new System.Threading.RateLimiting.TokenBucketRateLimiter(new System.Threading.RateLimiting.TokenBucketRateLimiterOptions
        {
            TokenLimit = 10,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromHours(1),
            AutoReplenishment = false,
        });
        await using var h = new Harness(output, o => o.RateLimiter = limiter);
        h.Queue.Then(h.Message("1"));

        await h.StartAsync();
        await h.Ops.WaitForAsync("delete");

        h.Logs.Entries.Should().NotContain(e => e.Level >= LogLevel.Error, "no slot is left to give back, and that is not a failed receive");
    }

    [Fact(DisplayName = "MaxRenewalDuration stops renewing a handler's lease and says so, so a hung handler cannot hide its message for ever")]
    public async Task Renewal_Stops_At_MaxRenewalDuration()
    {
        await using var h = new Harness(output, o => { o.VisibilityTimeout = TimeSpan.FromSeconds(10); o.MaxRenewalDuration = TimeSpan.FromSeconds(8); });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.State.Behaviour = async (_, _, _) => await release.Task;
        h.Queue.Then(h.Message("1"));

        await h.StartAsync();
        await h.WaitForHandlerStartAsync();
        await h.AdvancePastTimerAsync(1, TimeSpan.FromSeconds(5)); // 5 s in: renewed
        await h.Ops.WaitForAsync("update");
        await h.AdvancePastTimerAsync(2, TimeSpan.FromSeconds(5)); // 10 s in: past the limit
        await h.Logs.WaitForAsync(32);

        release.SetResult();
        await h.Ops.WaitForAsync("delete");
        h.Ops.Count("update").Should().Be(1);
    }
}
