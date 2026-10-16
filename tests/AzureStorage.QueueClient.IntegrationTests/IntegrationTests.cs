using System.Net.Http.Json;
using System.Text;
using Azure.Storage.Queues;
using AzureStorage.QueueService;
using AzureStorage.QueueService.Tests.Server;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace AzureStorage.QueueClient.IntegrationTests;

public class IntegrationTests : IClassFixture<TestWebApplicationFactory<Program>>
{
    private readonly TestWebApplicationFactory<Program> _factory;
    private readonly HttpClient _httpClient;

    public IntegrationTests(ITestOutputHelper testOutputHelper, TestWebApplicationFactory<Program> factory)
    {
        factory.TestOutputHelper = testOutputHelper;
        _factory = factory;
        _httpClient = factory.CreateClient();
    }

    [Fact]
    public async Task SendMessage_SendsAnd_ReturnsSameMessage()
    {
        // arrange
        var person = new Person()
        {
            FirstName = "Jason",
        };

        // act
        var response = await _httpClient.PostAsJsonAsync("/send", person);
        var personResponse = await response.Content.ReadFromJsonAsync<Person>();

        // assert
        response.IsSuccessStatusCode.Should().BeTrue();
        personResponse.Should().NotBeNull();
        personResponse!.FirstName.Should().Be(person.FirstName);
    }

    [Fact(DisplayName = "The documented infinite TTL sends a message that never expires")]
    public async Task Send_Infinite_TimeToLive_Does_Not_Expire()
    {
        var connectionString = _factory.Services.GetRequiredService<IConfiguration>()["Storage:ConnectionString"]!;
        var queueName = "never-expire-" + Guid.NewGuid().ToString("N");
        var raw = new Azure.Storage.Queues.QueueClient(connectionString, queueName);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(queues => queues.AddDefaultClient(settings =>
        {
            settings.ConnectionString = connectionString;
            settings.QueueName = queueName;
            settings.CreateIfNotExists = true;
        }));
        using var provider = services.BuildServiceProvider();
        var queue = provider.GetRequiredService<IQueueClientFactory>().GetQueue();

        try
        {
            var receipt = await queue.SendAsync(new Person { FirstName = "persistent" },
                new SendOptions(TimeToLive: Timeout.InfiniteTimeSpan));

            receipt.ExpirationTime.Year.Should().Be(9999);
            (await raw.PeekMessagesAsync(1)).Value.Should().ContainSingle().Which.ExpiresOn.Should().Be(receipt.ExpirationTime);
        }
        finally
        {
            await raw.DeleteIfExistsAsync();
        }
    }

    [Fact(DisplayName = "S1: a 56-character queue dead-letters to its 63-character default poison queue")]
    public async Task LongQueueName_DeadLetters_To_Its_Default_Poison_Queue()
    {
        var connectionString = _factory.Services.GetRequiredService<IConfiguration>()["Storage:ConnectionString"]!;
        var queue = _factory.Services.GetRequiredService<IQueueClientFactory>().GetQueue("long");
        queue.QueueName.Should().HaveLength(56);
        await queue.CreateIfNotExistsAsync();
        await queue.ClearAsync();
        var poison = new Azure.Storage.Queues.QueueClient(connectionString, queue.QueueName + "-poison");
        poison.Name.Should().HaveLength(63);
        await poison.CreateIfNotExistsAsync();
        await poison.ClearMessagesAsync();

        await queue.SendAsync(new Person { FirstName = "poisoned" });
        var message = (await queue.ReceiveAsync<Person>()).Should().ContainSingle().Subject;

        await message.DeadLetterAsync("test");

        message.Settlement.Should().Be(MessageSettlement.DeadLettered);
        (await poison.PeekMessagesAsync(10)).Value.Should().ContainSingle();
        (await new Azure.Storage.Queues.QueueClient(connectionString, queue.QueueName).PeekMessagesAsync(10)).Value.Should().BeEmpty();
    }

