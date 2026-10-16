using System.Diagnostics;

namespace AzureStorage.QueueService.Telemetry;

/// <summary>
/// Spans that are off by default because they are noisy: one per individual send and receive. The processor's
/// <c>Queue.Process</c> span is always on. Set with <c>ConfigureQueueServiceTelemetry</c>.
/// </summary>
public class QueueServiceTelemetrySettings
{
    /// <summary>
    /// A new <see cref="Activity"/> (span) will be created when an attempt is made to read the queue.
    /// </summary>
    public bool CreateNewActivityOnMessageRetrieval { get; set; }

    /// <summary>
    /// A new <see cref="Activity"/> (span) will be created when an attempt is made to send a message to the queue.
    /// </summary>
    public bool CreateNewActivityOnMessageSend { get; set; }
}
