namespace JasonShave.AzureStorage.QueueService.Interfaces;

internal interface IMessageConverter
{
    TOutput? Convert<TOutput>(BinaryData input);
}