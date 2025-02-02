using System.Diagnostics;

namespace AzureStorage.QueueService.Telemetry;

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
