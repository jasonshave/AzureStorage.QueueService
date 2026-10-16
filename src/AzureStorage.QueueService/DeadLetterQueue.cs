using Azure;
using Azure.Storage.Queues.Models;

namespace AzureStorage.QueueService;

/// <summary>
/// A queue's poison queue: see what was dead-lettered and why, and move messages back to be processed again. Inject it
/// for the default client, or with <c>[FromKeyedServices("name")]</c> for a named one, or call
/// <see cref="QueueClientFactoryExtensions.GetDeadLetterQueue(IQueueClientFactory, string)"/>.
/// </summary>
public interface IDeadLetterQueue
{
    /// <summary>The poison queue's name.</summary>
    string QueueName { get; }

    /// <summary>The queue whose messages land here, and where <see cref="ReplayAsync"/> sends them back to.</summary>
    string SourceQueueName { get; }

    /// <summary>Looks at up to <paramref name="maxMessages"/> (1 to 32) dead-lettered messages without receiving them.</summary>
    ValueTask<IReadOnlyList<DeadLetteredMessage>> PeekAsync(int maxMessages = 32, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves up to <paramref name="maxMessages"/> messages back to <see cref="SourceQueueName"/>, those for which
    /// <paramref name="filter"/> returns true (all, without one), and returns how many were moved. Each goes back as it
    /// was sent, without the dead-letter record, and gets a fresh dequeue count; its time to live starts again at the
    /// service default of 7 days. Messages the filter skips stay on the poison queue.
    /// </summary>
    /// <remarks>
    /// Each message is copied and then deleted, so a failure between the two can leave it on both queues; the handler
    /// sees it at least once, as with any delivery. Run one replay at a time per poison queue.
    /// </remarks>
    ValueTask<int> ReplayAsync(int maxMessages = int.MaxValue, Func<DeadLetteredMessage, bool>? filter = null, CancellationToken cancellationToken = default);

    /// <summary>The service's approximate count of messages on the poison queue; 0 when it does not exist yet.</summary>
    ValueTask<int> GetApproximateCountAsync(CancellationToken cancellationToken = default);
}

/// <summary>A message on a poison queue, with the record of why it was dead-lettered.</summary>
public sealed class DeadLetteredMessage
{
    private readonly IQueueMessageSerializer _serializer;

    internal DeadLetteredMessage(QueueMessageInfo info, string sourceQueueName, DeadLetterInfo? record, IReadOnlyDictionary<string, string> headers, BinaryData rawBody, DeserializationException? error, IQueueMessageSerializer serializer)
    {
        MessageId = info.MessageId;
        DeadLetteredOn = record?.At ?? info.InsertedOn;
        SourceQueueName = record?.Queue ?? sourceQueueName;
        OriginalMessageId = record?.MessageId;
        Reason = record?.Reason;
        Category = record?.Category;
        DequeueCount = record?.DequeueCount ?? 0;
        Headers = headers;
        RawBody = rawBody;
        Error = error;
        _serializer = serializer;
    }

    /// <summary>The message's id on the poison queue.</summary>
    public string MessageId { get; }

    /// <summary>The id it had on the source queue, when the dead-letter record has it.</summary>
    public string? OriginalMessageId { get; }

    /// <summary>The queue it was dead-lettered from.</summary>
    public string SourceQueueName { get; }

    /// <summary>Why it was dead-lettered, as logged at the time.</summary>
    public string? Reason { get; }

    /// <summary>One of <see cref="DeadLetterCategories"/>, when the record has it.</summary>
    public string? Category { get; }

    /// <summary>How many times it had been delivered when it was dead-lettered.</summary>
    public long DequeueCount { get; }

    /// <summary>When it was dead-lettered.</summary>
    public DateTimeOffset? DeadLetteredOn { get; }

    /// <summary>Headers the sender attached.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>
    /// The serialized message as the sender's serializer wrote it, unwrapped, decompressed and, for a body kept in Blob
    /// storage, downloaded. A message that cannot be read that way (see <see cref="Error"/>) is here as it arrived, or
    /// empty when its body is in Blob storage and could not be downloaded.
    /// </summary>
    public BinaryData RawBody { get; }

    /// <summary>Set when the body could not be unwrapped or downloaded.</summary>
    public DeserializationException? Error { get; }

    /// <summary>Reads the body as <typeparamref name="T"/> with the source client's serializer.</summary>
    /// <exception cref="DeserializationException">The body could not be read as <typeparamref name="T"/>.</exception>
    public T? GetBody<T>() => Error is not null ? throw Error : _serializer.Deserialize<T>(RawBody);
}

internal sealed class DeadLetterQueue : IDeadLetterQueue
{
    // Long enough to look at a batch and move it back; skipped messages are made visible again at the end.
    private static readonly TimeSpan ScanVisibility = TimeSpan.FromMinutes(5);

