using OpenTelemetry.Trace;

namespace AzureStorage.QueueService.Telemetry;

/// <summary>Subscribes an OpenTelemetry tracer provider to the library's spans.</summary>
public static class TracerProviderBuilderExtensions
{
    /// <summary>The library's spans: <c>Queue.Process</c> per delivered message, and <c>Queue.Send</c> / <c>Queue.Read</c> when turned on.</summary>
    public static TracerProviderBuilder AddAzureStorageQueueTracing(this TracerProviderBuilder builder)
    {
        builder.AddSource(QueueServiceDiagnostics.ServiceName);
        return builder;
    }
}
