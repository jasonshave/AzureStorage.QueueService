using Azure.Storage.Blobs;
using Azure.Storage.Queues;

namespace AzureStorage.QueueService.Blobs;

/// <summary>Works out a queue client's Blob storage from its own connection settings.</summary>
internal static class BlobEndpoints
{
    // Azurite's default ports: Blob on 10000, Queue on 10001.
    private const int EmulatorQueuePort = 10001;
    private const int EmulatorBlobPort = 10000;

    /// <summary>
    /// A Blob service client for the storage account the queue client connects to, with the same credentials: the
    /// connection string, or the endpoint's account with the client's <see cref="QueueClientSettings.TokenCredential"/>.
    /// </summary>
    /// <param name="settings">The queue client's finished settings.</param>
    /// <param name="clientDescription">Names the client in errors, for example "Queue client 'orders'".</param>
    /// <exception cref="InvalidOperationException">The settings do not say enough to find the Blob endpoint.</exception>
    public static BlobServiceClient ServiceClientFor(QueueClientSettings settings, string clientDescription)
    {
        const string Instead = "Pass a BlobContainerClient to UseBlobStorageForLargeMessages instead.";
        var options = BlobOptions(settings.ClientOptions);

        if (!string.IsNullOrEmpty(settings.ConnectionString))
        {
            try
            {
                return new BlobServiceClient(settings.ConnectionString, options);
            }
            catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException)
            {
                throw new InvalidOperationException($"{clientDescription} keeps large messages in Blob storage, but its connection string cannot be used for Blob storage: {e.Message} {Instead}", e);
            }
        }

        if (settings.EndpointUri is { } endpoint)
        {
            var blobEndpoint = ServiceUriFor(endpoint)
                ?? throw new InvalidOperationException($"{clientDescription} keeps large messages in Blob storage, but the Blob endpoint cannot be worked out from its queue endpoint '{endpoint}'. That works for '<account>.queue.<suffix>' hosts and the storage emulator. {Instead}");

            if (settings.TokenCredential is not { } credential)
                throw new InvalidOperationException($"{clientDescription} keeps large messages in Blob storage, but has an EndpointUri and no TokenCredential to connect with. Set one, or call UseTokenCredential.");

            return new BlobServiceClient(blobEndpoint, credential, options);
        }

        throw new InvalidOperationException($"{clientDescription} keeps large messages in Blob storage, but has no ConnectionString or EndpointUri to find the storage account from. {Instead}");
    }

    /// <summary>
    /// The account's Blob endpoint, from a queue or queue-service URI: <c>https://acct.queue.core.windows.net/orders</c>
    /// gives <c>https://acct.blob.core.windows.net/</c>, and the emulator's
    /// <c>http://127.0.0.1:10001/devstoreaccount1/orders</c> gives <c>http://127.0.0.1:10000/devstoreaccount1/</c>. Null
    /// when the URI is neither shape, such as a custom domain.
    /// </summary>
    public static Uri? ServiceUriFor(Uri queueEndpoint)
    {
        // Host style: the service is a label after the account, in every Azure cloud ("acct.queue.core.windows.net",
        // "acct.queue.core.chinacloudapi.cn", "acct-secondary.queue...").
        var labels = queueEndpoint.Host.Split('.');
        if (labels.Length > 2)
        {
            var service = Array.FindIndex(labels, 1, label => label.Equals("queue", StringComparison.OrdinalIgnoreCase));
            if (service > 0)
            {
                labels[service] = "blob";
                return new UriBuilder(queueEndpoint.Scheme, string.Join('.', labels), queueEndpoint.Port, "/").Uri;
            }
        }

        // Path style (the emulator): the account is the first path segment and the service is told apart by port.
        if (queueEndpoint.Port == EmulatorQueuePort && new QueueUriBuilder(queueEndpoint).AccountName is { Length: > 0 } account)
            return new UriBuilder(queueEndpoint.Scheme, queueEndpoint.Host, EmulatorBlobPort, $"/{account}/").Uri;

        return null;
    }

    /// <summary>The queue client's retry policy and transport, applied to the Blob client.</summary>
    private static BlobClientOptions BlobOptions(QueueClientOptions? queueOptions)
    {
        var options = new BlobClientOptions();
        if (queueOptions is null) return options;

        options.Retry.Mode = queueOptions.Retry.Mode;
        options.Retry.MaxRetries = queueOptions.Retry.MaxRetries;
        options.Retry.Delay = queueOptions.Retry.Delay;
        options.Retry.MaxDelay = queueOptions.Retry.MaxDelay;
        options.Retry.NetworkTimeout = queueOptions.Retry.NetworkTimeout;
        options.Transport = queueOptions.Transport;
        return options;
    }
}

/// <summary>
/// Azure's rules for a container name: 3 to 63 characters, lowercase letters, digits and single hyphens, starting and
/// ending with a letter or digit.
/// </summary>
internal static class ContainerNameRules
{
    /// <summary>Returns a description of the problem, or null when the name is valid.</summary>
    public static string? Check(string name)
    {
        if (name.Length is < 3 or > 63) return $"'{name}' is {name.Length} characters; a container name must be 3 to 63.";
        if (!char.IsAsciiLetterLower(name[0]) && !char.IsAsciiDigit(name[0])) return $"'{name}' must start with a lowercase letter or digit.";
        if (!char.IsAsciiLetterLower(name[^1]) && !char.IsAsciiDigit(name[^1])) return $"'{name}' must end with a lowercase letter or digit.";
        if (name.Contains("--", StringComparison.Ordinal)) return $"'{name}' must not contain consecutive hyphens.";
        foreach (var c in name)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-') return $"'{name}' may only contain lowercase letters, digits and hyphens.";
        }

        return null;
    }
}
