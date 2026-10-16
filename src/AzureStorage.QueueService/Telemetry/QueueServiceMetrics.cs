using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AzureStorage.QueueService.Telemetry;

/// <summary>
/// The library's instruments. Created through the host's <see cref="IMeterFactory"/> when one is registered (the
/// generic host registers one), so the meters belong to that container: they are disposed with it, and a test can
/// observe them with <c>MetricCollector&lt;T&gt;</c> without seeing other hosts' measurements. Without a factory every
/// client shares library-owned meters. Two meters: <see cref="QueueServiceDiagnostics.ServiceName"/> with the library's
/// own metric names (<c>AddAzureStorageQueueMetrics()</c>), and <see cref="QueueServiceDiagnostics.MessagingMeterName"/>
/// with the OpenTelemetry messaging names, which nobody receives unless they subscribe to it
/// (<c>AddAzureStorageQueueMessagingMetrics()</c>).
/// </summary>
internal sealed class QueueServiceMetrics
{
    // Lazy, so a process whose hosts all have a meter factory never creates the fallback meters at all.
    private static readonly Lazy<QueueServiceMetrics> FallbackInstance = new(() =>
        new QueueServiceMetrics(new Meter(QueueServiceDiagnostics.ServiceName), new Meter(QueueServiceDiagnostics.MessagingMeterName)));

