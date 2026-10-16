namespace AzureStorage.QueueService;

/// <summary>
/// Turns message bodies into bytes and back. One per client; the default is System.Text.Json.
/// Base64 encoding is the SDK's job: set <c>QueueClientSettings.ClientOptions.MessageEncoding</c> for queues
/// shared with Azure Functions or older SDKs.
/// </summary>
public interface IQueueMessageSerializer
{
    BinaryData Serialize<T>(T value);

    /// <exception cref="DeserializationException">The body could not be read as <typeparamref name="T"/>.</exception>
    T? Deserialize<T>(BinaryData data);
}
