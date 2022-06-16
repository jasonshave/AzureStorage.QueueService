using JasonShave.AzureStorage.QueueService.Interfaces;
using System.Text.Json;
using JasonShave.AzureStorage.QueueService.Exceptions;

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
}