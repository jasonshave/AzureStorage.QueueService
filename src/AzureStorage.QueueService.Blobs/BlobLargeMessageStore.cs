using System.Globalization;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace AzureStorage.QueueService.Blobs;

/// <summary>
/// Keeps queue message bodies that are too big for the queue (over 64 KiB) as blobs in one Blob storage container, and
/// puts only the blob's URI on the queue. Attach it to a client with <c>UseBlobStorageForLargeMessages</c>; you rarely
/// need to create one yourself.
/// </summary>
/// <remarks>
/// <para>
/// Each body is stored as <c>{queue}/{yyyy}/{MM}/{dd}/{random id}</c> in the container, dated in UTC. The library deletes
/// the blob once the message has been processed and completed, and keeps it when the message is dead-lettered, so a
/// replay from the poison queue still has its body.
/// </para>
/// <para>
/// <b>Every client that reads the queue needs the same store</b> (the same container). A receiver without one cannot
/// read these messages and dead-letters them.
/// </para>
/// <para>
/// <b>Some blobs are left behind.</b> If a send fails after the body was uploaded, the library deletes the blob, but only
/// on a best-effort basis. A message that expires on the queue (its time to live runs out) or is removed by clearing the
/// queue is never processed, so its blob is never deleted. Add an Azure Storage lifecycle management rule that deletes
/// blobs in this container older than the queue's message time to live plus a margin (for example, 7 days + 2 days for
/// the default time to live). If you dead-letter messages and replay them later, make the rule long enough to cover that
/// too, because a dead-lettered message still needs its blob.
/// </para>
/// <para>
/// <b>Messages are untrusted input.</b> Anyone who can write to the queue can put any reference in a message, so this
/// store only reads and deletes blobs inside its own container and refuses every other reference. A refused message
/// ends up dead-lettered.
/// </para>
/// <para>
/// The identity used by the container client needs the <c>Storage Blob Data Contributor</c> role on the container (or
/// the account), to create the container, write, read and delete blobs.
/// </para>
/// </remarks>
public sealed class BlobLargeMessageStore : ILargeMessageStore
{
    private const int MaxBlobNameLength = 1024;
    private const int MaxReferenceInMessage = 256;

    private readonly bool _createContainer;
    private readonly TimeProvider _time;
    private readonly Uri _containerUri;
    private readonly string _containerPath;
    private readonly Lock _gate = new();
    private Task? _containerReady;

    /// <summary>
    /// Creates a store that keeps bodies in <paramref name="container"/>.
    /// </summary>
    /// <param name="container">
    /// The container to keep bodies in, with the credential to reach it. If it was created from a URI with a SAS token,
    /// the token is never written to the queue: references are the blob's URI without any query string.
    /// </param>
    /// <param name="createContainerIfNotExists">
    /// When true (the default), the container is created the first time a body is uploaded, if it does not already
    /// exist. This happens once per store. Pass false when the container is created ahead of time and the identity is
    /// not allowed to create containers.
    /// </param>
    /// <param name="timeProvider">The clock that dates each blob's name. Defaults to the system clock.</param>
    public BlobLargeMessageStore(BlobContainerClient container, bool createContainerIfNotExists = true, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(container);

        Container = container;
        _createContainer = createContainerIfNotExists;
        _time = timeProvider ?? TimeProvider.System;
        _containerUri = new Uri(container.Uri.GetLeftPart(UriPartial.Path));
        _containerPath = _containerUri.AbsolutePath.TrimEnd('/') + "/";
    }

    /// <summary>The container bodies are kept in.</summary>
    public BlobContainerClient Container { get; }

    /// <summary>
    /// Uploads <paramref name="body"/> as a new blob named <c>{queueName}/{yyyy}/{MM}/{dd}/{random id}</c> and returns
    /// the blob's URI (without any SAS token), which the library puts on the queue in place of the body. Creates the
    /// container first if this store was told to and has not done so yet.
    /// </summary>
    public async ValueTask<string> UploadAsync(string queueName, BinaryData body, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueName);
        ArgumentNullException.ThrowIfNull(body);

        await EnsureContainerAsync(cancellationToken);

