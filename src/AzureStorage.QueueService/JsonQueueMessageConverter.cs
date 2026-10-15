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
            return JsonSerializer.Deserialize<TOutput>(input, _serializerOptions);
        }
        catch (Exception e)
        {
            throw new DeserializationException(Describe<TOutput>(e), e);
        }
    }

    public TOutput? Convert<TOutput>(string input)
    {
        // Messages written by this library (and by the v12 SDK by default) are plain JSON; messages written by
        // older SDKs, Azure Functions or the portal are Base64-encoded. Try the text as-is first so that short
        // JSON values which also happen to be valid Base64 ("true", "null", "1234") are not mangled, and only
        // then fall back to decoding.
        try
        {
            return JsonSerializer.Deserialize<TOutput>(input, _serializerOptions);
        }
        catch (JsonException) when (TryDecodeBase64(input, out var decoded))
        {
            try
            {
                return JsonSerializer.Deserialize<TOutput>(decoded, _serializerOptions);
            }
            catch (Exception e)
            {
                throw new DeserializationException(Describe<TOutput>(e), e);
            }
        }
        catch (Exception e)
        {
            throw new DeserializationException(Describe<TOutput>(e), e);
        }
    }

    public BinaryData Convert<TInput>(TInput input)
    {
        // Honour the caller's serializer options on the way out as well as on the way in.
        return BinaryData.FromObjectAsJson(input, _serializerOptions);
    }

    private static string Describe<TOutput>(Exception e) =>
        $"Could not deserialize the queue message as {typeof(TOutput).Name}: {e.Message}";

    private static bool TryDecodeBase64(string input, out string decoded)
    {
        // Decoded output is never longer than the encoded input.
        var buffer = new byte[input.Length];
        if (System.Convert.TryFromBase64String(input, buffer, out var bytesWritten))
        {
            decoded = Encoding.UTF8.GetString(buffer, 0, bytesWritten);
            return true;
        }

        decoded = string.Empty;
        return false;
    }
}
