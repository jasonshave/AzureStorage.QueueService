using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AzureStorage.QueueService.HealthChecks;

/// <summary>
/// Reports on the hosted processors from <see cref="IQueueProcessorController.GetStatus"/>. It reads in-memory state
/// only, so it costs nothing to run as often as a probe likes.
/// </summary>
internal sealed class QueueProcessorHealthCheck(IQueueProcessorController controller, QueueProcessorHealthOptions options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var statuses = controller.GetStatus();
        if (statuses.Count == 0)
            return Task.FromResult(HealthCheckResult.Healthy("No queue processors are registered."));

        var worst = HealthStatus.Healthy;
        var problems = new List<(HealthStatus Status, string Text)>();
        var data = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (var s in statuses)
        {
            var key = $"{s.QueueName}/{s.HandlerName}";
            if (data.ContainsKey(key)) key = $"{s.ClientName}:{key}";
            data[key] = Describe(s);

            foreach (var (status, text) in Assess(s, context.Registration.FailureStatus))
            {
                problems.Add((status, $"{key} {text}."));
                if (status < worst) worst = status;
            }
        }

        if (problems.Count == 0)
        {
            var waiting = statuses.Count(s => s.State == QueueProcessorState.NotStarted);
            var healthy = waiting == 0
                ? $"{Count(statuses.Count, "queue processor")} running."
                : $"{Count(statuses.Count - waiting, "queue processor")} running, {waiting} waiting for the host to finish starting.";
            return Task.FromResult(HealthCheckResult.Healthy(healthy, data));
        }

        // Worst first, so the first sentence is the one that decided the result.
        var description = string.Join(" ", problems.OrderBy(p => p.Status).Select(p => p.Text));
        return Task.FromResult(new HealthCheckResult(worst, description, data: data));
    }

    private IEnumerable<(HealthStatus Status, string Text)> Assess(QueueProcessorStatus s, HealthStatus failureStatus)
    {
        if (s.ConsecutiveReceiveFailures >= options.UnhealthyAfterReceiveFailures)
        {
            var error = s.LastReceiveError is null ? "" : $" (last error: {s.LastReceiveError})";
            yield return (failureStatus, $"cannot receive: the last {s.ConsecutiveReceiveFailures} attempts failed{error}");
        }

        switch (s.State)
        {
            case QueueProcessorState.Paused when options.PausedStatus != HealthStatus.Healthy:
                yield return (options.PausedStatus, "is paused and not receiving");
                break;
            case QueueProcessorState.Stopping:
                yield return (HealthStatus.Degraded, "is stopping because the host is shutting down");
                break;
            case QueueProcessorState.Stopped:
                yield return (HealthStatus.Degraded, "has stopped and is not receiving");
                break;
        }

        if (options.MaxHandlerAge is { } limit && s.OldestHandlerAge is { } age && age > limit)
            yield return (HealthStatus.Degraded, $"has had a message in its handler for {Format(age)}, longer than the {Format(limit)} allowed; the handler may be stuck");
    }

    private static Dictionary<string, object?> Describe(QueueProcessorStatus s) => new(StringComparer.Ordinal)
    {
        ["client"] = s.ClientName,
        ["queue"] = s.QueueName,
        ["handler"] = s.HandlerName,
        ["state"] = s.State.ToString(),
        ["activeHandlers"] = s.ActiveHandlers,
        ["maxConcurrentMessages"] = s.MaxConcurrentMessages,
        ["consecutiveReceiveFailures"] = s.ConsecutiveReceiveFailures,
        ["lastReceiveSucceededAt"] = s.LastReceiveSucceededAt,
        ["lastReceiveError"] = s.LastReceiveError,
        ["oldestHandlerAge"] = s.OldestHandlerAge,
    };

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m"
        : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s"
        : $"{Math.Max(0, (int)t.TotalSeconds)}s";
}
