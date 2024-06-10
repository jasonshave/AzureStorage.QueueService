using Azure.Storage.Queues.Models;
using JasonShave.AzureStorage.QueueService.Interfaces;

namespace JasonShave.AzureStorage.QueueService.Extensions;

internal static class PeekedMessageExtensions
{
    public static IEnumerable<TMessage> Convert<TMessage>(this PeekedMessage[] peekedMessages, IMessageConverter messageConverter)
    {
        foreach (var convertedMessage in peekedMessages.Select(message => messageConverter.Convert<TMessage>(message.Body)))
        {
            if (convertedMessage is not null) yield return convertedMessage;
        }
    }
}