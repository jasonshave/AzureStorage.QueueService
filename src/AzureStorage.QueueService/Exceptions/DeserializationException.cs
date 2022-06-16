namespace JasonShave.AzureStorage.QueueService.Exceptions;

public class DeserializationException : Exception
{
    public DeserializationException(string message)
    : base(message)
    {
        
    }
}