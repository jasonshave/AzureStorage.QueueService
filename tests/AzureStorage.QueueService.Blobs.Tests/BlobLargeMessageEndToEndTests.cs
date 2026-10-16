using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace AzureStorage.QueueService.Blobs.Tests;

/// <summary>One Azurite container for the whole class, deleted afterwards.</summary>
public sealed class AzuriteContainer : IAsyncLifetime
{
    public const string ConnectionString = "UseDevelopmentStorage=true";

    public BlobContainerClient Container { get; } = new(ConnectionString, $"azqs-blobs-{Guid.NewGuid():N}");

    public Task InitializeAsync() => Task.CompletedTask; // the store creates it, which is part of what is tested

    public Task DisposeAsync() => Container.DeleteIfExistsAsync();

    public async Task<List<string>> BlobNamesAsync(string prefix)
    {
        var names = new List<string>();
        if (!await Container.ExistsAsync()) return names;
        await foreach (var blob in Container.GetBlobsAsync(BlobTraits.None, BlobStates.None, prefix, CancellationToken.None)) names.Add(blob.Name);
        return names;
    }
}

/// <summary>
/// The real path a large message takes: in-memory queues, the real processor and a real Blob container in Azurite
/// (127.0.0.1:10000). Each test uses its own queue name, so its blobs have their own prefix in the shared container. The
/// clock is fake and never moves: the message is already on the queue when the processor's first poll runs.
/// </summary>
public class BlobLargeMessageEndToEndTests(AzuriteContainer azurite) : IClassFixture<AzuriteContainer>
{
    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(10);

    public sealed record Report(string Id, string Text);

    private static Report Large(string id) => new(id, string.Concat(Enumerable.Range(0, 10_000).Select(i => $"{i:D9}|")));

    private static IHost BuildHost(FakeTimeProvider time, Action<QueueClientSettingsBuilder> configure)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddAzureStorageQueueClient(configure);
        builder.Services.AddInMemoryQueues();
        return builder.Build();
    }

    [Fact(DisplayName = "Blob storage: a 100 KB message reaches the handler intact, and its blob is deleted once the message is completed")]
    public async Task Large_Message_Round_Trips_And_Blob_Is_Deleted()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
        var handled = new TaskCompletionSource<Report>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = BuildHost(time, queues =>
            queues.AddClient("orders", settings => settings.QueueName = "orders")
                  .UseBlobStorageForLargeMessages(azurite.Container)
                  .AddProcessor<Report>((Report report) => { handled.TrySetResult(report); }));

        var sent = Large("r-1");
        await host.Services.GetRequiredKeyedService<IQueueSender>("orders").SendAsync(sent);
        var stored = await azurite.BlobNamesAsync("orders/");
        stored.Should().ContainSingle().Which.Should().StartWith("orders/2026/10/16/", "the body went to Blob storage, named by queue and date");

        await host.StartAsync();
        var received = await handled.Task.WaitAsync(SafetyNet);
        await host.StopAsync(); // drains: completion, and the blob delete after it, finish before this returns

        received.Should().Be(sent);
        (await azurite.BlobNamesAsync("orders/")).Should().BeEmpty("the blob is deleted once the message is completed");
    }

    [Fact(DisplayName = "Blob storage: a dead-lettered large message keeps its blob, and the poison queue still reads the body")]
    public async Task Dead_Lettered_Message_Keeps_Its_Blob()
    {
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Both clients find Blob storage from their own connection string, as an application configured this way would.
        using var host = BuildHost(new FakeTimeProvider(), queues =>
        {
            queues.AddClient("refunds", settings => { settings.ConnectionString = AzuriteContainer.ConnectionString; settings.QueueName = "refunds"; })
                  .UseBlobStorageForLargeMessages(azurite.Container.Name)
                  .AddProcessor<Report>(async (QueueMessage<Report> message) =>
                  {
                      await message.DeadLetterAsync("cannot refund");
                      handled.TrySetResult();
                  });
            queues.AddClient("refunds-poison", settings => { settings.ConnectionString = AzuriteContainer.ConnectionString; settings.QueueName = "refunds-poison"; })
                  .UseBlobStorageForLargeMessages(azurite.Container.Name);
        });

        var sent = Large("r-2");
        await host.Services.GetRequiredKeyedService<IQueueSender>("refunds").SendAsync(sent);
        await host.StartAsync();
        await handled.Task.WaitAsync(SafetyNet);
        await host.StopAsync();

        (await azurite.BlobNamesAsync("refunds/")).Should().ContainSingle("a dead-lettered message may be replayed, so its body is kept");
        var deadLettered = (await host.Services.GetRequiredKeyedService<IQueueReceiver>("refunds-poison").ReceiveAsync<Report>()).Should().ContainSingle().Subject;
        deadLettered.Error.Should().BeNull();
        deadLettered.Body.Should().Be(sent);
    }
}
