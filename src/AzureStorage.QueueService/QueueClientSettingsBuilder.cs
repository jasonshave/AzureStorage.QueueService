﻿namespace AzureStorage.QueueService;

public class QueueClientSettingsBuilder
{
    internal QueueClientSettingsRegistry? Registry { get; }

    internal QueueClientSettingsBuilder(QueueClientSettingsRegistry registry)
    {
        if (Registry is null)
            Registry = registry;
    }

    public QueueClientSettingsBuilder AddClient(string clientName, Action<QueueClientSettings> settings)
    {
        var queueClientSettings = new QueueClientSettings();
        settings(queueClientSettings);

        Registry.NamedClientsSettings.TryAdd(clientName, queueClientSettings);

        return this;
    }

    public void AddDefaultClient(Action<QueueClientSettings> settings)
    {
        Registry.DefaultClientSettings = new QueueClientSettings();
        settings(Registry.DefaultClientSettings);
    }
}