using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Xunit.Abstractions;

namespace AzureStorage.QueueService.Tests;

/// <summary>
/// Batch handlers end to end on in-memory queues, the way <see cref="ProcessorFeatureTests"/> runs the single-message
/// processor: messages are sent before the host starts, the clock is a fake that never moves, and stopping the host
/// drains the batches, so their settlement is finished by then.
/// </summary>
public class BatchProcessorTests(ITestOutputHelper output)
{
    /// <summary>The batches the handler saw, by body, and what it does with each.</summary>
    public sealed class BatchState
    {
        private readonly Signal _handled = new();
        public List<string[]> Batches { get; } = new();
        public Func<IReadOnlyList<QueueMessage<TestObject>>, ValueTask> Behaviour { get; set; } = _ => ValueTask.CompletedTask;

        public async ValueTask HandleAsync(IReadOnlyList<QueueMessage<TestObject>> messages)
        {
            try
            {
                await Behaviour(messages);
            }
            finally
            {
                lock (Batches) Batches.Add(messages.Select(m => m.Body!.Property1!).ToArray());
                _handled.Set();
            }
        }

        public Task WaitForBatchesAsync(int count) => _handled.WaitUntilAsync(() => { lock (Batches) return Batches.Count >= count; });
    }

    private sealed class Handler(BatchState state) : IQueueBatchHandler<TestObject>
    {
        public ValueTask HandleAsync(IReadOnlyList<QueueMessage<TestObject>> messages, CancellationToken cancellationToken) => state.HandleAsync(messages);
    }

    private IHost Build(Action<QueueBatchProcessorOptions>? configure = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Logging.AddProvider(new XUnitLoggerProvider(output));
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider());
        builder.Services.AddSingleton<BatchState>();
        builder.Services.AddAzureStorageQueueClient(queues => queues
            .AddClient("orders", s => s.QueueName = "orders")
            .AddBatchProcessor<TestObject, Handler>(configure));
        builder.Services.AddInMemoryQueues();
        return builder.Build();
    }

    private static async Task SendAsync(IHost host, params string[] bodies)
    {
        var sender = host.Services.GetRequiredKeyedService<IQueueSender>("orders");
        foreach (var body in bodies) await sender.SendAsync(new TestObject { Property1 = body });
    }

    private static async Task<int> RemainingAsync(IHost host) =>
        (await host.Services.GetRequiredKeyedService<IStorageQueue>("orders").GetPropertiesAsync()).ApproximateMessagesCount;

    private static async Task<IReadOnlyList<DeadLetteredMessage>> DeadLetteredAsync(IHost host) =>
        await host.Services.GetRequiredKeyedService<IDeadLetterQueue>("orders").PeekAsync(maxMessages: 32);

    [Fact(DisplayName = "A batch handler gets up to MaxBatchSize messages per call, and returning completes them all")]
    public async Task Batches_Up_To_MaxBatchSize()
    {
        using var host = Build(o => o.MaxBatchSize = 3);
        await SendAsync(host, "1", "2", "3", "4", "5");
        var state = host.Services.GetRequiredService<BatchState>();

        await host.StartAsync();
        await state.WaitForBatchesAsync(2);
        await host.StopAsync();

        state.Batches.Select(b => b.Length).Should().Equal(3, 2);
        state.Batches.SelectMany(b => b).Should().BeEquivalentTo("1", "2", "3", "4", "5");
        (await RemainingAsync(host)).Should().Be(0);
        host.Services.GetRequiredService<IQueueProcessorController>().GetStatus().Should().ContainSingle()
            .Which.MaxConcurrentMessages.Should().Be(3, "one batch of up to three at a time");
    }

    [Fact(DisplayName = "A batch handler can dead-letter one message and return; the rest are completed")]
    public async Task Partial_Settlement_Then_Completion()
    {
        using var host = Build();
        await SendAsync(host, "ok 1", "bad", "ok 2");
        var state = host.Services.GetRequiredService<BatchState>();
        state.Behaviour = async messages =>
        {
            foreach (var message in messages.Where(m => m.Body!.Property1 == "bad")) await message.DeadLetterAsync("not a real order");
        };

        await host.StartAsync();
        await state.WaitForBatchesAsync(1);
        await host.StopAsync();

        state.Batches.Should().ContainSingle().Which.Should().HaveCount(3);
        var deadLettered = (await DeadLetteredAsync(host)).Should().ContainSingle().Subject;
        deadLettered.Category.Should().Be(DeadLetterCategories.Handler);
        deadLettered.Reason.Should().Be("not a real order");
        (await RemainingAsync(host)).Should().Be(0, "the two the handler left alone were completed");
    }

    [Fact(DisplayName = "A throwing batch handler: settled messages stand, the error policy decides per message, the rest are abandoned for retry")]
    public async Task Failure_Applies_The_Policy_Per_Message()
    {
        using var host = Build(o => o.OnHandlerError = context =>
            ((TestObject)context.Message.Body!).Property1 == "poison" ? HandlerErrorAction.DeadLetter : HandlerErrorAction.Retry);
        await SendAsync(host, "done", "poison", "retry");
        var state = host.Services.GetRequiredService<BatchState>();
        state.Behaviour = async messages =>
        {
            await messages.Single(m => m.Body!.Property1 == "done").CompleteAsync();
            throw new InvalidOperationException("downstream failed");
        };

        await host.StartAsync();
        await state.WaitForBatchesAsync(1);
        await host.StopAsync();

        state.Batches.Should().ContainSingle();
        (await DeadLetteredAsync(host)).Should().ContainSingle().Which.Reason.Should().Contain("InvalidOperationException");
        (await RemainingAsync(host)).Should().Be(1, "only the retried message is still on the queue");
        (await host.Services.GetRequiredKeyedService<IStorageQueue>("orders").PeekAsync<TestObject>())
            .Should().BeEmpty("it was abandoned with a retry delay and the clock has not moved");
    }

    [Fact(DisplayName = "An unreadable message is dead-lettered on its own and left out of the batch")]
    public async Task Unreadable_Messages_Are_Left_Out()
    {
        using var host = Build();
        await SendAsync(host, "a");
        await host.Services.GetRequiredKeyedService<IQueueSender>("orders").SendAsync("not an order");
        await SendAsync(host, "b");
        var state = host.Services.GetRequiredService<BatchState>();

        await host.StartAsync();
        await state.WaitForBatchesAsync(1);
        await host.StopAsync();

        state.Batches.Should().ContainSingle().Which.Should().Equal("a", "b");
        (await DeadLetteredAsync(host)).Should().ContainSingle().Which.Category.Should().Be(DeadLetterCategories.Deserialization);
        (await RemainingAsync(host)).Should().Be(0);
    }
}
