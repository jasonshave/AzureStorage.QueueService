using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using SdkQueueMessage = Azure.Storage.Queues.Models.QueueMessage;

namespace AzureStorage.QueueService;

/// <summary>
/// The raw calls one queue answers: bytes in, bytes out, no serialization, envelopes or telemetry. <see cref="StorageQueue"/>
/// does everything else once, over either the Azure SDK (<see cref="SdkQueueTransport"/>) or an in-memory queue
/// (<see cref="InMemoryQueueTransport"/>), so a test against the in-memory queue runs the code production runs.
/// </summary>
internal interface IQueueTransport
{
    string Name { get; }

    /// <summary>The queue's URI, for the spans' server attributes; null for an in-memory queue.</summary>
    Uri? Uri { get; }

    /// <summary>True when what is sent is Base64-encoded on the way, which makes it a third bigger against the size limit.</summary>
    bool Base64Encoded { get; }

    ValueTask CreateIfNotExistsAsync(IDictionary<string, string>? metadata, CancellationToken cancellationToken);

    /// <param name="timeToLive">In the service's terms: -1 second for never.</param>
    ValueTask<SendReceipt> SendAsync(BinaryData body, TimeSpan? visibilityTimeout, TimeSpan? timeToLive, CancellationToken cancellationToken);

