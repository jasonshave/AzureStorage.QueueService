using Azure.Identity;
using Azure.Storage.Queues;
using AzureStorage.QueueService.Telemetry;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AzureStorage.QueueService.Tests;

public class QueueClientFactoryTests : BaseTestHost
{
    private readonly IOptions<QueueServiceTelemetrySettings> _telemetrySettings = Options.Create(new QueueServiceTelemetrySettings());

    [Fact]
    public void QueueClientFactory_GetNamedClient_RegistersViaServiceCollection()
    {
        // arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(options => options.AddClient("foo", y => 
        {
            y.ConnectionString = "UseDevelopmentStorage=true";
            y.QueueName = "test-queue";
            y.CreateIfNotExists = false; // Don't try to create in tests
        }));

        var serviceProvider = services.BuildServiceProvider();
        var factory = serviceProvider.GetRequiredService<IQueueClientFactory>();

        // act & assert - This would fail without Azure Storage emulator, but we're testing the registration
        factory.Invoking(f => f.GetQueueClient("foo")).Should().NotThrow();
    }

    [Fact]
    public void QueueClientFactory_AddStorageQueueCalledTwice_DoesNotBreakRegistration()
    {
        // arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(options => options.AddClient("foo", y => 
        {
            y.ConnectionString = "UseDevelopmentStorage=true";
            y.QueueName = "test-queue";
            y.CreateIfNotExists = false; // Don't try to create in tests
        }));
        services.AddAzureStorageQueueClient(options => options.AddClient("foo", y => 
        {
            y.ConnectionString = "UseDevelopmentStorage=true";
            y.QueueName = "test-queue";
            y.CreateIfNotExists = false; // Don't try to create in tests
        }));

        var serviceProvider = services.BuildServiceProvider();
        var factory = serviceProvider.GetRequiredService<IQueueClientFactory>();

        // act & assert
        factory.Invoking(f => f.GetQueueClient("foo")).Should().NotThrow();
    }

    [Fact]
    public void GetNamedClientTwice_ReturnsSameInstance()
    {
        // arrange        
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(options => options.AddClient("foo", y => 
        {
            y.ConnectionString = "UseDevelopmentStorage=true";
            y.QueueName = "test-queue";
            y.CreateIfNotExists = false; // Don't try to create in tests
        }));

        var serviceProvider = services.BuildServiceProvider();
        var factory = serviceProvider.GetRequiredService<IQueueClientFactory>();

        // act
        var queueClient1 = factory.GetQueueClient("foo");
        var queueClient2 = factory.GetQueueClient("foo");

        // assert
        queueClient1.Should().NotBeNull();
        queueClient2.Should().NotBeNull();
        queueClient1.Should().BeSameAs(queueClient2);
    }

    [Fact]
    public void RegisterMultipleNamedClient_CanGetEach()
    {
        // arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(options =>
        {
            options.AddClient("foo", y => 
            {
                y.ConnectionString = "UseDevelopmentStorage=true";
                y.QueueName = "test-queue-1";
                y.CreateIfNotExists = false; // Don't try to create in tests
            });
            options.AddClient("bar", y => 
            {
                y.ConnectionString = "UseDevelopmentStorage=true";
                y.QueueName = "test-queue-2";
                y.CreateIfNotExists = false; // Don't try to create in tests
            });
        });

        var serviceProvider = services.BuildServiceProvider();
        var factory = serviceProvider.GetRequiredService<IQueueClientFactory>();

        // act & assert
        factory.Invoking(f => f.GetQueueClient("foo")).Should().NotThrow();
        factory.Invoking(f => f.GetQueueClient("bar")).Should().NotThrow();
    }

    [Fact]
    public void GetDefaultClient_RegistersViaServiceCollection()
    {
        // arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(options => options.AddDefaultClient(y => 
        {
            y.ConnectionString = "UseDevelopmentStorage=true";
            y.QueueName = "test-queue";
            y.CreateIfNotExists = false; // Don't try to create in tests
        }));

        var serviceProvider = services.BuildServiceProvider();
        var factory = serviceProvider.GetRequiredService<IQueueClientFactory>();

        // act & assert
        factory.Invoking(f => f.GetQueueClient()).Should().NotThrow();
    }

    [Fact]
    public void GetUnregisteredClient_Throws()
    {
        // arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(options => options.AddClient("foo", y => 
        {
            y.ConnectionString = "UseDevelopmentStorage=true";
            y.QueueName = "test-queue";
            y.CreateIfNotExists = false; // Don't try to create in tests
        }));

        var serviceProvider = services.BuildServiceProvider();
        var factory = serviceProvider.GetRequiredService<IQueueClientFactory>();

        // act & assert
        factory.Invoking(x => x.GetQueueClient("bar")).Should().Throw<ApplicationException>(because: "The client name wasn't registered");
    }

    [Fact]
    public void CreateClient_WithCorrectSettings_DoesNotThrow()
    {
        // arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAzureStorageQueueClient(options => options.AddDefaultClient(y =>
        {
            y.TokenCredential = new DefaultAzureCredential();
            y.EndpointUri = new Uri("https://fake.queue.core.windows.net/myqueue");
            y.QueueName = "foo";
            y.CreateIfNotExists = false; // Don't try to create in tests
        }));

        var serviceProvider = services.BuildServiceProvider();
        var factory = serviceProvider.GetRequiredService<IQueueClientFactory>();

        // act & assert
        factory.Invoking(f => f.GetQueueClient()).Should().NotThrow();
    }
}