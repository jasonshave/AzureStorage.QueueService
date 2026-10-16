using System.Collections.Concurrent;

namespace AzureStorage.QueueService;

/// <summary>
/// Pauses and resumes processors, and reports how each is doing. Inject it anywhere: an admin endpoint, a circuit
/// breaker that pauses a client while a downstream service is down, or a health check (the
/// <c>AzureStorage.QueueService.HealthChecks</c> package builds on <see cref="GetStatus"/>).
/// </summary>
/// <remarks>
/// Pausing stops a client's processors from receiving; messages they already hold are delivered and settled as usual,
/// and nothing is lost. A pause made before the host starts holds from the start. Clients are named as registered; the
/// default client's name is the empty string.
/// </remarks>
public interface IQueueProcessorController
{
    /// <summary>A snapshot of every processor's state.</summary>
    IReadOnlyList<QueueProcessorStatus> GetStatus();

    /// <summary>Stops every processor on <paramref name="clientName"/> from receiving until <see cref="Resume"/>.</summary>
    /// <exception cref="InvalidOperationException">No client with that name is registered.</exception>
    void Pause(string clientName);

    /// <summary>Lets the processors on <paramref name="clientName"/> receive again.</summary>
    /// <exception cref="InvalidOperationException">No client with that name is registered.</exception>
    void Resume(string clientName);

    /// <summary>True when <paramref name="clientName"/>'s processors are paused.</summary>
    bool IsPaused(string clientName);
}

/// <summary>Where a processor is in its life.</summary>
public enum QueueProcessorState
{
    /// <summary>Registered; waiting for the host to finish starting.</summary>
    NotStarted,

    /// <summary>Receiving and delivering.</summary>
    Running,

    /// <summary>Paused through <see cref="IQueueProcessorController"/>: not receiving, finishing what it holds.</summary>
    Paused,

    /// <summary>The host is shutting down; draining.</summary>
    Stopping,

    /// <summary>Stopped.</summary>
    Stopped,
}

/// <summary>One processor's state, from <see cref="IQueueProcessorController.GetStatus"/>.</summary>
/// <param name="ClientName">The client it belongs to; empty for the default client.</param>
/// <param name="QueueName">The queue it reads.</param>
/// <param name="HandlerName">The handler, as it appears in logs.</param>
/// <param name="MessageType">The type it reads bodies as.</param>
/// <param name="State">Where it is in its life.</param>
/// <param name="ActiveHandlers">Messages being delivered right now; for a batch processor, the messages in the batches being handled.</param>
/// <param name="MaxConcurrentMessages">The most messages it delivers at once; for a batch processor, <see cref="QueueBatchProcessorOptions.MaxConcurrentBatches"/> × <see cref="QueueBatchProcessorOptions.MaxBatchSize"/>.</param>
/// <param name="LastReceiveSucceededAt">When a receive last succeeded, empty or not; null before the first.</param>
/// <param name="LastReceiveFailedAt">When a receive last failed; null if none has.</param>
/// <param name="ConsecutiveReceiveFailures">Receives that have failed in a row; 0 after a success.</param>
/// <param name="LastReceiveError">The last receive failure's message, while failures continue; null after a success.</param>
/// <param name="OldestHandlerAge">How long the longest-running delivery has been running; null when none is.</param>
public sealed record QueueProcessorStatus(
    string ClientName,
    string QueueName,
    string HandlerName,
    Type MessageType,
    QueueProcessorState State,
    int ActiveHandlers,
    int MaxConcurrentMessages,
    DateTimeOffset? LastReceiveSucceededAt,
    DateTimeOffset? LastReceiveFailedAt,
    int ConsecutiveReceiveFailures,
    string? LastReceiveError,
    TimeSpan? OldestHandlerAge);

/// <summary>What the controller needs from a processor.</summary>
internal interface IControlledProcessor
{
    string ClientName { get; }

    QueueProcessorStatus GetStatus();
}

internal sealed class QueueProcessorController : IQueueProcessorController
{
    private readonly QueueClientSettingsRegistry _registry;
    private readonly ConcurrentDictionary<IControlledProcessor, byte> _processors = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _paused = new(StringComparer.Ordinal);

    public QueueProcessorController(QueueClientSettingsRegistry registry)
    {
        _registry = registry;
    }

    public IReadOnlyList<QueueProcessorStatus> GetStatus() => _processors.Keys.Select(p => p.GetStatus()).ToList();

    public void Pause(string clientName)
    {
        ThrowIfUnknown(clientName);
        _paused.TryAdd(clientName, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    public void Resume(string clientName)
    {
        ThrowIfUnknown(clientName);
        if (_paused.TryRemove(clientName, out var resumed)) resumed.TrySetResult();
    }

    public bool IsPaused(string clientName) => _paused.ContainsKey(clientName);

    /// <summary>Completes when <paramref name="clientName"/> is not paused (at once if it is not paused now).</summary>
    public Task WhenResumedAsync(string clientName, CancellationToken cancellationToken) =>
        _paused.TryGetValue(clientName, out var paused) ? paused.Task.WaitAsync(cancellationToken) : Task.CompletedTask;

    public IDisposable Register(IControlledProcessor processor)
    {
        _processors[processor] = 0;
        return new Registration(() => _processors.TryRemove(processor, out _));
    }

    private void ThrowIfUnknown(string clientName)
    {
        ArgumentNullException.ThrowIfNull(clientName);
        var known = clientName.Length == 0 ? _registry.HasDefaultClient : _registry.Contains(clientName);
        if (known) return;

        var registered = _registry.ClientNames.Select(n => $"'{n}'").Concat(_registry.HasDefaultClient ? ["'' (the default client)"] : []).ToList();
        throw new InvalidOperationException($"No queue client named '{clientName}' has been registered (registered: {(registered.Count == 0 ? "none" : string.Join(", ", registered))}).");
    }

    private sealed class Registration(Action dispose) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) dispose();
        }
    }
}
