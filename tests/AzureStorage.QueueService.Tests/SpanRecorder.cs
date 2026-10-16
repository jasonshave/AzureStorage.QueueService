using System.Diagnostics;
using AzureStorage.QueueService.Telemetry;

namespace AzureStorage.QueueService.Tests;

/// <summary>
/// Records the library's finished spans. Listeners are process-wide and test classes run in parallel, so tests pick
/// out their own spans by trace id rather than assuming every recorded span is theirs.
/// </summary>
internal sealed class SpanRecorder : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _stopped = new();
    private readonly Signal _spanStopped = new();

    public SpanRecorder()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == QueueServiceDiagnostics.ServiceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_stopped) _stopped.Add(activity);
                _spanStopped.Set();
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>Waits for the finished span named <paramref name="operationName"/> in <paramref name="traceId"/>.</summary>
    public async Task<Activity> WaitForAsync(string operationName, ActivityTraceId traceId)
    {
        Activity? found = null;
        await _spanStopped.WaitUntilAsync(() =>
        {
            lock (_stopped) found = _stopped.SingleOrDefault(a => a.OperationName == operationName && a.TraceId == traceId);
            return found is not null;
        });
        return found!;
    }

    public void Dispose() => _listener.Dispose();
}
