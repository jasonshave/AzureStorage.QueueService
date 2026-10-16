namespace AzureStorage.QueueService;

/// <summary>
/// Thrown when a queue message cannot be deserialized into the requested type. The original serializer error
/// is available as <see cref="Exception.InnerException"/>.
/// </summary>
public class DeserializationException : Exception
{
    /// <summary>Creates the exception with a message saying what could not be read.</summary>
    public DeserializationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with the serializer's own error as <see cref="Exception.InnerException"/>.</summary>
    public DeserializationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