    [Theory(DisplayName = "Reused Base64 options preserve default and custom decoding behavior in direct SDK receives and peeks")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Shared_Base64_Options_Preserve_Decoding_Behavior(bool peek, bool customHandler)
    {
        var connectionString = _factory.Services.GetRequiredService<IConfiguration>()["Storage:ConnectionString"]!;
        var queueName = "shared-options-" + Guid.NewGuid().ToString("N");
        var rawBefore = new Azure.Storage.Queues.QueueClient(connectionString, queueName + "-before");
        var rawAfter = new Azure.Storage.Queues.QueueClient(connectionString, queueName + "-after");
        var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
        var handled = 0;
        if (customHandler)
            options.MessageDecodingFailed += _ =>
            {
                handled++;
                return Task.CompletedTask;
            };
        var before = new Azure.Storage.Queues.QueueClient(connectionString, rawBefore.Name, options);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(queues => queues.AddDefaultClient(settings =>
        {
            settings.ConnectionString = connectionString;
            settings.QueueName = rawAfter.Name;
            settings.ClientOptions = options;
        }));
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IQueueClientFactory>().GetQueue();
        var after = new Azure.Storage.Queues.QueueClient(connectionString, rawAfter.Name, options);

        try
        {
            await rawBefore.CreateIfNotExistsAsync();
            await rawAfter.CreateIfNotExistsAsync();
            await rawBefore.SendMessageAsync("not base64!");
            await rawAfter.SendMessageAsync("not base64!");

            async Task<int> ReadAsync(Azure.Storage.Queues.QueueClient client)
            {
                if (peek) return (await client.PeekMessagesAsync(1)).Value.Length;
                return (await client.ReceiveMessagesAsync(1, TimeSpan.FromSeconds(1))).Value.Length;
            }

            if (customHandler)
            {
                (await ReadAsync(before)).Should().Be(0);
                (await ReadAsync(after)).Should().Be(0);
                handled.Should().Be(2, "the caller's existing handler controls both direct SDK clients");
            }
            else
            {
                await ((Func<Task>)(() => ReadAsync(before))).Should().ThrowAsync<FormatException>();
                await ((Func<Task>)(() => ReadAsync(after))).Should().ThrowAsync<FormatException>(
                    "library client construction must not silently suppress a direct SDK client's decoding errors");
            }
        }
        finally
        {
            await rawBefore.DeleteIfExistsAsync();
            await rawAfter.DeleteIfExistsAsync();
        }
    }

    [Fact(DisplayName = "F2: on a Base64 queue, a message that is not Base64 comes back with Error set, the healthy one still arrives, and the bad one can be dead-lettered")]
    public async Task Base64Queue_UndecodableMessage_DoesNotBreakTheBatch()
    {
        // arrange
        var connectionString = _factory.Services.GetRequiredService<IConfiguration>()["Storage:ConnectionString"]!;
        var queue = _factory.Services.GetRequiredService<IQueueClientFactory>().GetQueue("base64");
        await queue.CreateIfNotExistsAsync();
        await queue.ClearAsync();
        var poison = new Azure.Storage.Queues.QueueClient(connectionString, queue.QueueName + "-poison"); // no encoding: reads stored text as-is
        await poison.CreateIfNotExistsAsync();
        await poison.ClearMessagesAsync();

        // A plain client (no encoding) puts literal text on the queue that the Base64 client cannot decode.
        var raw = new Azure.Storage.Queues.QueueClient(connectionString, queue.QueueName);
        await raw.SendMessageAsync("this is not base64!!");
        await queue.SendAsync(new Person { FirstName = "healthy" });

        // act
        var messages = await queue.ReceiveAsync<Person>(maxMessages: 10);

        // assert
        messages.Should().HaveCount(2);
        var healthy = messages.Single(m => m.Error is null);
        var bad = messages.Single(m => m.Error is not null);
        healthy.Body!.FirstName.Should().Be("healthy");
        bad.RawBody.ToString().Should().Be("this is not base64!!");

        await bad.DeadLetterAsync("not base64");
        await healthy.CompleteAsync();

        var poisoned = (await poison.PeekMessagesAsync(10)).Value.Should().ContainSingle().Subject;
        Encoding.UTF8.GetString(Convert.FromBase64String(poisoned.MessageText)).Should().Be("this is not base64!!", "the poison client encodes like the source queue, so the raw text round-trips");
        (await raw.PeekMessagesAsync(10)).Value.Should().BeEmpty("both messages were settled");
    }
}
