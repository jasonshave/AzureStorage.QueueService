using System.Diagnostics;

namespace AzureStorage.QueueService.Telemetry;

internal class QueueServiceDiagnostics
{
    public static string ServiceName { get; } = "AzureStorage.QueueService";

    // Static, as ActivitySources normally are: listeners subscribe by name, and there is nothing to dispose per host.
    // The Meter is not: see QueueServiceMetrics.
    public static ActivitySource ActivitySource { get; } = new ActivitySource(ServiceName);

    public struct Activities
    {
        public static Activity? StartReadingQueueActivity(in TagList tags) => ActivitySource.StartActivityWithTags(Names.ReadingMessages, ActivityKind.Client, tags);
        public static Activity? StartSendingMessageActivity(in TagList tags) => ActivitySource.StartActivityWithTags(Names.SendingMessage, ActivityKind.Producer, tags);

        /// <summary>
        /// Starts the span for processing one message. When the sender attached its trace context, the span always
        /// links to it, as the messaging conventions recommend; and when nothing else is running — the processor's
        /// own loop, the usual case — that context also becomes the parent, so sender and handler share one trace.
        /// An ambient span (a test, or a caller that wraps the processor) stays the parent and keeps only the link.
        /// </summary>
        public static Activity? StartProcessingMessageActivity(in TagList tags, ActivityContext? creationContext)
        {
            if (creationContext is not { } creation)
                return ActivitySource.StartActivityWithTags(Names.ProcessingMessage, ActivityKind.Consumer, tags);

            var parent = Activity.Current is null ? creation : default;
            return ActivitySource.StartActivityWithTags(Names.ProcessingMessage, ActivityKind.Consumer, tags, parent, [new ActivityLink(creation)]);
        }
    }

    public struct Names
    {
        public const string ReadingMessages = "Queue.Read";
        public const string SendingMessage = "Queue.Send";
        public const string ProcessingMessage = "Queue.Process";
        public const string QueueName = "queue.name";
        public const string QueueMessageCount = "queue.message_count";
        public const string QueueProcessSuccess = "queue.process_success";
        public const string MessageId = "queue.message_id";
        public const string DequeueCount = "queue.dequeue_count";
        public const string Outcome = "queue.outcome";
    }

    /// <summary>
    /// OpenTelemetry messaging semantic-convention attributes, emitted alongside the original <see cref="Names"/> tags
    /// so standard App Insights and Grafana views recognise the data. Span names stay as they were.
    /// </summary>
    public struct Messaging
    {
        public const string System = "messaging.system";
        public const string DestinationName = "messaging.destination.name";
        public const string OperationType = "messaging.operation.type";
        public const string OperationName = "messaging.operation.name";
        public const string MessageId = "messaging.message.id";
        public const string BatchMessageCount = "messaging.batch.message_count";
        public const string ErrorType = "error.type";
        public const string ServerAddress = "server.address";
        public const string ServerPort = "server.port";

        // The conventions list well-known values for Service Bus, Event Hubs and Event Grid but none for Storage
        // queues, and allow a custom value for a system they do not list. Dotted like the newer entries (aws.sns),
        // and spelled out so it can never be mistaken for Service Bus queues.
        public const string SystemValue = "azure.storage.queue";

        // Operation types from the conventions. The operation name is free-form per system; ours is the same word.
        public const string Send = "send";
        public const string Receive = "receive";
        public const string Process = "process";
    }

    /// <summary>
    /// The tags every span for one operation on one queue starts with: the original <c>queue.name</c> plus the
    /// semantic-convention attributes that identify the system, destination and operation.
    /// </summary>
    public static TagList OperationTags(string queueName, string operation) => new()
    {
        { Names.QueueName, queueName },
        { Messaging.System, Messaging.SystemValue },
        { Messaging.DestinationName, queueName },
        { Messaging.OperationType, operation },
        { Messaging.OperationName, operation },
    };

    /// <summary>
    /// The tags on every measurement: exactly the 3.0 set. The semantic-convention attributes stay on spans, because a
    /// new label on an existing metric starts a new series and breaks continuity with stored history.
    /// </summary>
    public static TagList MetricTags(string queueName) => new() { { Names.QueueName, queueName } };

    // Event names only. An ActivityEvent captures its timestamp when constructed, so a shared static instance
    // would stamp every event with the time the process started.
    public struct Events
    {
        public const string QueueMessageReceived = "queue.message_received";
    }
}
