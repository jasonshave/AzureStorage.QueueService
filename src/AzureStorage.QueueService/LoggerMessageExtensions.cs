using Microsoft.Extensions.Logging;

namespace AzureStorage.QueueService;

public static partial class LoggerMessageExtensions
{
    [LoggerMessage(0, LogLevel.Information, "Processed message successfully, removing queue message id: {messageId}")]
    public static partial void LogProcessedMessage(this ILogger logger, string messageId);

    [LoggerMessage(1, LogLevel.Information, "Processing {messageCount} storage queue message(s).")]
    public static partial void LogMessageCount(this ILogger logger, int messageCount);

    [LoggerMessage(2, LogLevel.Error, "There was a problem sending the message. The error was: {errorMessage}")]
    public static partial void LogSendError(this ILogger logger, string errorMessage);
}