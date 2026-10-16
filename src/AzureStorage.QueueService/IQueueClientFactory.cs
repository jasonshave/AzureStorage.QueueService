namespace AzureStorage.QueueService;

/// <summary>
/// Hands out the queue registered under a name (or the default one). Each is created once and shared, the way
/// <c>IHttpClientFactory</c> shares handlers. Clients can also be injected directly: <see cref="IStorageQueue"/>,
/// <see cref="IQueueSender"/> or <see cref="IQueueReceiver"/> for the default client, and the same with
/// <c>[FromKeyedServices("name")]</c> for a named one.
/// </summary>
public interface IQueueClientFactory
{
    /// <exception cref="InvalidOperationException">No client with that name was registered, or its settings are incomplete.</exception>
    IStorageQueue GetQueue(string clientName);

    /// <exception cref="InvalidOperationException">No default client was registered, or its settings are incomplete.</exception>
    IStorageQueue GetQueue();
}
