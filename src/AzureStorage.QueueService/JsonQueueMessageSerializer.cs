using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AzureStorage.QueueService;

internal sealed class JsonQueueMessageSerializer : IQueueMessageSerializer
{
    /// <summary>
    /// The library's defaults: System.Text.Json's, except that property names are read case-insensitively, so a camelCase
    /// message from JavaScript, Azure Functions or Event Grid fills a PascalCase .NET type instead of arriving with empty
    /// fields. Writing is unchanged (PascalCase).
    /// </summary>
    public static readonly JsonSerializerOptions DefaultOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly JsonSerializerOptions _options;

    public JsonQueueMessageSerializer(JsonSerializerOptions? options = null)
    {
        _options = options ?? DefaultOptions;
    }

    public BinaryData Serialize<T>(T value) => new(JsonSerializer.SerializeToUtf8Bytes(value, TypeInfo<T>()));

    public T? Deserialize<T>(BinaryData data)
    {
        try
        {
            return JsonSerializer.Deserialize(data.ToMemory().Span, TypeInfo<T>());
        }
        catch (Exception e)
        {
            throw new DeserializationException($"Could not deserialize the queue message as {typeof(T).Name}: {e.Message}", e);
        }
    }

    // Metadata from the options' resolver: source-generated when the application gave one (UseJsonSerializer(context)),
    // which is what trimming and native AOT need. Without one, reflection, unless the application turned reflection-based
    // serialization off (as native AOT does), in which case GetTypeInfo explains what to do.
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Reflection is used only when the application left it enabled; JsonSerializer.IsReflectionEnabledByDefault is false under native AOT and trimming.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "As above.")]
    private JsonTypeInfo<T> TypeInfo<T>()
    {
        if (_options.TypeInfoResolver is null && JsonSerializer.IsReflectionEnabledByDefault)
        {
            // Options without a resolver get the reflection-based default; making them read-only fills it in.
            _options.MakeReadOnly(populateMissingResolver: true);
        }

        return (JsonTypeInfo<T>)_options.GetTypeInfo(typeof(T));
    }
}
