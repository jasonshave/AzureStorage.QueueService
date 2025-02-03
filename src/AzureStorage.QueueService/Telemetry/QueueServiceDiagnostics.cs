using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AzureStorage.QueueService.Telemetry;

internal class QueueServiceDiagnostics
{
    public static string ServiceName { get; } = "AzureStorage.QueueService";
    
    public static ActivitySource ActivitySource { get; } = new ActivitySource(ServiceName);

    public static Meter Meter { get; } = new Meter(ServiceName);

    public struct Activities
    {
        public static Activity? StartReadingQueueActivity(Action<TagList> tags) => ActivitySource.StartActivityWithTags(Names.ReadingMessages, tags);
        public static Activity? StartSendingMessageActivity(Action<TagList> tags) => ActivitySource.StartActivityWithTags(Names.SendingMessage, tags);
    }

    public struct Names
    {
        public const string ReadingMessages = "Queue.Read";
        public const string SendingMessage = "Queue.Send";
        public const string QueueName = "queue.name";
        public const string QueueMessageCount = "queue.message_count";
        public const string QueueProcessSuccess = "queue.process_success";
    }

    public struct Metrics
    {
        public static readonly Counter<long> MessagesReceived = Meter.CreateCounter<long>(
            "queue_messages_received_total",
            description: "Total number of messages received from the queue."
        );

        public static readonly Counter<long> MessagesSent = Meter.CreateCounter<long>(
            "queue_messages_sent_total",
            description: "Total number of messages sent to the queue."
        );

        public static readonly Counter<long> MessagesProcessed = Meter.CreateCounter<long>(
            "queue_messages_processed_total",
            description: "Total number of messages successfully processed from the queue."
        );

        public static readonly Counter<long> MessageFailures = Meter.CreateCounter<long>(
            "queue_messages_failed_total",
            description: "Total number of messages that failed processing."
        );

        public static readonly Histogram<double> MessageProcessingDuration = Meter.CreateHistogram<double>(
            "queue_message_processing_duration_seconds",
            description: "Time taken to process a message from the queue."
        );

        public static readonly Histogram<long> MessageSize = Meter.CreateHistogram<long>(
            "queue_message_size_bytes",
            description: "Size of messages sent to the queue."
        );

        public static readonly UpDownCounter<long> QueueDepth = Meter.CreateUpDownCounter<long>(
            "queue_depth",
            description: "Current number of messages in the queue."
        );
    }

    public struct Events
    {        
        public static ActivityEvent QueueMessageReceived = new ActivityEvent("queue.message_received");
        public static ActivityEvent QueueMessageProcessed = new ActivityEvent("queue.message_processed");
    }
}
