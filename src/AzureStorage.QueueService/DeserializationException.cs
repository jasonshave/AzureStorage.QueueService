namespace AzureStorage.QueueService;

/// <summary>
/// Thrown when a queue message cannot be deserialized into the requested type. The original serializer error
/// is available as <see cref="Exception.InnerException"/>.
/// </summary>
public class DeserializationException : Exception
{
    public DeserializationException(string message)
        : base(message)
    {
    }

    public DeserializationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
