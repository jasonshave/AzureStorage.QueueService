using Azure.Core;
using Azure.Storage.Queues;

namespace AzureStorage.QueueService;

/// <summary>
/// One client's settings: how to connect, which queue, and how messages are sent and dead-lettered. Bound from
/// configuration with <c>AddClient(name, IConfiguration)</c> or set in code with <c>AddClient(name, settings => ...)</c>.
/// </summary>
public class QueueClientSettings
{
    /// <summary>
    /// The storage account connection string. Use this together with <see cref="QueueName"/>,
    /// or use <see cref="EndpointUri"/> and <see cref="TokenCredential"/> instead.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// The queue. Needed with <see cref="ConnectionString"/>, and with an <see cref="EndpointUri"/> that is the storage
    /// account's endpoint rather than the queue's.
    /// </summary>
    public string? QueueName { get; set; }

    /// <summary>
    /// When <c>true</c>, the queue is created the first time the client is used if it does not already exist.
    /// </summary>
    public bool CreateIfNotExists { get; set; }

    /// <summary>
    /// The queue's URI (<c>https://account.queue.core.windows.net/orders</c>), or the storage account's queue endpoint
    /// (<c>https://account.queue.core.windows.net/</c>) together with <see cref="QueueName"/>. Use it with
    /// <see cref="TokenCredential"/>.
    /// </summary>
    public Uri? EndpointUri { get; set; }

    /// <summary>The identity to connect with when using <see cref="EndpointUri"/>; see also <c>UseTokenCredential</c>.</summary>
    public TokenCredential? TokenCredential { get; set; }

    /// <summary>
    /// How long a message received with <see cref="IQueueReceiver.ReceiveAsync{T}"/> stays hidden from other consumers
    /// when the call does not say. When not set, the service default of 30 seconds applies. A processor uses its own
    /// <see cref="QueueProcessorOptions.VisibilityTimeout"/> instead.
    /// </summary>
    public TimeSpan? VisibilityTimeout { get; set; }

    /// <summary>
    /// Where dead-lettered messages go. Defaults to the queue name plus <c>-poison</c>; set it explicitly when the queue
    /// name is too long for that (Azure allows 63 characters) or when a shared poison queue is wanted.
    /// </summary>
    public string? PoisonQueueName { get; set; }

    /// <summary>
    /// The serializer for this client only. Set with <c>UseSerializer</c> or <c>UseJsonSerializer</c> on the client
    /// builder; when null, the serializer shared by all clients is used.
    /// </summary>
    public IQueueMessageSerializer? Serializer { get; set; }

    /// <summary>
    /// Options for the underlying Azure SDK client, such as the retry policy or
    /// <see cref="QueueClientOptions.MessageEncoding"/> for queues that carry Base64-encoded messages.
    /// </summary>
    public QueueClientOptions? ClientOptions { get; set; }

    /// <summary>
    /// When <c>true</c>, sending wraps each message in a small JSON envelope that carries the current W3C trace context
    /// (<c>traceparent</c> / <c>tracestate</c>), so a processor's span continues the sender's trace. Storage queue
    /// messages have no properties, so the context has to travel in the body. A message sent with no current trace goes
    /// out plain. This setting governs sending only: every client of this library from 3.1 on unwraps the envelope when
    /// it receives, whatever its own setting. Off by default because it changes what is on the queue: turn it on only
    /// when no consumer of the queue is on 3.0 or earlier, or is other code that does not follow the wire format in the README.
    /// The envelope makes each message bigger: roughly 100 bytes plus the <c>tracestate</c>, and a custom serializer's
    /// non-JSON output is carried Base64-encoded, about a third larger. The service rejects messages over 64 KiB, so a
    /// message close to that limit can start failing to send when this is turned on; there is no fallback to plain.
    /// </summary>
    public bool PropagateTraceContext { get; set; }

    /// <summary>
    /// How long a dead-lettered message stays on the poison queue before the service discards it; null (the default) or
    /// <see cref="Timeout.InfiniteTimeSpan"/> keeps it until someone deletes or replays it. Before 3.1 poison messages
    /// took the service default of 7 days and could disappear unseen.
    /// </summary>
    public TimeSpan? PoisonTimeToLive { get; set; }

    /// <summary>
    /// Gzip message bodies of at least this many bytes (as the serializer writes them) before sending; null (the
    /// default) never compresses. A compressed message travels in the library's envelope, so the same caution as
    /// <see cref="PropagateTraceContext"/> applies: every receiver of the queue must be on 3.1 or later. Compression is
    /// skipped for a message it would not make smaller. Set with <c>UseCompression()</c> on the client builder.
    /// </summary>
    public int? CompressionThreshold { get; set; }

