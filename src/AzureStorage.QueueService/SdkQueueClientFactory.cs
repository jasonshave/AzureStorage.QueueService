using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzureStorage.QueueService;

internal sealed class SdkQueueClientFactory : ISdkQueueClientFactory
{
    private readonly ILogger _logger;
    private int _fallbackLogged;

    public SdkQueueClientFactory(ILogger<SdkQueueClientFactory>? logger = null)
    {
        _logger = logger ?? NullLogger<SdkQueueClientFactory>.Instance;
    }

    public QueueClient CreateClient(string clientDescription, QueueClientSettings settings)
    {
        // Only what this client needs to exist. Source-queue policy (poison destination, receive timing) is the
        // factory's job; applying it here would demand a poison queue for the poison queue.
        var error = settings.ValidateConnection();
        if (error is not null) throw new InvalidOperationException($"{clientDescription} {error}");

        QueueClient Create(QueueClientOptions? options) => settings.UsesConnectionString
            ? new QueueClient(settings.ConnectionString, settings.QueueName, options)
            : new QueueClient(settings.EndpointUri, settings.TokenCredential, options);

        if (settings.ClientOptions is not { MessageEncoding: QueueMessageEncoding.Base64 } callerOptions) return Create(settings.ClientOptions);

        // The SDK captures MessageDecodingFailed handlers during construction, so the library's handler has to be on the
        // options the client is built from. Put it on a private copy: on the caller's object, a QueueClient the app builds
        // from the same options at that moment would capture it too, and it throws outside the library's receives.
        QueueClientOptions copy;
        try
        {
            copy = QueueClientOptionsCopy.Copy(callerOptions);
        }
        catch (Exception e)
        {
            // Never fail a client over this. Fall back to the 3.0 behavior, which only risks that narrow race.
            if (Interlocked.Exchange(ref _fallbackLogged, 1) == 0) _logger.LogClientOptionsCopyFailed(clientDescription, e);
            return CreateWithTemporaryHandler(callerOptions, () => Create(callerOptions));
        }

        copy.MessageDecodingFailed += DecodingFailures.OnDecodingFailedAsync;
        return Create(copy);
    }

    private static QueueClient CreateWithTemporaryHandler(QueueClientOptions options, Func<QueueClient> create)
    {
        // Restore caller-owned options immediately afterward, including when construction fails; serialize library
        // construction with the same options to avoid duplicates.
        lock (options)
        {
            options.MessageDecodingFailed += DecodingFailures.OnDecodingFailedAsync;
            try
            {
                return create();
            }
            finally
            {
                options.MessageDecodingFailed -= DecodingFailures.OnDecodingFailedAsync;
            }
        }
    }
}
