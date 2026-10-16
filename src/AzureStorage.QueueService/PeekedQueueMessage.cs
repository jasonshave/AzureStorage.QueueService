namespace AzureStorage.QueueService;

/// <summary>
/// A message looked at without receiving it. It has no pop receipt, so there is nothing to complete or abandon.
/// </summary>
/// <typeparam name="T">The type the body is read as.</typeparam>
public sealed class PeekedQueueMessage<T>
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    /// <summary>Public so a test can build one.</summary>
    public PeekedQueueMessage(string messageId, long dequeueCount, DateTimeOffset? insertedOn, DateTimeOffset? expiresOn, BinaryData rawBody, T? body, DeserializationException? error)
    {
        MessageId = messageId;
        DequeueCount = dequeueCount;
        InsertedOn = insertedOn;
        ExpiresOn = expiresOn;
        RawBody = rawBody;
        Body = body;
        Error = error;
    }

    /// <summary>The service's id for the message.</summary>
    public string MessageId { get; }

    /// <summary>How many times the message has been received.</summary>
    public long DequeueCount { get; }

    /// <summary>When the message was put on the queue.</summary>
    public DateTimeOffset? InsertedOn { get; }

    /// <summary>When the service discards the message.</summary>
    public DateTimeOffset? ExpiresOn { get; }

    /// <summary>The serialized message, unwrapped from the library's envelope if it was in one.</summary>
    public BinaryData RawBody { get; }

    /// <summary>The deserialized body; null when it could not be read (see <see cref="Error"/>) or was JSON <c>null</c>.</summary>
    public T? Body { get; }

    /// <summary>Set when the body could not be decoded or deserialized.</summary>
    public DeserializationException? Error { get; }

    /// <summary>Headers the sender attached; empty when there were none.</summary>
    public IReadOnlyDictionary<string, string> Headers
    {
        get => _headers ?? NoHeaders;
        init => _headers = value;
    }

    private readonly IReadOnlyDictionary<string, string>? _headers;
}
