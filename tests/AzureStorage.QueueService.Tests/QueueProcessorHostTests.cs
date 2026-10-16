using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace AzureStorage.QueueService.Tests;

/// <summary>
/// The processor's lifecycle through a real Generic Host, not direct calls: when workers start, and what happens
/// when startup fails after the processor's own start-up steps have run.
/// </summary>
public class QueueProcessorHostTests
{
    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(10);

    private sealed class CountingQueue : IStorageQueue
    {
        private int _receives;
        public int Receives => Volatile.Read(ref _receives);
        public TaskCompletionSource FirstReceive { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string QueueName => "orders";

        public ValueTask<IReadOnlyList<QueueMessage<T>>> ReceiveAsync<T>(int maxMessages = 1, TimeSpan? visibilityTimeout = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _receives);
            FirstReceive.TrySetResult();
            return new(Array.Empty<QueueMessage<T>>());
        }

        public ValueTask<IReadOnlyList<PeekedQueueMessage<T>>> PeekAsync<T>(int maxMessages = 1, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<SendResult> SendAsync<T>(T message, SendOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask CreateIfNotExistsAsync(IDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask ClearAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<QueueStatus> GetPropertiesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoopHandler : IQueueMessageHandler<TestObject>
    {
        public ValueTask HandleAsync(QueueMessage<TestObject> message, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    /// <summary>A service registered after the processor whose own StartedAsync fails — startup has already run the processor's steps by then.</summary>
    private sealed class FailsAfterStarted : IHostedService, IHostedLifecycleService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StartedAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("a later service failed to start");
        public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static IHost BuildHost(CountingQueue queue, Action<IServiceCollection>? more = null) =>
        Host.CreateDefaultBuilder().ConfigureServices(services =>
        {
            var factory = new Mock<IQueueClientFactory>();
            factory.Setup(x => x.GetQueue("orders")).Returns(queue);
            services.AddSingleton(factory.Object);                      // the library's TryAdd keeps this one
            services.AddSingleton<TimeProvider>(new FakeTimeProvider()); // so an empty queue never waits on real time
            services.AddAzureStorageQueueClient(x =>
                x.AddClient("orders", y => { y.ConnectionString = "UseDevelopmentStorage=true"; y.QueueName = "orders"; })
                 .AddProcessor<TestObject, NoopHandler>());
            more?.Invoke(services);
        }).Build();

    [Fact(DisplayName = "S4: when a later service fails startup, the processor never polls, and disposing the host leaves nothing running")]
    public async Task Failed_Startup_Never_Polls()
    {
        var queue = new CountingQueue();
        var host = BuildHost(queue, services => services.AddHostedService<FailsAfterStarted>());

        await host.Invoking(h => h.StartAsync()).Should().ThrowAsync<InvalidOperationException>();
        host.Dispose();
        for (var i = 0; i < 10; i++) await Task.Yield();

        queue.Receives.Should().Be(0, "workers start only when the host reports ApplicationStarted, which a failed startup never does");
    }

    [Fact(DisplayName = "After a successful startup the processor polls, and stops with the host")]
    public async Task Successful_Startup_Polls_And_Stops()
    {
        var queue = new CountingQueue();
        using var host = BuildHost(queue);

        await host.StartAsync();
        await queue.FirstReceive.Task.WaitAsync(SafetyNet);
        await host.StopAsync();

        queue.Receives.Should().BeGreaterThanOrEqualTo(1);
    }
}
