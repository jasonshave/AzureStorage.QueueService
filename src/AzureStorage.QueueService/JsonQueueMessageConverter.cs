using System.Text;
using System.Text.Json;

namespace AzureStorage.QueueService;

internal class JsonQueueMessageConverter : IMessageConverter
{
    private readonly JsonSerializerOptions _serializerOptions;

    public JsonQueueMessageConverter(JsonSerializerOptions? serializerOptions = null)
    {
        _serializerOptions = serializerOptions ?? new JsonSerializerOptions();
    }

    public TOutput? Convert<TOutput>(BinaryData input)
    {
        try
        {
            var convertedMessage = JsonSerializer.Deserialize<TOutput>(input, _serializerOptions);
            return convertedMessage;
        }
        catch (Exception e)
        {
            throw new DeserializationException(e.Message);
        }
    }

    public TOutput? Convert<TOutput>(string input)
    {
        if (IsBase64(input))
        {
            var byteData = System.Convert.FromBase64String(input);
            input = Encoding.UTF8.GetString(byteData);
        }

        try
        {
            var convertedMessage = JsonSerializer.Deserialize<TOutput>(input, _serializerOptions);
            return convertedMessage;
        }
        catch (Exception e)
        {
            throw new DeserializationException(e.Message);
        }
    }

    public BinaryData Convert<TInput>(TInput input)
    {
        var binaryData = new BinaryData(input);
        return binaryData;
    }

    private static bool IsBase64(string input)
    {
        Span<byte> buffer = new Span<byte>(new byte[input.Length]);
        return System.Convert.TryFromBase64String(input, buffer, out _);
    }
}