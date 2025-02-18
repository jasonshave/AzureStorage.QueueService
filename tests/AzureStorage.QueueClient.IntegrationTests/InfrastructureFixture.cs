using Testcontainers.Azurite;

namespace AzureStorage.QueueClient.IntegrationTests;

public class InfrastructureFixture : IAsyncLifetime
{
    public string ConnectionString { get; private set; } = string.Empty;

    private readonly AzuriteContainer _azuriteContainer = new AzuriteBuilder()
        .WithImage("mcr.microsoft.com/azure-storage/azurite")
        .Build();

    public async Task DisposeAsync()
    {
        await _azuriteContainer.StopAsync();
    }

    public async Task InitializeAsync()
    {
        await _azuriteContainer.StartAsync();
        ConnectionString = _azuriteContainer.GetConnectionString();
    }
}
