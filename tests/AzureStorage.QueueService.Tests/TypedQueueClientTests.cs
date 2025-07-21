using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using AzureStorage.QueueService.Telemetry;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AzureStorage.QueueService.Tests;

public class TypedQueueClientTests : BaseTestHost
{
    private readonly IOptions<QueueServiceTelemetrySettings> _telemetrySettings = Options.Create(new QueueServiceTelemetrySettings());

    [Fact]
    public void AddTypedQueueClient_WithDefaultClient_RegistersCorrectly()
    {
        // arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(options => 
            options.AddDefaultClient(y => 
            {
                y.ConnectionString = "UseDevelopmentStorage=true";
                y.QueueName = "test-queue";
                y.CreateIfNotExists = false; // Don't actually try to create in tests
            }));
        services.AddTypedQueueClient<TestObject>();

        // act
        var serviceProvider = services.BuildServiceProvider();
        var typedClient = serviceProvider.GetService<ITypedQueueClient<TestObject>>();

        // assert
        typedClient.Should().NotBeNull();
        typedClient.Should().BeOfType<TypedQueueClient<TestObject>>();
    }

    [Fact]
    public void AddTypedQueueClient_WithNamedClient_RegistersCorrectly()
    {
        // arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(options => 
            options.AddClient("testqueue", y => 
            {
                y.ConnectionString = "UseDevelopmentStorage=true";
                y.QueueName = "test-queue";
                y.CreateIfNotExists = false; // Don't actually try to create in tests
            }));
        services.AddTypedQueueClient<TestObject>("testqueue");

        // act
        var serviceProvider = services.BuildServiceProvider();
        var typedClient = serviceProvider.GetService<ITypedQueueClient<TestObject>>();

        // assert
        typedClient.Should().NotBeNull();
        typedClient.Should().BeOfType<TypedQueueClient<TestObject>>();
    }

    [Fact]
    public async Task TypedQueueClient_SendMessageAsync_CallsUnderlyingClient()
    {
        // arrange
        var mockQueueClient = new Mock<QueueClient>();
        mockQueueClient.Setup(x => x.SendMessageAsync(It.IsAny<BinaryData>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<CancellationToken>()))
                      .ReturnsAsync(Azure.Response.FromValue(
                          Azure.Storage.Queues.Models.QueuesModelFactory.SendReceipt("test-id", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test-receipt", DateTimeOffset.UtcNow), 
                          Mock.Of<Azure.Response>()));

        var messageConverter = new JsonQueueMessageConverter();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var azureQueueClient = new AzureStorageQueueClient(messageConverter, mockQueueClient.Object, loggerFactory, _telemetrySettings);
        var typedClient = new TypedQueueClient<TestObject>(azureQueueClient);

        var testMessage = new TestObject { Property1 = "Test", Property3 = 42 };

        // act
        var result = await typedClient.SendMessageAsync(testMessage);

        // assert
        result.Should().NotBeNull();
        result.MessageId.Should().Be("test-id");
        mockQueueClient.Verify(x => x.SendMessageAsync(It.IsAny<BinaryData>(), null, null, default), Moq.Times.Once);
    }

    [Fact]
    public void TypedQueueClient_DifferentMessageTypes_CanBeRegistered()
    {
        // arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(options => 
            options.AddDefaultClient(y => 
            {
                y.ConnectionString = "UseDevelopmentStorage=true";
                y.QueueName = "test-queue";
                y.CreateIfNotExists = false; // Don't actually try to create in tests
            }));
        
        // Register different typed clients for different message types
        services.AddTypedQueueClient<TestObject>();
        services.AddTypedQueueClient<string>();

        // act
        var serviceProvider = services.BuildServiceProvider();
        var testObjectClient = serviceProvider.GetService<ITypedQueueClient<TestObject>>();
        var stringClient = serviceProvider.GetService<ITypedQueueClient<string>>();

        // assert
        testObjectClient.Should().NotBeNull();
        stringClient.Should().NotBeNull();
        testObjectClient.Should().BeOfType<TypedQueueClient<TestObject>>();
        stringClient.Should().BeOfType<TypedQueueClient<string>>();
    }

    [Fact]
    public void TypedQueueClient_MultipleNamedClients_CanBeRegistered()
    {
        // arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(options => 
        {
            options.AddClient("queue1", y => 
            {
                y.ConnectionString = "UseDevelopmentStorage=true";
                y.QueueName = "test-queue-1";
                y.CreateIfNotExists = false; // Don't actually try to create in tests
            });
            options.AddClient("queue2", y => 
            {
                y.ConnectionString = "UseDevelopmentStorage=true";
                y.QueueName = "test-queue-2";
                y.CreateIfNotExists = false; // Don't actually try to create in tests
            });
        });
        
        // Register same message type but different queues
        services.AddTypedQueueClient<TestObject>("queue1");
        services.AddTypedQueueClient<TestObject>("queue2");

        // act
        var serviceProvider = services.BuildServiceProvider();
        var clients = serviceProvider.GetServices<ITypedQueueClient<TestObject>>().ToList();

        // assert
        clients.Should().HaveCount(2);
        clients.Should().AllBeOfType<TypedQueueClient<TestObject>>();
    }

    [Fact]
    public void TypedQueueClient_WithoutBaseRegistration_ThrowsException()
    {
        // arrange
        var services = new ServiceCollection();
        services.AddLogging();
        // Note: Not registering base queue client services
        services.AddTypedQueueClient<TestObject>();

        // act & assert
        services.Invoking(s => s.BuildServiceProvider().GetRequiredService<ITypedQueueClient<TestObject>>())
               .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void AddQueueClient_WithCustomClientType_RegistersCorrectly()
    {
        // arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(options => 
            options.AddDefaultClient(y => 
            {
                y.ConnectionString = "UseDevelopmentStorage=true";
                y.QueueName = "test-queue";
                y.CreateIfNotExists = false; // Don't actually try to create in tests
            }));
        services.AddTypedQueueClient<TestObject>(); // Register the ITypedQueueClient<TestObject> first
        services.AddQueueClient<TestOrderClient>(); // Register the custom client

        // act
        var serviceProvider = services.BuildServiceProvider();
        var customClient = serviceProvider.GetService<TestOrderClient>();

        // assert
        customClient.Should().NotBeNull();
        customClient.Should().BeOfType<TestOrderClient>();
    }

    [Fact]
    public void CustomQueueClient_CanUseUnderlyingTypedClient()
    {
        // arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(options => 
            options.AddDefaultClient(y => 
            {
                y.ConnectionString = "UseDevelopmentStorage=true";
                y.QueueName = "test-queue";
                y.CreateIfNotExists = false; // Don't actually try to create in tests
            }));
        services.AddTypedQueueClient<TestObject>(); // Register the underlying typed client
        services.AddQueueClient<TestOrderClient>(); // Register the custom client

        // act
        var serviceProvider = services.BuildServiceProvider();
        var customClient = serviceProvider.GetService<TestOrderClient>();
        var typedClient = serviceProvider.GetService<ITypedQueueClient<TestObject>>();

        // assert
        customClient.Should().NotBeNull();
        typedClient.Should().NotBeNull();
        customClient!.GetUnderlyingClient().Should().NotBeNull();
    }

    public class TestOrderClient
    {
        private readonly ITypedQueueClient<TestObject> _queueClient;

        public TestOrderClient(ITypedQueueClient<TestObject> queueClient)
        {
            _queueClient = queueClient;
        }

        public async Task SendOrderAsync(TestObject order)
        {
            await _queueClient.SendMessageAsync(order);
        }

        public ITypedQueueClient<TestObject> GetUnderlyingClient() => _queueClient;
    }
}