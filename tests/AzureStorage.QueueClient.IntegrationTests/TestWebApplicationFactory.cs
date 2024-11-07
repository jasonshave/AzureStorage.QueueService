using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace AzureStorage.QueueClient.IntegrationTests;

public class TestWebApplicationFactory<TProgram>
    : WebApplicationFactory<TProgram> where TProgram : class
{
    public ITestOutputHelper TestOutputHelper { get; set; }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddLogging(x => x.Services.AddSingleton<ILoggerProvider>(_ => new XUnitLoggerProvider(TestOutputHelper)));
        });

        return base.CreateHost(builder);
    }
}