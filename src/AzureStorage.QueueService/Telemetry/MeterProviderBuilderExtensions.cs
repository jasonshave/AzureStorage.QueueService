using OpenTelemetry.Metrics;

namespace AzureStorage.QueueService.Telemetry;

public static class MeterProviderBuilderExtensions
{
    public static MeterProviderBuilder AddAzureStorageQueueMetrics(this MeterProviderBuilder builder)
    {
        builder.AddMeter(QueueServiceDiagnostics.ServiceName);
        return builder;
    }
}
