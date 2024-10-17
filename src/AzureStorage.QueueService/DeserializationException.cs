namespace AzureStorage.QueueService;

public class DeserializationException : Exception
{
    public DeserializationException(string message)
    : base(message)
    {

    }
}