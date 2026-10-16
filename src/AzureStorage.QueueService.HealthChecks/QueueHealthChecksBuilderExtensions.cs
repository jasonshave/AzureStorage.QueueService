using AzureStorage.QueueService;
using AzureStorage.QueueService.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Adds AzureStorage.QueueService health checks to <c>services.AddHealthChecks()</c>. Expose them with ASP.NET Core's
/// <c>app.MapHealthChecks("/health")</c>, or read them through <see cref="HealthCheckService"/>.
/// </summary>
public static class QueueHealthChecksBuilderExtensions
{
    /// <summary>
    /// Adds a check that reports how the hosted queue processors are doing, from
    /// <see cref="IQueueProcessorController.GetStatus"/>. It reads in-memory state only and makes no storage calls, so
    /// it is free to run on every probe.
    /// </summary>
    /// <remarks>
    /// <para>The result is the worst of these, across every processor:</para>
    /// <list type="bullet">
    /// <item><paramref name="failureStatus"/> (Unhealthy by default) when a processor's last
    /// <see cref="QueueProcessorHealthOptions.UnhealthyAfterReceiveFailures"/> receives (3 by default) all failed: it
    /// cannot reach its queue, the queue is missing, or it has lost access.</item>
    /// <item><see cref="QueueProcessorHealthOptions.PausedStatus"/> (Degraded by default) when a processor is paused.</item>
    /// <item>Degraded when a message has been in its handler longer than
    /// <see cref="QueueProcessorHealthOptions.MaxHandlerAge"/> (off unless you set it), or when a processor is stopping
    /// or stopped because the host is shutting down.</item>
    /// </list>
    /// <para>A processor that has not started yet (the host is still starting) counts as healthy. The description names
    /// each processor with a problem and says why, and the result's data has one entry per processor, keyed
    /// <c>"{queue}/{handler}"</c>, with its state, receive failures, last error and active handlers.</para>
    /// </remarks>
    /// <param name="builder">The builder from <c>services.AddHealthChecks()</c>.</param>
    /// <param name="name">The check's name, as it appears in the health report.</param>
    /// <param name="failureStatus">What to report when a processor cannot receive; Unhealthy when null.</param>
    /// <param name="tags">Tags for filtering checks, for example to serve this one only on a liveness endpoint.</param>
    /// <param name="configure">Changes the thresholds; see <see cref="QueueProcessorHealthOptions"/>.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A threshold in <paramref name="configure"/> is out of range.</exception>
    public static IHealthChecksBuilder AddAzureStorageQueueProcessors(
        this IHealthChecksBuilder builder,
        string name = "azure-storage-queue-processors",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        Action<QueueProcessorHealthOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var options = new QueueProcessorHealthOptions();
        configure?.Invoke(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.UnhealthyAfterReceiveFailures, 1, nameof(QueueProcessorHealthOptions.UnhealthyAfterReceiveFailures));
        if (options.MaxHandlerAge is { } age) ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(age, TimeSpan.Zero, nameof(QueueProcessorHealthOptions.MaxHandlerAge));

        return builder.Add(new HealthCheckRegistration(
            name,
            provider => new QueueProcessorHealthCheck(provider.GetRequiredService<IQueueProcessorController>(), options),
            failureStatus,
            tags));
    }

    /// <summary>
    /// Adds a check that a queue client's queue can be reached, by reading the queue's properties. Optionally it also
    /// reports Degraded while the queue's poison queue holds messages (see
    /// <see cref="QueueHealthOptions.DegradedWhenPoisonMessages"/>).
    /// </summary>
    /// <remarks>
    /// Every run is a real, billable call to Azure Storage (two with the poison-queue option), so mind how often your
    /// probes run it, and consider tagging it so only an occasional readiness probe uses it. A missing queue, a refused
    /// credential, an unreachable account or a run longer than <see cref="QueueHealthOptions.Timeout"/> reports
    /// <paramref name="failureStatus"/>, with the exception attached.
    /// </remarks>
    /// <param name="builder">The builder from <c>services.AddHealthChecks()</c>.</param>
    /// <param name="clientName">The queue client to check, as registered; null for the default client.</param>
    /// <param name="name">The check's name, as it appears in the health report; null for
    /// <c>"azure-storage-queue"</c>, or <c>"azure-storage-queue-{clientName}"</c> for a named client.</param>
    /// <param name="failureStatus">What to report when the queue cannot be read; Unhealthy when null.</param>
    /// <param name="tags">Tags for filtering checks, for example to serve this one only on a readiness endpoint.</param>
    /// <param name="configure">Changes the check's settings; see <see cref="QueueHealthOptions"/>.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="QueueHealthOptions.Timeout"/> is zero or negative.</exception>
    public static IHealthChecksBuilder AddAzureStorageQueue(
        this IHealthChecksBuilder builder,
        string? clientName = null,
        string? name = null,
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        Action<QueueHealthOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new QueueHealthOptions();
        configure?.Invoke(options);
        if (options.Timeout is { } timeout) ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero, nameof(QueueHealthOptions.Timeout));

        var client = clientName ?? string.Empty;
        name ??= client.Length == 0 ? "azure-storage-queue" : $"azure-storage-queue-{client}";

        return builder.Add(new HealthCheckRegistration(
            name,
            provider => new QueueHealthCheck(provider.GetRequiredService<IQueueClientFactory>(), client, options),
            failureStatus,
            tags,
            options.Timeout));
    }
}
