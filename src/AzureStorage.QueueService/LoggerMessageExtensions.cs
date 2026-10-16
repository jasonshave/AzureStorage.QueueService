using Microsoft.Extensions.Logging;

namespace AzureStorage.QueueService;

internal static partial class LoggerMessageExtensions
{
    // Event ids 0-2 were the 2.x messages without a queue name; removed in 3.0.

    [LoggerMessage(26, LogLevel.Warning, "Queue '{QueueName}': the message handler threw for message {MessageId} after settling it ({Settlement}); the settlement stands.")]
    public static partial void LogHandlerFailedAfterSettling(this ILogger logger, string? queueName, string messageId, MessageSettlement settlement, Exception exception);

    [LoggerMessage(3, LogLevel.Information, "Queue '{QueueName}': processed message {MessageId} successfully; removing it from the queue.")]
    public static partial void LogMessageProcessed(this ILogger logger, string? queueName, string messageId);

    [LoggerMessage(4, LogLevel.Information, "Queue '{QueueName}': processing {MessageCount} message(s).")]
    public static partial void LogMessagesReceived(this ILogger logger, string? queueName, int messageCount);

    [LoggerMessage(5, LogLevel.Error, "Queue '{QueueName}': there was a problem sending the message.")]
    public static partial void LogSendFailed(this ILogger logger, string? queueName, Exception exception);

    [LoggerMessage(6, LogLevel.Error, "Queue '{QueueName}': failed to receive messages.")]
    public static partial void LogReceiveFailed(this ILogger logger, string? queueName, Exception exception);

    [LoggerMessage(7, LogLevel.Error, "Queue '{QueueName}': the message handler threw for message {MessageId} (dequeue count {DequeueCount}). The message was not completed and will be delivered again.")]
    public static partial void LogHandlerFailed(this ILogger logger, string? queueName, string messageId, long dequeueCount, Exception exception);

    [LoggerMessage(8, LogLevel.Warning, "Queue '{QueueName}': message {MessageId} was handled successfully but could not be deleted. The handler probably ran longer than the visibility timeout ({VisibilityTimeout}), so the message may be delivered again.")]
    public static partial void LogDeleteFailed(this ILogger logger, string? queueName, string messageId, TimeSpan visibilityTimeout, Exception exception);

    [LoggerMessage(11, LogLevel.Warning, "Queue '{QueueName}': message {MessageId} moved to '{PoisonQueue}' after {DequeueCount} attempt(s). Reason: {Reason}")]
    public static partial void LogDeadLettered(this ILogger logger, string? queueName, string messageId, string? poisonQueue, long dequeueCount, string reason);

    [LoggerMessage(12, LogLevel.Information, "Processor {Processor}: started with {Workers} worker(s).")]
    public static partial void LogProcessorStarted(this ILogger logger, string processor, int workers);

    [LoggerMessage(13, LogLevel.Error, "Processor {Processor}: receiving failed; trying again in {Backoff}.")]
    public static partial void LogProcessorReceiveFailed(this ILogger logger, string processor, TimeSpan backoff, Exception exception);

    [LoggerMessage(14, LogLevel.Warning, "Processor {Processor}: could not renew the lease on message {MessageId}. It will become visible to other consumers when the lease lapses, even if this handler finishes.")]
    public static partial void LogProcessorRenewFailed(this ILogger logger, string processor, string messageId, Exception exception);

    [LoggerMessage(15, LogLevel.Error, "Processor {Processor}: could not move message {MessageId} to the poison queue; abandoning it instead so it is retried.")]
    public static partial void LogProcessorDeadLetterFailed(this ILogger logger, string processor, string messageId, Exception exception);

    [LoggerMessage(16, LogLevel.Warning, "Processor {Processor}: could not abandon message {MessageId}. It will become visible again when its lease lapses.")]
    public static partial void LogProcessorAbandonFailed(this ILogger logger, string processor, string messageId, Exception exception);

    [LoggerMessage(17, LogLevel.Information, "Processor {Processor}: stopping; waiting up to {DrainTimeout} for {InFlight} handler(s) to finish.")]
    public static partial void LogProcessorDraining(this ILogger logger, string processor, TimeSpan drainTimeout, int inFlight);

    [LoggerMessage(18, LogLevel.Warning, "Processor {Processor}: handlers did not finish within the drain timeout; cancelling them.")]
    public static partial void LogProcessorDrainTimedOut(this ILogger logger, string processor);

    [LoggerMessage(19, LogLevel.Information, "Processor {Processor}: handler cancelled for message {MessageId}; the message was returned to the queue.")]
    public static partial void LogProcessorHandlerCancelled(this ILogger logger, string processor, string messageId);

    [LoggerMessage(20, LogLevel.Information, "Processor {Processor}: stopped.")]
    public static partial void LogProcessorStopped(this ILogger logger, string processor);

    [LoggerMessage(21, LogLevel.Error, "Processor {Processor}: delivering message {MessageId} failed outside the handler (resolving the handler, its scope, or an option callback); the message was abandoned for retry.")]
    public static partial void LogProcessorDeliveryFailed(this ILogger logger, string processor, string messageId, Exception exception);

    [LoggerMessage(22, LogLevel.Error, "Processor {Processor}: the RetryDelay callback threw for dequeue count {DequeueCount}; using {Fallback} instead.")]
    public static partial void LogProcessorRetryDelayFailed(this ILogger logger, string processor, long dequeueCount, TimeSpan fallback, Exception exception);

    [LoggerMessage(23, LogLevel.Warning, "Queue '{QueueName}': message {MessageId} (dequeue count {DequeueCount}) is not valid Base64 and was returned with Error set so it can be dead-lettered.")]
    public static partial void LogDecodingFailed(this ILogger logger, string? queueName, string messageId, long dequeueCount);

    [LoggerMessage(24, LogLevel.Warning, "Processor {Processor}: {InFlight} handler(s) ignored cancellation for {Timeout} after the drain deadline; not waiting any longer. Their messages become visible again when their leases lapse.")]
    public static partial void LogProcessorHandlersIgnoredCancellation(this ILogger logger, string processor, int inFlight, TimeSpan timeout);

    [LoggerMessage(25, LogLevel.Critical, "Processor {Processor}: a worker failed unexpectedly and will restart in {Backoff}. This is a bug in the library; please report it.")]
    public static partial void LogProcessorWorkerFailed(this ILogger logger, string processor, TimeSpan backoff, Exception exception);
}
