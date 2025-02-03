using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AzureStorage.QueueService.Telemetry;

public static class MetricExtensions
{
    public static async Task ProcessWithMetricsAsync(
        this Histogram<double> histogram,
        Func<Task> processFunc,
        TagList tags,
        Func<Exception, Task>? onFailure = null)
    {
        // More efficient than creating a Stopwatch instance
        var start = Stopwatch.GetTimestamp();
        bool success = false;

        try
        {
            await processFunc();
            success = true;
        }
        catch (Exception ex)
        {
            QueueServiceDiagnostics.Metrics.MessageFailures.Add(1, tags);

            if (onFailure is not null)
            {
                await onFailure(ex);
            }
            else
            {
                // Rethrow so the extension method can bubble up the error
                throw;
            }
        }
        finally
        {
            var elapsedSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
            histogram.Record(elapsedSeconds, tags);

            tags.Add(QueueServiceDiagnostics.Names.QueueProcessSuccess, success);
        }
    }
}
