using JasonShave.AzureStorage.QueueService.Extensions;
using JasonShave.AzureStorage.QueueService.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace JasonShave.AzureStorage.QueueService.Tests;

public class ExtensionTests
{
    [Fact(DisplayName = "Test configuration by indexing IConfiguration")]
    public void Extensions_Should_Build_Host()
    {
        // arrange
        var host = Host.CreateDefaultBuilder().ConfigureHostConfiguration(builder =>
        {
            builder.SetBasePath(Environment.CurrentDirectory)
                .AddJsonFile("testConfiguration.json", false, true);
        }).ConfigureServices(services =>
        {
            services.AddAzureStorageQueueClient(x => x.AddDefaultClient(y =>
            {
                y.ConnectionString = "localhost";
                y.QueueName = "foo";
            }));
        }).Build();

        host.Dispose();
    }

    [Fact(DisplayName = "Test configuration by using binder")]
    public void ConfigurationBinder_Should_Build_Host()
    {
        // arrange
        var host = Host.CreateDefaultBuilder().ConfigureHostConfiguration(builder =>
        {
            builder.SetBasePath(Environment.CurrentDirectory)
                .AddJsonFile("testConfiguration.json", false, true);
        }).ConfigureServices((hostContext, services) =>
        {
            services.AddAzureStorageQueueClient(x => x.AddDefaultClient(y => hostContext.Configuration.Bind(nameof(QueueClientSettings), y)));
        }).Build();

        host.Dispose();
    }

    [Fact(DisplayName = "Test custom Json configuration should build host")]
    public void Test_Custom_JsonConfiguration_Should_Build_Host()
    {
        // arrange
        var host = Host.CreateDefaultBuilder().ConfigureHostConfiguration(builder =>
        {
            builder.SetBasePath(Environment.CurrentDirectory)
                .AddJsonFile("testConfiguration.json", false, true);
        }).ConfigureServices((hostContext, services) =>
        {
            services.AddAzureStorageQueueClient(x => x.AddDefaultClient(y => hostContext.Configuration.Bind(nameof(QueueClientSettings), y)));
        }).Build();

        host.Dispose();
    }
}