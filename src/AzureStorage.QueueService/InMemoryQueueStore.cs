using System.Collections.Concurrent;
using Azure;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging;

namespace AzureStorage.QueueService;

/// <summary>
/// The queues of one in-memory storage account, registered by <c>AddInMemoryQueues</c>. Queues are keyed by queue
/// name, so clients that name the same queue share its messages, as they would on the service; that is also how a
/// test reads a poison queue: by registering a client for it.
/// </summary>
internal sealed class InMemoryQueueStore
{
    private readonly ConcurrentDictionary<string, InMemoryQueue> _queues = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public InMemoryQueueStore(TimeProvider time)
    {
        _time = time;
    }

    // Every queue exists: a test should not have to provision what an emulator or deployment script would.
    public InMemoryQueue GetQueue(string queueName) => _queues.GetOrAdd(queueName, name => new InMemoryQueue(name, _time));

    /// <summary>A client over the queue <paramref name="settings"/> names. The settings must have passed <see cref="QueueClientSettings.ValidateInMemory"/>.</summary>
    public IStorageQueue CreateClient(QueueClientSettings settings, IQueueMessageSerializer serializer, ILoggerFactory loggerFactory) =>
        new InMemoryStorageQueue(
            GetQueue(settings.InMemoryQueueName!),
            () => GetQueue(settings.InMemoryPoisonQueueName!),
            serializer,
            loggerFactory,
            settings.VisibilityTimeout,
            settings.PropagateTraceContext);
}

/// <summary>
/// One queue's messages, with the service's rules for visibility, pop receipts, dequeue counts and expiry. Every
/// operation takes the lock, so the processor's concurrent workers see the same consistency a single queue gives them.
/// </summary>
internal sealed class InMemoryQueue
{
    private static readonly TimeSpan MaxVisibility = QueueClientSettings.MaxVisibility;
    private static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromDays(7);
    private static readonly TimeSpan InfiniteTimeToLive = StorageQueueCommon.InfiniteTimeToLive;

    private readonly object _lock = new();
    private readonly List<Entry> _messages = new();
    private readonly TimeProvider _time;
    private IDictionary<string, string>? _metadata;

    public InMemoryQueue(string name, TimeProvider time)
    {
        Name = name;
        _time = time;
    }

    public string Name { get; }

    /// <summary>
    /// Takes the time to live as the service does: -1 second for never. The client converts .NET's -1 ms sentinel first
    /// (<see cref="StorageQueueCommon.ServiceTimeToLive"/>), as the real client does before calling the service.
    /// </summary>
    public SendReceipt Send(BinaryData body, TimeSpan? visibilityTimeout, TimeSpan? timeToLive)
    {
        var visibility = WholeSeconds(visibilityTimeout ?? TimeSpan.Zero);
        var ttl = WholeSeconds(timeToLive ?? DefaultTimeToLive);
        if (visibility < TimeSpan.Zero || visibility > MaxVisibility) throw OutOfRange("visibilitytimeout", visibility, "0 seconds to 7 days");
        if (ttl != InfiniteTimeToLive && ttl < TimeSpan.FromSeconds(1)) throw OutOfRange("messagettl", ttl, "at least 1 second, or -1 second for never");
        if (ttl != InfiniteTimeToLive && visibility >= ttl) throw OutOfRange("visibilitytimeout", visibility, "less than the time to live");

        lock (_lock)
        {
            var now = _time.GetUtcNow();
            var entry = new Entry(Guid.NewGuid().ToString(), body, now, ttl == InfiniteTimeToLive ? DateTimeOffset.MaxValue : now + ttl)
            {
                // The service hands back a receipt on send, and it can settle the message until someone receives it.
                PopReceipt = NewReceipt(),
                NextVisibleOn = now + visibility,
            };
            _messages.Add(entry);
            return QueuesModelFactory.SendReceipt(entry.MessageId, entry.InsertedOn, entry.ExpiresOn, entry.PopReceipt, entry.NextVisibleOn);
        }
    }

    /// <summary>Hides each message received for <paramref name="visibilityTimeout"/>, counts the delivery, and issues a new receipt.</summary>
    public IReadOnlyList<Snapshot> Receive(int maxMessages, TimeSpan visibilityTimeout)
    {
        var visibility = WholeSeconds(visibilityTimeout);
        if (visibility < TimeSpan.FromSeconds(1) || visibility > MaxVisibility) throw OutOfRange("visibilitytimeout", visibility, "1 second to 7 days");

        lock (_lock)
        {
            var now = RemoveExpired();
            var received = new List<Snapshot>();
            foreach (var entry in _messages)
            {
                if (received.Count == maxMessages) break;
                if (entry.NextVisibleOn > now) continue;

                entry.DequeueCount++;
                entry.PopReceipt = NewReceipt();
                entry.NextVisibleOn = now + visibility;
                received.Add(entry.Snapshot());
            }

            return received;
        }
    }

