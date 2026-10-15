using Microsoft.Extensions.Logging;

namespace AzureStorage.QueueService;

public static partial class LoggerMessageExtensions
{
    [Obsolete("Use LogMessageProcessed, which includes the queue name. This method will be removed in 3.0.")]
    [LoggerMessage(0, LogLevel.Information, "Processed message successfully, removing queue message id: {MessageId}")]
    public static partial void LogProcessedMessage(this ILogger logger, string messageId);

    [Obsolete("Use LogMessagesReceived, which includes the queue name. This method will be removed in 3.0.")]
    [LoggerMessage(1, LogLevel.Information, "Processing {MessageCount} storage queue message(s).")]
    public static partial void LogMessageCount(this ILogger logger, int messageCount);

    [Obsolete("Use LogSendFailed, which includes the queue name and the exception. This method will be removed in 3.0.")]
    [LoggerMessage(2, LogLevel.Error, "There was a problem sending the message. The error was: {ErrorMessage}")]
    public static partial void LogSendError(this ILogger logger, string errorMessage);

    [LoggerMessage(3, LogLevel.Information, "Queue '{QueueName}': processed message {MessageId} successfully; removing it from the queue.")]
    public static partial void LogMessageProcessed(this ILogger logger, string? queueName, string messageId);

    [LoggerMessage(4, LogLevel.Information, "Queue '{QueueName}': processing {MessageCount} message(s).")]
    public static partial void LogMessagesReceived(this ILogger logger, string? queueName, int messageCount);

    [LoggerMessage(5, LogLevel.Error, "Queue '{QueueName}': there was a problem sending the message.")]
    public static partial void LogSendFailed(this ILogger logger, string? queueName, Exception exception);

    [LoggerMessage(6, LogLevel.Error, "Queue '{QueueName}': failed to receive messages. The error has been passed to the exception handler.")]
    public static partial void LogReceiveFailed(this ILogger logger, string? queueName, Exception exception);

    [LoggerMessage(7, LogLevel.Error, "Queue '{QueueName}': the message handler threw for message {MessageId} (dequeue count {DequeueCount}). The message was not deleted and will become visible again.")]
    public static partial void LogHandlerFailed(this ILogger logger, string? queueName, string messageId, long dequeueCount, Exception exception);

    [LoggerMessage(8, LogLevel.Warning, "Queue '{QueueName}': message {MessageId} was handled successfully but could not be deleted. The handler probably ran longer than the visibility timeout ({VisibilityTimeout}), so the message may be delivered again.")]
    public static partial void LogDeleteFailed(this ILogger logger, string? queueName, string messageId, TimeSpan visibilityTimeout, Exception exception);

    [LoggerMessage(9, LogLevel.Error, "Queue '{QueueName}': the exception handler itself threw while handling message {MessageId}. Continuing with the next message.")]
    public static partial void LogExceptionHandlerFailed(this ILogger logger, string? queueName, string messageId, Exception exception);

    [LoggerMessage(10, LogLevel.Warning, "Queue '{QueueName}': could not read the queue metadata; the handler will receive null metadata.")]
    public static partial void LogMetadataFailed(this ILogger logger, string? queueName, Exception exception);
}
