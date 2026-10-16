namespace AzureStorage.QueueService;

/// <summary>
/// The service-side identity of a received message.
/// </summary>
public sealed record QueueMessageInfo(
    string MessageId,
    string PopReceipt,
    long DequeueCount,
    DateTimeOffset? InsertedOn,
    DateTimeOffset? ExpiresOn,
    DateTimeOffset? NextVisibleOn);
