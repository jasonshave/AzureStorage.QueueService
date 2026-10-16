namespace AzureStorage.QueueService;

/// <summary>
/// A message looked at without receiving it. It has no pop receipt, so there is nothing to complete or abandon.
/// </summary>
public sealed class PeekedQueueMessage<T>
{
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

    public string MessageId { get; }

    public long DequeueCount { get; }

    public DateTimeOffset? InsertedOn { get; }

    public DateTimeOffset? ExpiresOn { get; }

    public BinaryData RawBody { get; }

    public T? Body { get; }

    public DeserializationException? Error { get; }
}