        var now = _time.GetUtcNow().UtcDateTime;
        var name = string.Create(CultureInfo.InvariantCulture, $"{queueName}/{now:yyyy}/{now:MM}/{now:dd}/{Guid.NewGuid():N}");
        var blob = Container.GetBlobClient(name);
        await blob.UploadAsync(body, overwrite: false, cancellationToken);
        return blob.Uri.GetLeftPart(UriPartial.Path);
    }

    /// <summary>
    /// Downloads the body a message refers to. Throws <see cref="ArgumentException"/> without making any request when
    /// <paramref name="reference"/> is not a blob in this store's container, and the service's exception when the blob is
    /// gone; the library dead-letters the message in both cases.
    /// </summary>
    public async ValueTask<BinaryData> DownloadAsync(string reference, CancellationToken cancellationToken)
    {
        var blob = BlobFor(reference);
        var result = await blob.DownloadContentAsync(cancellationToken);
        return result.Value.Content;
    }

    /// <summary>
    /// Deletes the blob a message refers to, with any snapshots. A blob that is already gone is not an error. Throws
    /// <see cref="ArgumentException"/> without making any request when <paramref name="reference"/> is not a blob in
    /// this store's container.
    /// </summary>
    public async ValueTask DeleteAsync(string reference, CancellationToken cancellationToken)
    {
        var blob = BlobFor(reference);
        await blob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, conditions: null, cancellationToken);
    }

    /// <summary>
    /// The blob <paramref name="reference"/> names, reached through this store's own container client, or an
    /// <see cref="ArgumentException"/> when it is anything else: another account, endpoint or container, a query string
    /// (such as a SAS token), or a name that would not come back to the same URI.
    /// </summary>
    internal BlobClient BlobFor(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (!Uri.TryCreate(reference, UriKind.Absolute, out var uri))
            throw Refused(reference, "it is not an absolute URI");

        if (uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
            throw Refused(reference, "it has a query string, fragment or user information, which this store never writes");

        if (!string.Equals(uri.Scheme, _containerUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, _containerUri.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != _containerUri.Port)
            throw Refused(reference, $"it is not on this store's Blob endpoint ({_containerUri.GetLeftPart(UriPartial.Authority)})");

        // AbsolutePath has dot segments already removed, so "/container/../other/x" is "/other/x" here and fails this.
        var path = uri.AbsolutePath;
        if (!path.StartsWith(_containerPath, StringComparison.Ordinal) || path.Length == _containerPath.Length)
            throw Refused(reference, $"it is not a blob in this store's container ({_containerUri})");

        var name = Uri.UnescapeDataString(path[_containerPath.Length..]);
        if (!IsPlainBlobName(name))
            throw Refused(reference, "its blob name is not one this store writes");

        var blob = Container.GetBlobClient(name);

        // Whatever the escaping, the request must go to exactly the blob the reference names.
        var target = new Uri(blob.Uri.GetLeftPart(UriPartial.Path));
        if (!string.Equals(target.AbsolutePath, path, StringComparison.Ordinal))
            throw Refused(reference, "it does not name a blob in this store's container exactly");

        return blob;
    }

    private static bool IsPlainBlobName(string name)
    {
        if (name.Length is 0 or > MaxBlobNameLength) return false;
        foreach (var segment in name.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..") return false;
        }

        foreach (var c in name)
        {
            if (char.IsControl(c) || c == '\\') return false;
        }

        return true;
    }

    private static ArgumentException Refused(string reference, string why)
    {
        // The reference came off the queue, so it may be anything; keep the error message a sensible size.
        var shown = reference.Length > MaxReferenceInMessage ? reference[..MaxReferenceInMessage] + "..." : reference;
        return new ArgumentException($"Refused to use large message reference '{shown}': {why}.", nameof(reference));
    }

    private Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (!_createContainer) return Task.CompletedTask;

        var ready = Volatile.Read(ref _containerReady);
        if (ready is null || ready.IsFaulted || ready.IsCanceled)
        {
            lock (_gate)
            {
                ready = _containerReady;
                // A failed attempt (no permission yet, a network blip) is tried again by the next upload.
                if (ready is null || ready.IsFaulted || ready.IsCanceled)
                {
                    // Shared by every upload waiting on it, so it is not tied to any one caller's cancellation.
                    ready = Task.Run(() => Container.CreateIfNotExistsAsync(PublicAccessType.None, metadata: null, cancellationToken: CancellationToken.None));
                    Volatile.Write(ref _containerReady, ready);
                }
            }
        }

        return ready.IsCompletedSuccessfully ? Task.CompletedTask : ready.WaitAsync(cancellationToken);
    }
}
