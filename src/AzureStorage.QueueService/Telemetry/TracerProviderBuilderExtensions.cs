using OpenTelemetry.Trace;

namespace AzureStorage.QueueService.Telemetry;

public static class TracerProviderBuilderExtensions
{
    public static TracerProviderBuilder AddAzureStorageQueueTracing(this TracerProviderBuilder builder)
    {
        builder.AddSource(QueueServiceDiagnostics.ServiceName);
        return builder;
    }
}
