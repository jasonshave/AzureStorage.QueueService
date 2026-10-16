namespace AzureStorage.QueueService;

/// <summary>
/// Turns message bodies into bytes and back. One per client; the default is System.Text.Json.
/// Base64 encoding is the SDK's job: set <c>QueueClientSettings.ClientOptions.MessageEncoding</c> for queues
/// shared with Azure Functions or older SDKs.
/// </summary>
public interface IQueueMessageSerializer
{
    /// <summary>Writes <paramref name="value"/> as the bytes that go on the queue.</summary>
    BinaryData Serialize<T>(T value);

    /// <summary>Reads a message body back as <typeparamref name="T"/>.</summary>

    /// <exception cref="DeserializationException">The body could not be read as <typeparamref name="T"/>.</exception>
    T? Deserialize<T>(BinaryData data);
}