    private readonly StorageQueue _source;

    public DeadLetterQueue(StorageQueue source)
    {
        _source = source;
    }

    public string QueueName => _source.PoisonQueue().Name;

    public string SourceQueueName => _source.QueueName;

    public async ValueTask<IReadOnlyList<DeadLetteredMessage>> PeekAsync(int maxMessages = 32, CancellationToken cancellationToken = default)
    {
        StorageQueue.ValidateMaxMessages(maxMessages);
        var poison = _source.PoisonQueue();

        IReadOnlyList<TransportMessage> peeked;
        try
        {
            peeked = await poison.PeekAsync(maxMessages, cancellationToken);
        }
        catch (RequestFailedException e) when (e.ErrorCode == QueueErrorCode.QueueNotFound)
        {
            return [];
        }

        var messages = new List<DeadLetteredMessage>(peeked.Count);
        foreach (var m in peeked) messages.Add(await ReadAsync(m, cancellationToken));
        return messages;
    }

    public async ValueTask<int> ReplayAsync(int maxMessages = int.MaxValue, Func<DeadLetteredMessage, bool>? filter = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxMessages);
        var poison = _source.PoisonQueue();

        var replayed = 0;
        // Skipped messages, by id, with their latest receipt: made visible again at the end.
        var skipped = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            while (replayed < maxMessages)
            {
                IReadOnlyList<TransportMessage> batch;
                try
                {
                    batch = await poison.ReceiveAsync(StorageQueue.ServiceMaxMessages, ScanVisibility, cancellationToken);
                }
                catch (RequestFailedException e) when (e.ErrorCode == QueueErrorCode.QueueNotFound)
                {
                    break;
                }

                // Stop when the queue is empty, or when everything left is something already looked at and skipped
                // (a slow scan outlives ScanVisibility and sees those again).
                var anythingNew = false;
                foreach (var m in batch)
                {
                    if (skipped.ContainsKey(m.Info.MessageId))
                    {
                        skipped[m.Info.MessageId] = m.Info.PopReceipt;
                        continue;
                    }

                    anythingNew = true;
                    if (replayed >= maxMessages || (filter is not null && !filter(await ReadAsync(m, cancellationToken))))
                    {
                        skipped[m.Info.MessageId] = m.Info.PopReceipt;
                        continue;
                    }

                    await _source.SendWireAsync(WithoutDeadLetterRecord(m.Body), cancellationToken);
                    await poison.DeleteAsync(m.Info.MessageId, m.Info.PopReceipt, cancellationToken);
                    replayed++;
                }

                if (!anythingNew) break;
            }
        }
        finally
        {
            foreach (var (id, receipt) in skipped)
            {
                try
                {
                    await poison.UpdateVisibilityAsync(id, receipt, TimeSpan.Zero, CancellationToken.None);
                }
                catch (RequestFailedException)
                {
                    // Its scan lease lapsed and someone else has it, or it is gone; either way it is not hidden by us.
                }
            }
        }

        return replayed;
    }

    public async ValueTask<int> GetApproximateCountAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return (await _source.PoisonQueue().GetPropertiesAsync(cancellationToken)).ApproximateMessagesCount;
        }
        catch (RequestFailedException e) when (e.ErrorCode == QueueErrorCode.QueueNotFound)
        {
            return 0;
        }
    }

    private async ValueTask<DeadLetteredMessage> ReadAsync(TransportMessage m, CancellationToken cancellationToken)
    {
        // Read like any received message (envelope, decompression, a body kept in Blob storage), but never settled
        // through this object: replay works on the transport directly.
        var message = MessageCodec.Received<BinaryData>(m, _source);
        var error = message.Error ?? await _source.ResolvePayloadAsync(message.Lease, cancellationToken);
        var record = message.Lease.Envelope?.DeadLetter;
        return new DeadLetteredMessage(m.Info, _source.QueueName, record, message.Headers, message.RawBody, error, _source.Codec.Serializer);
    }

    /// <summary>The message as it was before it was dead-lettered: the envelope without its record, or just the body when nothing else is left.</summary>
    internal static BinaryData WithoutDeadLetterRecord(BinaryData wire)
    {
        if (MessageEnvelope.TryRead(wire, out var envelope, out _) != EnvelopeKind.Envelope || envelope!.DeadLetter is null) return wire;

        var copy = envelope.Clone();
        copy.DeadLetter = null;
        return copy.CarriesOnlyBody ? copy.Body! : copy.Write();
    }
}
