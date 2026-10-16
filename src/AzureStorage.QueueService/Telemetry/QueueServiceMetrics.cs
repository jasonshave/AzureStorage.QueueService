using System.Diagnostics.Metrics;

namespace AzureStorage.QueueService.Telemetry;

/// <summary>
/// The library's instruments. Created through the host's <see cref="IMeterFactory"/> when one is registered (the
/// generic host registers one), so the meter belongs to that container: it is disposed with it, and a test can observe
/// it with <c>MetricCollector&lt;T&gt;</c> without seeing other hosts' measurements. Without a factory every client shares
/// one library-owned meter, as before. Either way the meter is named <see cref="QueueServiceDiagnostics.ServiceName"/>,
/// so <c>AddAzureStorageQueueMetrics()</c> picks it up.
/// </summary>
internal sealed class QueueServiceMetrics
{
    // Lazy, so a process whose hosts all have a meter factory never creates the fallback meter at all.
    private static readonly Lazy<QueueServiceMetrics> FallbackInstance = new(() => new QueueServiceMetrics(new Meter(QueueServiceDiagnostics.ServiceName)));

    /// <summary>The library-owned instruments, for when no <see cref="IMeterFactory"/> is registered.</summary>
    public static QueueServiceMetrics Fallback => FallbackInstance.Value;

    public static QueueServiceMetrics Create(IMeterFactory? meterFactory) =>
        meterFactory is null ? Fallback : new QueueServiceMetrics(meterFactory.Create(new MeterOptions(QueueServiceDiagnostics.ServiceName)));

    private QueueServiceMetrics(Meter meter)
    {
        MessagesReceived = meter.CreateCounter<long>(
            "queue_messages_received_total",
            description: "Total number of messages received from the queue.");

        MessagesSent = meter.CreateCounter<long>(
            "queue_messages_sent_total",
            description: "Total number of messages sent to the queue.");

        MessagesProcessed = meter.CreateCounter<long>(
            "queue_messages_processed_total",
            description: "Total number of messages successfully processed from the queue.");

        MessageFailures = meter.CreateCounter<long>(
            "queue_messages_failed_total",
            description: "Total number of messages that failed processing.");

        MessageProcessingDuration = meter.CreateHistogram<double>(
            "queue_message_processing_duration_seconds",
            description: "Time taken to process a message from the queue.");

        MessageSize = meter.CreateHistogram<long>(
            "queue_message_size_bytes",
            description: "Size of messages sent to the queue.");
    }

    public Counter<long> MessagesReceived { get; }

    public Counter<long> MessagesSent { get; }

    public Counter<long> MessagesProcessed { get; }

    public Counter<long> MessageFailures { get; }

    public Histogram<double> MessageProcessingDuration { get; }

    public Histogram<long> MessageSize { get; }
}