    public IReadOnlyList<Snapshot> Peek(int maxMessages)
    {
        lock (_lock)
        {
            var now = RemoveExpired();
            return _messages.Where(m => m.NextVisibleOn <= now).Take(maxMessages).Select(m => m.Snapshot()).ToList();
        }
    }

    public void Delete(string messageId, string popReceipt)
    {
        lock (_lock)
        {
            RemoveExpired();
            _messages.Remove(Find(messageId, popReceipt));
        }
    }

    /// <summary>Sets the message visible <paramref name="visibilityTimeout"/> from now and replaces its receipt.</summary>
    public VisibilityReceipt UpdateVisibility(string messageId, string popReceipt, TimeSpan visibilityTimeout)
    {
        var visibility = WholeSeconds(visibilityTimeout);
        if (visibility < TimeSpan.Zero || visibility > MaxVisibility) throw OutOfRange("visibilitytimeout", visibility, "0 seconds to 7 days");

        lock (_lock)
        {
            var now = RemoveExpired();
            var entry = Find(messageId, popReceipt);
            entry.PopReceipt = NewReceipt();
            entry.NextVisibleOn = now + visibility;
            return new VisibilityReceipt(entry.PopReceipt, entry.NextVisibleOn);
        }
    }

    public void CreateIfNotExists(IDictionary<string, string>? metadata)
    {
        // Like the service, an existing queue keeps the metadata it was created with.
        lock (_lock) _metadata ??= metadata is null ? null : new Dictionary<string, string>(metadata);
    }

    public void Clear()
    {
        lock (_lock) _messages.Clear();
    }

    public QueueProperties GetProperties()
    {
        lock (_lock)
        {
            RemoveExpired();
            // The service's count includes hidden messages; so does this one.
            return QueuesModelFactory.QueueProperties(_metadata ?? new Dictionary<string, string>(), _messages.Count);
        }
    }

    // Expiry is applied lazily, whenever the queue is looked at, against the injected clock.
    private DateTimeOffset RemoveExpired()
    {
        var now = _time.GetUtcNow();
        _messages.RemoveAll(m => m.ExpiresOn <= now);
        return now;
    }

    // The service answers a settle with an outdated receipt — the message was received again, or updated, since — as
    // though the message did not exist, so the caller's lease-lost handling runs exactly as it would in production.
    private Entry Find(string messageId, string popReceipt) =>
        _messages.Find(m => m.MessageId == messageId && m.PopReceipt == popReceipt)
        ?? throw new RequestFailedException(404, $"The specified message does not exist (message {messageId} in queue '{Name}', or its pop receipt is out of date).", QueueErrorCode.MessageNotFound.ToString(), null);

    // The SDK sends whole seconds, dropping any fraction, so the service never sees one.
    private static TimeSpan WholeSeconds(TimeSpan value) => TimeSpan.FromSeconds(Math.Truncate(value.TotalSeconds));

    private static string NewReceipt() => Guid.NewGuid().ToString("N");

    private RequestFailedException OutOfRange(string parameter, TimeSpan value, string allowed) =>
        new(400, $"Queue '{Name}': {parameter} of {value} is out of range; the service accepts {allowed}.", QueueErrorCode.OutOfRangeQueryParameterValue.ToString(), null);

    /// <summary>A message as it stood at one moment; what callers get instead of the live, lock-protected entry.</summary>
    internal sealed record Snapshot(string MessageId, string PopReceipt, long DequeueCount, DateTimeOffset InsertedOn, DateTimeOffset ExpiresOn, DateTimeOffset NextVisibleOn, BinaryData Body);

    private sealed class Entry(string messageId, BinaryData body, DateTimeOffset insertedOn, DateTimeOffset expiresOn)
    {
        public string MessageId { get; } = messageId;
        public BinaryData Body { get; } = body;
        public DateTimeOffset InsertedOn { get; } = insertedOn;
        public DateTimeOffset ExpiresOn { get; } = expiresOn;
        public required string PopReceipt { get; set; }
        public required DateTimeOffset NextVisibleOn { get; set; }
        public long DequeueCount { get; set; }

        public Snapshot Snapshot() => new(MessageId, PopReceipt, DequeueCount, InsertedOn, ExpiresOn, NextVisibleOn, Body);
    }
}
