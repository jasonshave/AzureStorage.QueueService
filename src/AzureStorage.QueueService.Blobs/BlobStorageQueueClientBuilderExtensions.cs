using Azure.Storage.Blobs;
using AzureStorage.QueueService.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AzureStorage.QueueService;

/// <summary>
/// Lets one queue client send messages over the queue's 64 KiB limit by keeping their bodies in Azure Blob storage.
/// </summary>
/// <remarks>
/// <para>
/// A message too big for the queue has its body uploaded to a blob, and the queue message carries only a small envelope
/// with the blob's URI (the claim-check pattern). When a processor or receiver gets that message, it downloads the body
/// before the handler sees it, so handlers read large and small messages the same way. The blob is deleted once the
/// message is completed, and kept when the message is dead-lettered so a replay still has its body. Smaller messages are
/// sent as usual unless you set a threshold.
/// </para>
/// <para>
/// <b>Every client that reads the queue needs the same store</b>: call one of these methods, pointing at the same
/// container, on the sending client and on every client that receives from the queue or its poison queue. Large messages
/// travel in the library's envelope, which only version 3.1 or later of this library reads.
/// </para>
/// <para>
/// <b>Some blobs are left behind.</b> A send that fails after the upload deletes its blob on a best-effort basis, and a
/// message that expires or is cleared from the queue never gets processed, so its blob stays. Add an Azure Storage
/// lifecycle management rule that deletes blobs in the container older than the queue's message time to live plus a
/// margin (and long enough to replay dead-lettered messages).
/// </para>
/// <para>
/// The identity needs the <c>Storage Blob Data Contributor</c> role on the container or the account. One that is not
/// allowed to create containers needs the container created ahead of time and <c>createContainerIfNotExists: false</c>.
/// </para>
/// </remarks>
public static class BlobStorageQueueClientBuilderExtensions
{
    /// <summary>The container used when none is named: <c>queue-large-messages</c>.</summary>
    public const string DefaultContainerName = "queue-large-messages";

