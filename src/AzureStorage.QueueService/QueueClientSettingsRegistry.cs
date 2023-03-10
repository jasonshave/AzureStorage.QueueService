using JasonShave.AzureStorage.QueueService.Models;

namespace JasonShave.AzureStorage.QueueService;

internal sealed class QueueClientSettingsRegistry
{
    public Dictionary<string, QueueClientSettings> ClientSettings { get; } = new();

    public QueueClientSettings DefaultClientSettings { get; set; } = new();
}