    /// <summary>
    /// Send each message's type (<c>typeof(T).Name</c>) as the <see cref="QueueMessageHeaders.MessageType"/> header, for a
    /// routed processor on the receiving side. Like any header, it puts the message in the library's envelope.
    /// </summary>
    public bool SendMessageType { get; set; }

    /// <summary>
    /// Where bodies too big for a queue message go; set with <c>UseLargeMessageStore</c>, or
    /// <c>UseBlobStorageForLargeMessages</c> from the <c>AzureStorage.QueueService.Blobs</c> package. Every client that
    /// reads the queue needs the same store.
    /// </summary>
    public ILargeMessageStore? LargeMessageStore { get; set; }

    /// <summary>
    /// With a <see cref="LargeMessageStore"/>, send bodies of at least this many bytes to it even when they would fit on
    /// the queue; null (the default) sends only what would otherwise be over the 64 KiB limit.
    /// </summary>
    public int? LargeMessageThreshold { get; set; }

    internal static readonly TimeSpan MinVisibility = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxVisibility = TimeSpan.FromDays(7);

    /// <summary>True when the SDK client is built from the connection string; otherwise from the endpoint URI.</summary>
    internal bool UsesConnectionString => !string.IsNullOrEmpty(ConnectionString);

    /// <summary>
    /// The queue named in <see cref="EndpointUri"/>'s path, if any. Read the way the SDK reads it, so an emulator's
    /// path-style URI (<c>http://127.0.0.1:10001/devstoreaccount1/orders</c>) names the queue after the account.
    /// </summary>
    internal string? EndpointQueueName => QueueInUri(EndpointUri);