    /// <summary>
    /// Keeps bodies too big for a queue message as blobs in <paramref name="container"/>, which is created on the first
    /// large send if it does not exist (unless <paramref name="createContainerIfNotExists"/> is false).
    /// </summary>
    /// <param name="builder">The client to attach the store to.</param>
    /// <param name="container">The container to keep bodies in, with the credential to reach it.</param>
    /// <param name="thresholdBytes">
    /// Also send bodies of at least this many bytes to Blob storage even when they would fit on the queue. Null (the
    /// default) sends only what would otherwise be over the 64 KiB limit.
    /// </param>
    /// <param name="createContainerIfNotExists">
    /// True (the default) creates the container on the first large send if it does not exist. Pass false when the
    /// container is created ahead of time and the identity is not allowed to create containers.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    public static IQueueClientBuilder UseBlobStorageForLargeMessages(this IQueueClientBuilder builder, BlobContainerClient container, int? thresholdBytes = null, bool createContainerIfNotExists = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(container);
        return builder.UseBlobStorageForLargeMessages(_ => container, thresholdBytes, createContainerIfNotExists);
    }

    /// <summary>
    /// Keeps bodies too big for a queue message as blobs in the container <paramref name="createContainer"/> returns. It
    /// is called once, from the application's services, the first time the client is used, so the container client can
    /// come from dependency injection (for example one registered with <c>Microsoft.Extensions.Azure</c>). The container
    /// is created on the first large send if it does not exist (unless <paramref name="createContainerIfNotExists"/> is
    /// false).
    /// </summary>
    /// <param name="builder">The client to attach the store to.</param>
    /// <param name="createContainer">Returns the container to keep bodies in, with the credential to reach it.</param>
    /// <param name="thresholdBytes">
    /// Also send bodies of at least this many bytes to Blob storage even when they would fit on the queue. Null (the
    /// default) sends only what would otherwise be over the 64 KiB limit.
    /// </param>
    /// <param name="createContainerIfNotExists">
    /// True (the default) creates the container on the first large send if it does not exist. Pass false when the
    /// container is created ahead of time and the identity is not allowed to create containers.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    public static IQueueClientBuilder UseBlobStorageForLargeMessages(this IQueueClientBuilder builder, Func<IServiceProvider, BlobContainerClient> createContainer, int? thresholdBytes = null, bool createContainerIfNotExists = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(createContainer);
        if (thresholdBytes is { } threshold) ArgumentOutOfRangeException.ThrowIfNegative(threshold, nameof(thresholdBytes));

        var clientName = builder.Name;
        return builder.UseLargeMessageStore(
            provider =>
            {
                var container = createContainer(provider)
                    ?? throw new InvalidOperationException($"{Describe(clientName)} keeps large messages in Blob storage, but the delegate passed to UseBlobStorageForLargeMessages returned no container.");
                return new BlobLargeMessageStore(container, createContainerIfNotExists, provider.GetService<TimeProvider>());
            },
            thresholdBytes);
    }

    /// <summary>
    /// Keeps bodies too big for a queue message as blobs in the container <paramref name="containerName"/>, in the same
    /// storage account as the queue and reached with the same credentials. The container is created on the first large
    /// send if it does not exist (unless <paramref name="createContainerIfNotExists"/> is false).
    /// </summary>
    /// <remarks>
    /// The Blob endpoint is worked out from the client's settings the first time the client is used: a connection
    /// string is used as it is, and an <see cref="QueueClientSettings.EndpointUri"/> such as
    /// <c>https://myaccount.queue.core.windows.net/orders</c> becomes <c>https://myaccount.blob.core.windows.net/</c>,
    /// used with the client's <see cref="QueueClientSettings.TokenCredential"/> (or the one from <c>UseTokenCredential</c>).
    /// The storage emulator's <c>http://127.0.0.1:10001/devstoreaccount1</c> becomes <c>http://127.0.0.1:10000/devstoreaccount1</c>.
    /// When it cannot be worked out (a custom domain, or in-memory queues with no connection details), the first use of
    /// the client throws <see cref="InvalidOperationException"/>; pass a <see cref="BlobContainerClient"/> instead. The
    /// client's retry settings and transport from <see cref="QueueClientSettings.ClientOptions"/> carry over.
    /// </remarks>
    /// <param name="builder">The client to attach the store to.</param>
    /// <param name="containerName">
    /// The container to keep bodies in; defaults to <see cref="DefaultContainerName"/>. Azure's rules apply: 3 to 63
    /// lowercase letters, digits and single hyphens, starting and ending with a letter or digit.
    /// </param>
    /// <param name="thresholdBytes">
    /// Also send bodies of at least this many bytes to Blob storage even when they would fit on the queue. Null (the
    /// default) sends only what would otherwise be over the 64 KiB limit.
    /// </param>
    /// <param name="createContainerIfNotExists">
    /// True (the default) creates the container on the first large send if it does not exist. Pass false when the
    /// container is created ahead of time and the identity is not allowed to create containers.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="containerName"/> is not a valid container name.</exception>
    public static IQueueClientBuilder UseBlobStorageForLargeMessages(this IQueueClientBuilder builder, string containerName = DefaultContainerName, int? thresholdBytes = null, bool createContainerIfNotExists = true)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(containerName);
        if (ContainerNameRules.Check(containerName) is { } problem) throw new ArgumentException($"Invalid container name: {problem}", nameof(containerName));

        var clientName = builder.Name;
        return builder.UseBlobStorageForLargeMessages(
            provider =>
            {
                var settings = provider.GetRequiredService<IOptionsMonitor<QueueClientSettings>>().Get(clientName);
                return BlobEndpoints.ServiceClientFor(settings, Describe(clientName)).GetBlobContainerClient(containerName);
            },
            thresholdBytes,
            createContainerIfNotExists);
    }

    private static string Describe(string clientName) =>
        clientName == Options.DefaultName ? "The default queue client" : $"Queue client '{clientName}'";
}
