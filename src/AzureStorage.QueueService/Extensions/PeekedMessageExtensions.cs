using Azure.Storage.Queues.Models;
using JasonShave.AzureStorage.QueueService.Interfaces;

namespace JasonShave.AzureStorage.QueueService.Extensions;

internal static class PeekedMessageExtensions
{
    public static IEnumerable<TMessage> Convert<TMessage>(this PeekedMessage[] peekedMessages, IMessageConverter messageConverter)
    {
        var results = new List<TMessage>();

        foreach (var message in peekedMessages)
        {
            var convertedMessage = messageConverter.Convert<TMessage>(message.MessageText);
            if (convertedMessage is not null) results.Add(convertedMessage);
        }

        return results;
    }
}