    private static string? QueueInUri(Uri? uri)
    {
        if (uri is null) return null;
        try
        {
            return new QueueUriBuilder(uri).QueueName?.Trim('/') is { Length: > 0 } name ? name : null;
        }
        catch (Exception e) when (e is FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The queue the SDK client will actually target, following the same precedence as construction: the connection
    /// string path uses <see cref="QueueName"/>; the endpoint path uses the queue in the URI, or <see cref="QueueName"/>
    /// when the URI is the account's endpoint.
    /// </summary>
    internal string? EffectiveQueueName => UsesConnectionString ? QueueName : EndpointQueueName ?? (string.IsNullOrEmpty(QueueName) ? null : QueueName);

    /// <summary>The queue's own URI: <see cref="EndpointUri"/> when it names the queue, or the account endpoint with <see cref="QueueName"/> added.</summary>
    internal Uri? QueueUri =>
        EndpointUri is null || EndpointQueueName is not null || string.IsNullOrEmpty(QueueName)
            ? EndpointUri
            : new QueueUriBuilder(EndpointUri) { QueueName = QueueName }.ToUri();

    /// <summary>
    /// Where dead-lettered messages go: <see cref="PoisonQueueName"/>, or the queue name plus <c>-poison</c>. An empty
    /// name (a blank placeholder in configuration) counts as not set.
    /// </summary>
    internal string? EffectivePoisonQueueName => PoisonQueueNameFor(EffectiveQueueName);

    /// <summary>
    /// The queue an in-memory client stands in for (see <c>AddInMemoryQueues</c>): the one the SDK client would target
    /// (<see cref="EffectiveQueueName"/>) when there are connection details, or <see cref="QueueName"/> alone, since a
    /// test host typically has none at all.
    /// </summary>
    internal string? InMemoryQueueName =>
        UsesConnectionString || EndpointUri is not null ? EffectiveQueueName : (string.IsNullOrEmpty(QueueName) ? null : QueueName);

    /// <summary>The poison queue an in-memory client dead-letters to, named by the same rule as the real one.</summary>
    internal string? InMemoryPoisonQueueName => PoisonQueueNameFor(InMemoryQueueName);

    private string? PoisonQueueNameFor(string? queueName) =>
        HasPoisonQueueName ? PoisonQueueName : (queueName is { Length: > 0 } queue ? $"{queue}-poison" : null);

    private bool HasPoisonQueueName => !string.IsNullOrEmpty(PoisonQueueName);

    /// <summary>
    /// What any SDK client the library builds needs: a way to connect and one queue to target. Used for source queues
    /// and for poison destinations alike. Returns the problem, or null.
    /// </summary>
    internal string? ValidateConnection()
    {
        if (UsesConnectionString)
            return string.IsNullOrEmpty(QueueName) ? "has a ConnectionString but no QueueName." : null;

        if (EndpointUri is null || TokenCredential is null)
            return "is not configured. Set ConnectionString and QueueName, or set EndpointUri (including the queue name) and TokenCredential.";

        return ValidateEndpointQueue();
    }

    /// <summary>Which queue an <see cref="EndpointUri"/> names, and that <see cref="QueueName"/> does not contradict it.</summary>
    private string? ValidateEndpointQueue()
    {
        if (EndpointQueueName is null)
            return string.IsNullOrEmpty(QueueName)
                ? "has an EndpointUri with no queue name in its path, and no QueueName. Put the queue in the URI, or set QueueName when EndpointUri is the storage account's endpoint."
                : null;

        // With endpoint authentication the SDK takes the queue from the URI, so a different QueueName would be
        // silently ignored and every check based on it would be checking the wrong queue.
        if (!string.IsNullOrEmpty(QueueName) && !string.Equals(QueueName, EndpointQueueName, StringComparison.Ordinal))
            return $"has QueueName '{QueueName}' but EndpointUri points at queue '{EndpointQueueName}'. With EndpointUri the queue comes from the URI; remove QueueName or make them agree.";

        return null;
    }

    /// <summary>
    /// The connection plus the processing policy of a source queue: receive timing and the poison destination.
    /// Shared by startup validation and source-queue construction so the two can never disagree. Not applied to
    /// destination clients, which have no policy of their own.
    /// </summary>
    internal string? Validate() => ValidateConnection() ?? ValidatePolicy(EffectiveQueueName);

    /// <summary>
    /// What an in-memory client needs: no credential or connection string, but the same rules for which queue it is
    /// and the same processing policy, so an endpoint or poison queue name that would fail in production fails the
    /// test too. Returns the problem, or null.
    /// </summary>
    internal string? ValidateInMemory()
    {
        if (!UsesConnectionString && EndpointUri is not null && ValidateEndpointQueue() is { } problem) return problem;

        return InMemoryQueueName is { } queueName
            ? ValidatePolicy(queueName)
            : "has no QueueName. In-memory queues need no connection details, but still need a queue name to name the queue and its poison queue.";
    }

    private string? ValidatePolicy(string? queueName)
    {
        if (VisibilityTimeout is { } visibility && (visibility < MinVisibility || visibility > MaxVisibility))
            return $"has a VisibilityTimeout of {visibility}; the service accepts 1 second to 7 days.";

        if (PoisonTimeToLive is { } poisonTtl && poisonTtl != Timeout.InfiniteTimeSpan && poisonTtl < TimeSpan.FromSeconds(1))
            return $"has a PoisonTimeToLive of {poisonTtl}; use at least 1 second, or Timeout.InfiniteTimeSpan (or nothing) for never.";

        if (CompressionThreshold is < 0)
            return $"has a CompressionThreshold of {CompressionThreshold}; it is a size in bytes and cannot be negative.";

        if (LargeMessageThreshold is { } large && large < 0)
            return $"has a LargeMessageThreshold of {large}; it is a size in bytes and cannot be negative.";

        var poison = PoisonQueueNameFor(queueName);
        if (poison is not null && QueueNameRules.Check(poison) is { } problem)
        {
            return !HasPoisonQueueName
                ?$"derives the poison queue name '{poison}' from the queue name, and {problem} Set PoisonQueueName to a valid name."
                : $"has an invalid PoisonQueueName: {problem}";
        }

        if (poison is not null && string.Equals(poison, queueName, StringComparison.Ordinal))
            return $"has PoisonQueueName equal to its own queue name ('{poison}'). Dead-lettered messages would go straight back onto the queue with a fresh dequeue count; use a different poison queue.";

        return null;
    }
}

/// <summary>
/// Azure's rules for a queue name: 3 to 63 characters, lowercase letters, digits and single hyphens, starting and
/// ending with a letter or digit.
/// </summary>
internal static class QueueNameRules
{
    public const int MaxLength = 63;

    /// <summary>Returns a description of the problem, or null when the name is valid.</summary>
    public static string? Check(string name)
    {
        if (name.Length is < 3 or > MaxLength) return $"'{name}' is {name.Length} characters; a queue name must be 3 to {MaxLength}.";
        if (!char.IsAsciiLetterLower(name[0]) && !char.IsAsciiDigit(name[0])) return $"'{name}' must start with a lowercase letter or digit.";
        if (!char.IsAsciiLetterLower(name[^1]) && !char.IsAsciiDigit(name[^1])) return $"'{name}' must end with a lowercase letter or digit.";
        if (name.Contains("--")) return $"'{name}' must not contain consecutive hyphens.";
        foreach (var c in name)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-') return $"'{name}' may only contain lowercase letters, digits and hyphens.";
        }

        return null;
    }
}
