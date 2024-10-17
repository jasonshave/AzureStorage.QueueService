using Microsoft.Extensions.Configuration;

namespace AzureStorage.QueueService.Tests
{
    public abstract class BaseTestHost
    {
        protected IConfiguration Configuration { get; }

        protected BaseTestHost()
        {
            Configuration = new ConfigurationBuilder()
                .SetBasePath(Environment.CurrentDirectory)
                .AddJsonFile("testConfiguration.json", false, true)
                .Build();
        }
    }
}
