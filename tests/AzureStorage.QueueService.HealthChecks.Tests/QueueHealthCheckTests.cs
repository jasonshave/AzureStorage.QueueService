using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AzureStorage.QueueService.HealthChecks.Tests;

/// <summary>The queue check against in-memory queues: the real factory, queue and poison queue, no storage account.</summary>
public class QueueHealthCheckTests
{
    private sealed record Order(string Id);

    [Fact(DisplayName = "Queue: healthy while reachable, degraded once a message is dead-lettered, unhealthy for a client that is not registered")]
    public async Task Reports_Reachability_And_Poison_Messages()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddAzureStorageQueueClient(queues => queues.AddClient("orders", settings => settings.QueueName = "orders"));
        services.AddInMemoryQueues();
        services.AddHealthChecks()
            .AddAzureStorageQueue("orders", configure: o => o.DegradedWhenPoisonMessages = true)
            .AddAzureStorageQueue("missing");
        await using var provider = services.BuildServiceProvider();
        var health = provider.GetRequiredService<HealthCheckService>();

        var before = await health.CheckHealthAsync();
        before.Entries["azure-storage-queue-orders"].Status.Should().Be(HealthStatus.Healthy);
        var missing = before.Entries["azure-storage-queue-missing"];
        missing.Status.Should().Be(HealthStatus.Unhealthy);
        missing.Exception.Should().BeOfType<InvalidOperationException>();

        var queue = provider.GetRequiredKeyedService<IStorageQueue>("orders");
        await queue.SendAsync(new Order("1"));
        var received = (await queue.ReceiveAsync<Order>()).Single();
        await received.DeadLetterAsync("bad order");

        var after = (await health.CheckHealthAsync()).Entries["azure-storage-queue-orders"];
        after.Status.Should().Be(HealthStatus.Degraded);
        after.Data["poisonQueue"].Should().Be("orders-poison");
        after.Data["poisonMessageCount"].Should().Be(1);
        after.Description.Should().Contain("'orders-poison' holds about 1 message(s)");
    }
}
