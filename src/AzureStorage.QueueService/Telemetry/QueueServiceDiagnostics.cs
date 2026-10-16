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
        public static Activity? StartReadingQueueActivity(in TagList tags) => ActivitySource.StartActivityWithTags(Names.ReadingMessages, ActivityKind.Client, tags);
        public static Activity? StartSendingMessageActivity(in TagList tags) => ActivitySource.StartActivityWithTags(Names.SendingMessage, ActivityKind.Producer, tags);
        public static Activity? StartProcessingMessageActivity(in TagList tags) => ActivitySource.StartActivityWithTags(Names.ProcessingMessage, ActivityKind.Consumer, tags);
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
    }

    // Event names only. An ActivityEvent captures its timestamp when constructed, so a shared static instance
    // would stamp every event with the time the process started.
    public struct Events
    {
        public const string QueueMessageReceived = "queue.message_received";
    }
}
