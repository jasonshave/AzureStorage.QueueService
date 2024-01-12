using JasonShave.AzureStorage.QueueService.Models;

namespace JasonShave.AzureStorage.QueueService.Services;

public class QueueClientSettingsBuilder
{
    internal QueueClientSettingsRegistry SettingsRegistry { get; } = new();

    public QueueClientSettingsBuilder AddClient(string clientName, Action<QueueClientSettings> settings)
    {
        var queueClientSettings = new QueueClientSettings();
        settings(queueClientSettings);

        SettingsRegistry.ClientSettings.Add(clientName, queueClientSettings);

        return this;
    }

    public void AddDefaultClient(Action<QueueClientSettings> settings)
    {
        SettingsRegistry.DefaultClientSettings = new QueueClientSettings();
        settings(SettingsRegistry.DefaultClientSettings);
    }
}