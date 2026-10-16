using OpenTelemetry.Metrics;

namespace AzureStorage.QueueService.Telemetry;

/// <summary>Subscribes an OpenTelemetry meter provider to the library's metrics.</summary>
public static class MeterProviderBuilderExtensions
{
    /// <summary>The library's metrics under their own names (<c>queue_messages_processed_total</c> and so on).</summary>
    public static MeterProviderBuilder AddAzureStorageQueueMetrics(this MeterProviderBuilder builder)
    {
        builder.AddMeter(QueueServiceDiagnostics.ServiceName);
        return builder;
    }

    /// <summary>
    /// The same activity under the OpenTelemetry messaging metric names (<c>messaging.client.sent.messages</c>,
    /// <c>messaging.client.consumed.messages</c>, <c>messaging.process.duration</c> and
    /// <c>messaging.client.operation.duration</c>), which standard dashboards recognise. On a meter of its own, so turning
    /// it on leaves the library's own metrics and their history untouched.
    /// </summary>
    public static MeterProviderBuilder AddAzureStorageQueueMessagingMetrics(this MeterProviderBuilder builder)
    {
        builder.AddMeter(QueueServiceDiagnostics.MessagingMeterName);
        return builder;
    }
}
