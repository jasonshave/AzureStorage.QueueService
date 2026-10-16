using System.ComponentModel.DataAnnotations;
using Azure.Messaging;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using System.Diagnostics.Metrics;
using Xunit.Abstractions;

namespace AzureStorage.QueueService.Tests;

/// <summary>
/// The 3.1 processor features end to end, on in-memory queues: a real host, the real processor and the real queue code.
/// Messages are sent before the host starts, so the processor's first receives find them and nothing waits on a clock;
/// the clock is a fake that never moves. Stopping the host drains the deliveries, so their settlement is finished by then.
/// </summary>
public class ProcessorFeatureTests(ITestOutputHelper output)
{
    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(10);

    public sealed record OrderCreated(string Id);

    public sealed record OrderCancelled(string Id);

    /// <summary>What the handlers saw, in order, with a signal per entry so a test awaits outcomes instead of sleeping.</summary>
    public sealed class Recorder
    {
        private readonly Signal _added = new();
        public List<string> Entries { get; } = new();

        public void Add(string entry)
        {
            lock (Entries) Entries.Add(entry);
            _added.Set();
        }

        public Task WaitForAsync(int count) => _added.WaitUntilAsync(() => { lock (Entries) return Entries.Count >= count; });
    }

    private sealed class CreatedHandler(Recorder recorder) : IQueueMessageHandler<OrderCreated>
    {
        public ValueTask HandleAsync(QueueMessage<OrderCreated> message, CancellationToken cancellationToken)
        {
            recorder.Add($"created {message.Body!.Id} ({message.Headers[QueueMessageHeaders.MessageType]})");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancelledHandler(Recorder recorder) : IQueueMessageHandler<OrderCancelled>
    {
        public ValueTask HandleAsync(QueueMessage<OrderCancelled> message, CancellationToken cancellationToken)
        {
            recorder.Add($"cancelled {message.Body!.Id}");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EventHandler(Recorder recorder) : ICloudEventHandler<OrderCreated>
    {
        public ValueTask HandleAsync(QueueMessage<CloudEvent> message, OrderCreated? data, CancellationToken cancellationToken)
        {
            recorder.Add($"{message.Body!.Type} {data!.Id}");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingHandler(Recorder recorder) : IQueueMessageHandler<TestObject>
    {
        public ValueTask HandleAsync(QueueMessage<TestObject> message, CancellationToken cancellationToken)
        {
            recorder.Add($"{message.Body!.Property1} attempt {message.DequeueCount}");
            throw message.Body.Property1 == "invalid" ? new ValidationException("no") : new TimeoutException("slow downstream");
        }
    }

    private sealed class RecordingHandler(Recorder recorder) : IQueueMessageHandler<TestObject>
    {
        public ValueTask HandleAsync(QueueMessage<TestObject> message, CancellationToken cancellationToken)
        {
            recorder.Add($"handler {message.Body!.Property1}");
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A scoped middleware that stops the pipeline for messages marked "skip".</summary>
    private sealed class SkippingMiddleware(Recorder recorder) : IQueueMessageMiddleware
    {
        public async ValueTask InvokeAsync(QueueMessageContext context, QueueMessageDelegate next)
        {
            var body = ((TestObject)context.Message.Body!).Property1;
            recorder.Add($"client {body}");
            if (body != "skip") await next(context);
        }
    }

    private IHost Build(Action<QueueServiceBuilder> configure, Action<IServiceCollection>? services = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.AddProvider(new XUnitLoggerProvider(output));
        builder.Services.AddMetrics();
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        builder.Services.AddSingleton<Recorder>();
        builder.Services.AddAzureStorageQueueClient(configure);
        builder.Services.AddInMemoryQueues();
        services?.Invoke(builder.Services);
        return builder.Build();
    }

    private static async Task<IReadOnlyList<DeadLetteredMessage>> DeadLettered(IHost host, string client) =>
        await host.Services.GetRequiredKeyedService<IDeadLetterQueue>(client).PeekAsync();

    [Fact(DisplayName = "A routed processor sends each message type to its own handler and dead-letters a type nothing is routed for")]
    public async Task Routed_Processor()
    {
        using var host = Build(queues => queues
            .AddClient("orders", s => { s.QueueName = "orders"; s.SendMessageType = true; })
            .AddRoutedProcessor(
                routes => routes.Route<OrderCreated, CreatedHandler>().Route<OrderCancelled, CancelledHandler>("cancelled"),
                o => o.MaxConcurrentMessages = 3));
        var sender = host.Services.GetRequiredKeyedService<IQueueSender>("orders");
        await sender.SendAsync(new OrderCreated("1"));
        await sender.SendAsync(new OrderCancelled("2"), new SendOptions { MessageType = "cancelled" });
        await sender.SendAsync(new OrderCancelled("3"), new SendOptions { MessageType = "refunded" });

        await host.StartAsync();
        await host.Services.GetRequiredService<Recorder>().WaitForAsync(2);
        await host.StopAsync();

        host.Services.GetRequiredService<Recorder>().Entries.Should().BeEquivalentTo("created 1 (OrderCreated)", "cancelled 2");
        var unroutable = (await DeadLettered(host, "orders")).Should().ContainSingle().Subject;
        unroutable.Category.Should().Be(DeadLetterCategories.Unroutable);
        unroutable.Reason.Should().Contain("'refunded'");
    }

    [Fact(DisplayName = "A CloudEvent router sends each event to the handler for its type, with its data typed")]
    public async Task CloudEvent_Router()
    {
        using var host = Build(queues => queues
            .AddClient("events", s => s.QueueName = "events")
            .AddCloudEventRouter(routes => routes.Route<OrderCreated, EventHandler>("Contoso.Orders.Created")));
        var sender = host.Services.GetRequiredKeyedService<IQueueSender>("events");
        await sender.SendAsync(new CloudEvent("/orders", "Contoso.Orders.Created", new OrderCreated("7")));
        await sender.SendAsync(new CloudEvent("/orders", "Contoso.Orders.Shipped", new OrderCreated("8")));

        await host.StartAsync();
        await host.Services.GetRequiredService<Recorder>().WaitForAsync(1);
        await host.StopAsync();

        host.Services.GetRequiredService<Recorder>().Entries.Should().Equal("Contoso.Orders.Created 7");
        (await DeadLettered(host, "events")).Should().ContainSingle().Which.Category.Should().Be(DeadLetterCategories.Unroutable);
    }

    [Fact(DisplayName = "A lambda handler gets the body, the message, the token and services from the message's scope")]
    public async Task Lambda_Handler()
    {
        using var host = Build(queues => queues
            .AddClient("orders", s => s.QueueName = "orders")
            .AddProcessor<TestObject>((TestObject body, IQueueMessage message, Recorder recorder, CancellationToken token) =>
                recorder.Add($"{body.Property1} from {message.QueueName}, cancellable {token.CanBeCanceled}")));
        await host.Services.GetRequiredKeyedService<IQueueSender>("orders").SendAsync(new TestObject { Property1 = "hi" });

        await host.StartAsync();
        await host.Services.GetRequiredService<Recorder>().WaitForAsync(1);
        await host.StopAsync();

        host.Services.GetRequiredService<Recorder>().Entries.Should().Equal("hi from orders, cancellable True");
        (await host.Services.GetRequiredKeyedService<IStorageQueue>("orders").GetPropertiesAsync()).ApproximateMessagesCount.Should().Be(0);
    }

    [Fact(DisplayName = "Middleware runs in registration order, global first here; one that does not call next completes the message without the handler")]
    public async Task Middleware_Order_And_Short_Circuit()
    {
        using var host = Build(queues =>
        {
            queues.Use(async (context, next) =>
            {
                context.Services.GetRequiredService<Recorder>().Add($"global {((TestObject)context.Message.Body!).Property1}");
                await next(context);
            });
            queues.AddClient("orders", s => s.QueueName = "orders")
                  .UseMiddleware<SkippingMiddleware>()
                  .AddProcessor<TestObject, RecordingHandler>();
        });
        var sender = host.Services.GetRequiredKeyedService<IQueueSender>("orders");
        await sender.SendAsync(new TestObject { Property1 = "run" });
        await sender.SendAsync(new TestObject { Property1 = "skip" });

        await host.StartAsync();
        await host.Services.GetRequiredService<Recorder>().WaitForAsync(5);
        await host.StopAsync();

        host.Services.GetRequiredService<Recorder>().Entries.Should().Equal("global run", "client run", "handler run", "global skip", "client skip");
        (await host.Services.GetRequiredKeyedService<IStorageQueue>("orders").GetPropertiesAsync()).ApproximateMessagesCount.Should().Be(0, "the skipped message counts as handled");
    }

    [Fact(DisplayName = "Error policy: DeadLetterOn dead-letters without a retry; OnHandlerError can complete; the handler sees IsLastAttempt")]
    public async Task Error_Policy()
    {
        using var host = Build(queues => queues
            .AddClient("orders", s => s.QueueName = "orders")
            .AddProcessor<TestObject, ThrowingHandler>(o =>
            {
                o.MaxConcurrentMessages = 2;
                o.DeadLetterOn<ValidationException>();
                o.OnHandlerError = context => context.Exception is TimeoutException ? HandlerErrorAction.Complete : HandlerErrorAction.Retry;
            }));
        var sender = host.Services.GetRequiredKeyedService<IQueueSender>("orders");
        await sender.SendAsync(new TestObject { Property1 = "invalid" });
        await sender.SendAsync(new TestObject { Property1 = "timeout" });

        await host.StartAsync();
        await host.Services.GetRequiredService<Recorder>().WaitForAsync(2);
        await host.StopAsync();

        host.Services.GetRequiredService<Recorder>().Entries.Should().BeEquivalentTo("invalid attempt 1", "timeout attempt 1");
        var deadLettered = (await DeadLettered(host, "orders")).Should().ContainSingle().Subject;
        deadLettered.Category.Should().Be(DeadLetterCategories.Handler);
        deadLettered.Reason.Should().Contain("ValidationException");
        (await host.Services.GetRequiredKeyedService<IStorageQueue>("orders").GetPropertiesAsync()).ApproximateMessagesCount.Should().Be(0, "the timeout was completed by the policy");
    }

    [Fact(DisplayName = "A client paused before the host starts receives nothing until it is resumed")]
    public async Task Pause_And_Resume()
    {
        using var host = Build(queues => queues.AddClient("orders", s => s.QueueName = "orders").AddProcessor<TestObject, RecordingHandler>());
        var controller = host.Services.GetRequiredService<IQueueProcessorController>();
        var orders = host.Services.GetRequiredKeyedService<IStorageQueue>("orders");
        await orders.SendAsync(new TestObject { Property1 = "held" });
        controller.Pause("orders");

        await host.StartAsync();

        controller.GetStatus().Should().ContainSingle().Which.State.Should().Be(QueueProcessorState.Paused);
        (await orders.PeekAsync<TestObject>()).Should().ContainSingle("a paused processor has not taken the message");
        controller.Invoking(c => c.Pause("typo")).Should().Throw<InvalidOperationException>().WithMessage("*'orders'*");

        controller.Resume("orders");
        await host.Services.GetRequiredService<Recorder>().WaitForAsync(1);
        await host.StopAsync();
        host.Services.GetRequiredService<Recorder>().Entries.Should().Equal("handler held");
    }

    [Fact(DisplayName = "Metrics: time in queue, receive requests by result, dead-letters by reason, and the messaging-convention duration")]
    public async Task Metrics()
    {
        using var host = Build(queues => queues
            .AddClient("orders", s => s.QueueName = "orders")
            .AddProcessor<TestObject, ThrowingHandler>(o => o.DeadLetterOn<ValidationException>()));
        var meters = host.Services.GetRequiredService<IMeterFactory>();
        using var timeInQueue = new MetricCollector<double>(meters, "AzureStorage.QueueService", "queue_message_time_in_queue_seconds");
        using var receives = new MetricCollector<long>(meters, "AzureStorage.QueueService", "queue_receive_requests_total");
        using var deadLettered = new MetricCollector<long>(meters, "AzureStorage.QueueService", "queue_messages_dead_lettered_total");
        using var processDuration = new MetricCollector<double>(meters, "AzureStorage.QueueService.Messaging", "messaging.process.duration");
        await host.Services.GetRequiredKeyedService<IQueueSender>("orders").SendAsync(new TestObject { Property1 = "invalid" });

        await host.StartAsync();
        await host.Services.GetRequiredService<Recorder>().WaitForAsync(1);
        await host.StopAsync();

        timeInQueue.GetMeasurementSnapshot().Should().ContainSingle().Which.Value.Should().Be(0, "the fake clock never moved");
        receives.GetMeasurementSnapshot().Should().Contain(m => (string?)m.Tags["queue.receive_result"] == "message");
        deadLettered.GetMeasurementSnapshot().Should().ContainSingle().Which.Tags["queue.dead_letter_reason"].Should().Be(DeadLetterCategories.Handler);
        processDuration.GetMeasurementSnapshot().Should().ContainSingle().Which.Tags["error.type"].Should().Be(typeof(ValidationException).FullName);
    }

    [Fact(DisplayName = "Registering the same handler on the same client twice runs one processor with the merged options")]
    public void Duplicate_Registration_Runs_One_Processor()
    {
        using var host = Build(queues =>
        {
            queues.AddClient("orders", s => s.QueueName = "orders").AddProcessor<TestObject, RecordingHandler>(o => o.MaxConcurrentMessages = 2);
            queues.AddClient("orders", _ => { }).AddProcessor<TestObject, RecordingHandler>(o => o.RenewVisibility = false);
        });

        host.Services.GetServices<IHostedService>().Should().ContainSingle();
    }
}
