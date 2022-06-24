using JasonShave.AzureStorage.QueueService.Exceptions;
using JasonShave.AzureStorage.QueueService.Interfaces;
using System.Text;
using System.Text.Json;

namespace JasonShave.AzureStorage.QueueService.Converters;

internal class JsonQueueMessageConverter : IMessageConverter
{
    private readonly JsonSerializerOptions _serializerOptions;

    public JsonQueueMessageConverter(JsonSerializerOptions? serializerOptions = null)
    {
        _serializerOptions = serializerOptions ?? new JsonSerializerOptions();
    }

    public TOutput? Convert<TOutput>(BinaryData input)
    {
        if (input is not BinaryData) { Convert(input.ToString());}

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
        if (input is BinaryData)
        {
            byte[] byteData = System.Convert.FromBase64String(input);
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
}