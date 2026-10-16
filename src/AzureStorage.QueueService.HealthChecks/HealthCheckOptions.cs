using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AzureStorage.QueueService.HealthChecks;

/// <summary>
/// Settings for the processor health check added by
/// <see cref="Microsoft.Extensions.DependencyInjection.QueueHealthChecksBuilderExtensions.AddAzureStorageQueueProcessors"/>.
/// The defaults suit most applications; change them only when a threshold does not match how your handlers behave.
/// </summary>
public sealed class QueueProcessorHealthOptions
{
    /// <summary>
    /// How many receives in a row must fail before a processor counts as failing and the check reports its failure
    /// status (Unhealthy unless you chose another). A single failed receive is often a network blip the processor
    /// recovers from on its own, so the default is 3. Must be at least 1.
    /// </summary>
    public int UnhealthyAfterReceiveFailures { get; set; } = 3;

    /// <summary>
    /// What to report while a processor is paused through <see cref="IQueueProcessorController.Pause"/>. Paused
    /// processors receive nothing, so the default is <see cref="HealthStatus.Degraded"/>. Set it to
    /// <see cref="HealthStatus.Healthy"/> when pausing is a normal part of how you run (for example, a circuit breaker
    /// that pauses while a downstream service is down).
    /// </summary>
    public HealthStatus PausedStatus { get; set; } = HealthStatus.Degraded;

    /// <summary>
    /// How long a single message may stay in its handler before the check reports <see cref="HealthStatus.Degraded"/>,
    /// which usually means a handler is stuck. Null (the default) turns this off. Set it comfortably above the longest
    /// time a handler normally takes. Must be greater than zero when set.
    /// </summary>
    public TimeSpan? MaxHandlerAge { get; set; }
}

/// <summary>
/// Settings for the queue health check added by
/// <see cref="Microsoft.Extensions.DependencyInjection.QueueHealthChecksBuilderExtensions.AddAzureStorageQueue"/>.
/// </summary>
public sealed class QueueHealthOptions
{
    /// <summary>
    /// When true, the check also reads how many messages are on the queue's poison queue (where messages go after
    /// failing too many times) and reports <see cref="HealthStatus.Degraded"/> when there are any. This costs a second
    /// storage call on every run. Off by default.
    /// </summary>
    public bool DegradedWhenPoisonMessages { get; set; }

    /// <summary>
    /// How long one run of the check may take before it gives up and reports its failure status. The Azure SDK retries
    /// a failing call several times, so without a limit an unreachable storage account can hold a health probe open for
    /// minutes. Defaults to 10 seconds; null means no limit.
    /// </summary>
    public TimeSpan? Timeout { get; set; } = TimeSpan.FromSeconds(10);
}
