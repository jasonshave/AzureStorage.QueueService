using System.Text.Json;

namespace AzureStorage.QueueService;

internal sealed class JsonQueueMessageSerializer : IQueueMessageSerializer
{
    private readonly JsonSerializerOptions _options;

    public JsonQueueMessageSerializer(JsonSerializerOptions? options = null)
    {
        _options = options ?? new JsonSerializerOptions();
    }

    public BinaryData Serialize<T>(T value) => BinaryData.FromObjectAsJson(value, _options);

    public T? Deserialize<T>(BinaryData data)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(data, _options);
        }
        catch (Exception e)
        {
            throw new DeserializationException($"Could not deserialize the queue message as {typeof(T).Name}: {e.Message}", e);
        }
    }
}