    /// <param name="visibilityTimeout">Null for the service's default of 30 seconds.</param>
    ValueTask<IReadOnlyList<TransportMessage>> ReceiveAsync(int maxMessages, TimeSpan? visibilityTimeout, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<TransportMessage>> PeekAsync(int maxMessages, CancellationToken cancellationToken);

    ValueTask DeleteAsync(string messageId, string popReceipt, CancellationToken cancellationToken);

    ValueTask<VisibilityReceipt> UpdateVisibilityAsync(string messageId, string popReceipt, TimeSpan visibilityTimeout, CancellationToken cancellationToken);

    ValueTask ClearAsync(CancellationToken cancellationToken);

    ValueTask<QueueProperties> GetPropertiesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A message as the transport returns it. <paramref name="Undecodable"/> marks one the SDK could not Base64-decode, whose
/// body is the text as it was on the queue. A peeked message has an empty pop receipt.
/// </summary>
internal sealed record TransportMessage(QueueMessageInfo Info, BinaryData Body, bool Undecodable = false);

internal sealed class SdkQueueTransport : IQueueTransport
{
    private readonly QueueClient _client;

    public SdkQueueTransport(QueueClient client, bool base64Encoded)
    {
        _client = client;
        Base64Encoded = base64Encoded;
    }

    public string Name => _client.Name;

    public Uri? Uri => _client.Uri;

    public bool Base64Encoded { get; }

    public async ValueTask CreateIfNotExistsAsync(IDictionary<string, string>? metadata, CancellationToken cancellationToken) =>
        await _client.CreateIfNotExistsAsync(metadata, cancellationToken);

    public async ValueTask<SendReceipt> SendAsync(BinaryData body, TimeSpan? visibilityTimeout, TimeSpan? timeToLive, CancellationToken cancellationToken) =>
        await _client.SendMessageAsync(body, visibilityTimeout, timeToLive, cancellationToken);

    public async ValueTask<IReadOnlyList<TransportMessage>> ReceiveAsync(int maxMessages, TimeSpan? visibilityTimeout, CancellationToken cancellationToken)
    {
        // Messages the SDK could not Base64-decode are raised as events during the call; collect them so they come back
        // as messages with Error set instead of failing the whole batch.
        SdkQueueMessage[] received;
        var undecodable = DecodingFailures.Begin();
        try
        {
            received = await _client.ReceiveMessagesAsync(maxMessages, visibilityTimeout, cancellationToken);
        }
        finally
        {
            DecodingFailures.End();
        }

        var messages = new List<TransportMessage>(received.Length + undecodable.Count);
        foreach (var m in received)
            messages.Add(new TransportMessage(Info(m), m.Body ?? BinaryData.FromString(m.MessageText ?? string.Empty)));

        foreach (var failure in undecodable)
        {
            if (failure.ReceivedMessage is { } m)
                messages.Add(new TransportMessage(Info(m), BinaryData.FromString(m.MessageText ?? string.Empty), Undecodable: true));
        }

        return messages;
    }

    public async ValueTask<IReadOnlyList<TransportMessage>> PeekAsync(int maxMessages, CancellationToken cancellationToken)
    {
        PeekedMessage[] peeked;
        var undecodable = DecodingFailures.Begin();
        try
        {
            peeked = await _client.PeekMessagesAsync(maxMessages, cancellationToken);
        }
        finally
        {
            DecodingFailures.End();
        }

        var messages = new List<TransportMessage>(peeked.Length + undecodable.Count);
        foreach (var m in peeked)
            messages.Add(new TransportMessage(Info(m), m.Body ?? BinaryData.FromString(m.MessageText ?? string.Empty)));

        foreach (var failure in undecodable)
        {
            if (failure.PeekedMessage is { } m)
                messages.Add(new TransportMessage(Info(m), BinaryData.FromString(m.MessageText ?? string.Empty), Undecodable: true));
        }

        return messages;
    }

    public async ValueTask DeleteAsync(string messageId, string popReceipt, CancellationToken cancellationToken) =>
        await _client.DeleteMessageAsync(messageId, popReceipt, cancellationToken);

    public async ValueTask<VisibilityReceipt> UpdateVisibilityAsync(string messageId, string popReceipt, TimeSpan visibilityTimeout, CancellationToken cancellationToken)
    {
        // No message text: only the visibility changes.
        UpdateReceipt receipt = await _client.UpdateMessageAsync(messageId, popReceipt, (string?)null, visibilityTimeout, cancellationToken);
        return new VisibilityReceipt(receipt.PopReceipt, receipt.NextVisibleOn);
    }

    public async ValueTask ClearAsync(CancellationToken cancellationToken) => await _client.ClearMessagesAsync(cancellationToken);

    public async ValueTask<QueueProperties> GetPropertiesAsync(CancellationToken cancellationToken) =>
        (await _client.GetPropertiesAsync(cancellationToken)).Value;

    private QueueMessageInfo Info(SdkQueueMessage m) =>
        new(m.MessageId, m.PopReceipt, m.DequeueCount, m.InsertedOn, m.ExpiresOn, m.NextVisibleOn) { QueueName = _client.Name };

    private QueueMessageInfo Info(PeekedMessage m) =>
        new(m.MessageId, string.Empty, m.DequeueCount, m.InsertedOn, m.ExpiresOn, null) { QueueName = _client.Name };
}

internal sealed class InMemoryQueueTransport : IQueueTransport
{
    private static readonly TimeSpan ServiceDefaultVisibility = TimeSpan.FromSeconds(30);

    private readonly InMemoryQueue _queue;

    /// <param name="base64Encoded">What the client's options say, so the size limit is checked as it would be in production.</param>
    public InMemoryQueueTransport(InMemoryQueue queue, bool base64Encoded)
    {
        _queue = queue;
        Base64Encoded = base64Encoded;
    }

    public string Name => _queue.Name;

    public Uri? Uri => null;

    public bool Base64Encoded { get; }

    public ValueTask CreateIfNotExistsAsync(IDictionary<string, string>? metadata, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _queue.CreateIfNotExists(metadata);
        return ValueTask.CompletedTask;
    }

    public ValueTask<SendReceipt> SendAsync(BinaryData body, TimeSpan? visibilityTimeout, TimeSpan? timeToLive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(_queue.Send(body, visibilityTimeout, timeToLive));
    }

    public ValueTask<IReadOnlyList<TransportMessage>> ReceiveAsync(int maxMessages, TimeSpan? visibilityTimeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<TransportMessage> messages = _queue.Receive(maxMessages, visibilityTimeout ?? ServiceDefaultVisibility)
            .Select(m => new TransportMessage(new QueueMessageInfo(m.MessageId, m.PopReceipt, m.DequeueCount, m.InsertedOn, m.ExpiresOn, m.NextVisibleOn) { QueueName = _queue.Name }, m.Body))
            .ToList();
        return new(messages);
    }

    public ValueTask<IReadOnlyList<TransportMessage>> PeekAsync(int maxMessages, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<TransportMessage> messages = _queue.Peek(maxMessages)
            .Select(m => new TransportMessage(new QueueMessageInfo(m.MessageId, string.Empty, m.DequeueCount, m.InsertedOn, m.ExpiresOn, null) { QueueName = _queue.Name }, m.Body))
            .ToList();
        return new(messages);
    }

    public ValueTask DeleteAsync(string messageId, string popReceipt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _queue.Delete(messageId, popReceipt);
        return ValueTask.CompletedTask;
    }

    public ValueTask<VisibilityReceipt> UpdateVisibilityAsync(string messageId, string popReceipt, TimeSpan visibilityTimeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(_queue.UpdateVisibility(messageId, popReceipt, visibilityTimeout));
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _queue.Clear();
        return ValueTask.CompletedTask;
    }

    public ValueTask<QueueProperties> GetPropertiesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new(_queue.GetProperties());
    }
}
