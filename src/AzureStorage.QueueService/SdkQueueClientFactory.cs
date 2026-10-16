using Azure.Storage.Queues;

namespace AzureStorage.QueueService;

internal sealed class SdkQueueClientFactory : ISdkQueueClientFactory
{
    public QueueClient CreateClient(string clientDescription, QueueClientSettings settings)
    {
        // Only what this client needs to exist. Source-queue policy (poison destination, receive timing) is the
        // factory's job; applying it here would demand a poison queue for the poison queue.
        var error = settings.ValidateConnection();
        if (error is not null) throw new InvalidOperationException($"{clientDescription} {error}");

        QueueClient Create() => settings.UsesConnectionString
            ? new QueueClient(settings.ConnectionString, settings.QueueName, settings.ClientOptions)
            : new QueueClient(settings.EndpointUri, settings.TokenCredential, settings.ClientOptions);

        if (settings.ClientOptions is not { MessageEncoding: QueueMessageEncoding.Base64 } options) return Create();

        // The SDK captures event handlers during construction. Restore caller-owned options immediately afterward,
        // including when construction fails; serialize library construction with the same options to avoid duplicates.
        lock (options)
        {
            options.MessageDecodingFailed += DecodingFailures.OnDecodingFailedAsync;
            try
            {
                return Create();
            }
            finally
            {
                options.MessageDecodingFailed -= DecodingFailures.OnDecodingFailedAsync;
            }
        }
    }
}