    // Bucket boundaries are part of the stored history: changing them later splits it, so they are set once, here.
    private static readonly double[] DurationBuckets = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300, 600, 1800, 3600];
    private static readonly double[] TimeInQueueBuckets = [0.1, 0.5, 1, 5, 10, 30, 60, 120, 300, 600, 1800, 3600, 7200, 21600, 86400, 604800];
    private static readonly long[] SizeBuckets = [128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 49152, 65536];

    // The messaging conventions' own advice for their duration histograms.
    private static readonly double[] SemanticConventionDurationBuckets = [0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10];

    private readonly ConcurrentDictionary<object, Func<IEnumerable<Measurement<double>>>> _handlerAges = new();
    private readonly ConcurrentDictionary<(string Queue, string Kind), long> _depths = new();

    /// <summary>The library-owned instruments, for when no <see cref="IMeterFactory"/> is registered.</summary>
    public static QueueServiceMetrics Fallback => FallbackInstance.Value;

    public static QueueServiceMetrics Create(IMeterFactory? meterFactory) =>
        meterFactory is null
            ? Fallback
            : new QueueServiceMetrics(
                meterFactory.Create(new MeterOptions(QueueServiceDiagnostics.ServiceName)),
                meterFactory.Create(new MeterOptions(QueueServiceDiagnostics.MessagingMeterName)));

    private QueueServiceMetrics(Meter meter, Meter messaging)
    {
        // The 3.0 instruments: names, kinds and tags unchanged, so stored history stays continuous.
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

        MessageProcessingDuration = meter.CreateHistogram(
            "queue_message_processing_duration_seconds",
            unit: "s",
            description: "Time taken to process a message from the queue.",
            tags: null,
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets });

        MessageSize = meter.CreateHistogram(
            "queue_message_size_bytes",
            unit: "By",
            description: "Size of messages sent to the queue, as the serializer wrote them.",
            tags: null,
            advice: new InstrumentAdvice<long> { HistogramBucketBoundaries = SizeBuckets });

        // Added in 3.1.
        TimeInQueue = meter.CreateHistogram(
            "queue_message_time_in_queue_seconds",
            unit: "s",
            description: "How long a message waited on the queue before a processor started delivering it, measured from when it was first sent; retries count.",
            tags: null,
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = TimeInQueueBuckets });

        ReceiveRequests = meter.CreateCounter<long>(
            "queue_receive_requests_total",
            description: "Receive requests made to the queue, tagged queue.receive_result: message, empty or error. Each is a billable transaction, empty ones included.");

        ActiveHandlers = meter.CreateUpDownCounter<long>(
            "queue_processor_active_handlers",
            description: "Messages a processor is delivering right now. Compare with MaxConcurrentMessages for saturation.");

        LeaseRenewals = meter.CreateCounter<long>(
            "queue_lease_renewals_total",
            description: "Visibility renewals made by processors while handlers ran.");

        LeaseRenewalFailures = meter.CreateCounter<long>(
            "queue_lease_renewal_failures_total",
            description: "Visibility renewals that failed; the message becomes visible to other consumers when its lease lapses.");

        DeadLettered = meter.CreateCounter<long>(
            "queue_messages_dead_lettered_total",
            description: "Messages moved to the poison queue, tagged queue.dead_letter_reason: max_dequeue, deserialization, handler or unroutable.");

        meter.CreateObservableGauge(
            "queue_processor_oldest_handler_age_seconds",
            ObserveHandlerAges,
            unit: "s",
            description: "How long the longest-running handler of each processor has been running. A value that keeps growing is a hung handler.");

        meter.CreateObservableGauge(
            "queue_approximate_messages",
            ObserveDepths,
            unit: "{message}",
            description: "The service's approximate message count, hidden messages included, for queues with depth metrics turned on; tagged queue.kind: main or poison.");

        // The OpenTelemetry messaging names, on their own meter.
        SentMessages = messaging.CreateCounter<long>("messaging.client.sent.messages", unit: "{message}", description: "Number of messages producer attempted to send to the broker.");
        ConsumedMessages = messaging.CreateCounter<long>("messaging.client.consumed.messages", unit: "{message}", description: "Number of messages that were delivered to the application.");
        ProcessDuration = messaging.CreateHistogram("messaging.process.duration", unit: "s", description: "Duration of processing operation.", tags: null,
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = SemanticConventionDurationBuckets });
        OperationDuration = messaging.CreateHistogram("messaging.client.operation.duration", unit: "s", description: "Duration of messaging operation initiated by a producer or consumer client.", tags: null,
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = SemanticConventionDurationBuckets });
    }

    public Counter<long> MessagesReceived { get; }

    public Counter<long> MessagesSent { get; }

    public Counter<long> MessagesProcessed { get; }

    public Counter<long> MessageFailures { get; }

    public Histogram<double> MessageProcessingDuration { get; }

    public Histogram<long> MessageSize { get; }

    public Histogram<double> TimeInQueue { get; }

    public Counter<long> ReceiveRequests { get; }

    public UpDownCounter<long> ActiveHandlers { get; }

    public Counter<long> LeaseRenewals { get; }

    public Counter<long> LeaseRenewalFailures { get; }

    public Counter<long> DeadLettered { get; }

    public Counter<long> SentMessages { get; }

    public Counter<long> ConsumedMessages { get; }

    public Histogram<double> ProcessDuration { get; }

    public Histogram<double> OperationDuration { get; }

    /// <summary>Adds a processor's handler ages to the gauge until the returned handle is disposed.</summary>
    public IDisposable ObserveHandlerAge(Func<IEnumerable<Measurement<double>>> observe)
    {
        var key = new object();
        _handlerAges[key] = observe;
        return new Unregister(() => _handlerAges.TryRemove(key, out _));
    }

    /// <summary>Records the latest approximate count for a queue; reported by the depth gauge until <see cref="ForgetDepth"/>.</summary>
    public void SetDepth(string queueName, string kind, long count) => _depths[(queueName, kind)] = count;

    public void ForgetDepth(string queueName, string kind) => _depths.TryRemove((queueName, kind), out _);

    private IEnumerable<Measurement<double>> ObserveHandlerAges()
    {
        foreach (var observe in _handlerAges.Values)
        {
            foreach (var measurement in observe()) yield return measurement;
        }
    }

    private IEnumerable<Measurement<long>> ObserveDepths()
    {
        foreach (var ((queue, kind), count) in _depths)
            yield return new Measurement<long>(count, new KeyValuePair<string, object?>(QueueServiceDiagnostics.Names.QueueName, queue), new KeyValuePair<string, object?>(QueueServiceDiagnostics.Names.QueueKind, kind));
    }

    private sealed class Unregister(Action action) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) action();
        }
    }
}
