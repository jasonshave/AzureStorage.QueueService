using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Azure.Core;
using Azure.Storage.Queues;

namespace AzureStorage.QueueService;

[ExcludeFromCodeCoverage]
public class QueueClientSettings
{
    public const string SectionName = "AzureStorageQueueClientSettings";

    /// <summary>
    /// The storage account connection string. Use this together with <see cref="QueueName"/>,
    /// or use <see cref="EndpointUri"/> and <see cref="TokenCredential"/> instead.
    /// </summary>
    public string? ConnectionString { get; set; }

    public string? QueueName { get; set; }

    /// <summary>
    /// When <c>true</c>, the queue is created the first time the client is used if it does not already exist.
    /// </summary>
    public bool CreateIfNotExists { get; set; }

    /// <summary>
    /// The full queue URI, including the queue name. Use this together with <see cref="TokenCredential"/>.
    /// </summary>
    public Uri? EndpointUri { get; set; }

    public TokenCredential? TokenCredential { get; set; }

    /// <summary>
    /// How long a received message stays hidden from other consumers while your handler runs. When not set, the
    /// service default of 30 seconds applies. If a handler runs longer than this the message becomes visible again,
    /// another consumer can pick it up, and this consumer's delete fails. Allow for the slowest handler multiplied
    /// by the number of messages received per call, since a batch is processed one message at a time.
    /// </summary>
    public TimeSpan? VisibilityTimeout { get; set; }

    /// <summary>
    /// JSON serializer options for this client only. When set, they take precedence over the options passed to
    /// <c>AddAzureStorageQueueClient</c> for both sending and receiving.
    /// </summary>
    public JsonSerializerOptions? SerializerOptions { get; set; }

    /// <summary>
    /// Options for the underlying Azure SDK client, such as the retry policy or
    /// <see cref="QueueClientOptions.MessageEncoding"/> for queues that carry Base64-encoded messages.
    /// </summary>
    public QueueClientOptions? ClientOptions { get; set; }
}
