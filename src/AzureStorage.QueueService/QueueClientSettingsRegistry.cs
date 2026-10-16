using Azure.Core;

namespace AzureStorage.QueueService;

/// <summary>
/// Records which client names were registered. The settings themselves live in the named-options system
/// (<c>IOptionsMonitor&lt;QueueClientSettings&gt;</c>), keyed by the same names.
/// </summary>
internal sealed class QueueClientSettingsRegistry
{
    private readonly HashSet<string> _clientNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _processors = new(StringComparer.Ordinal);
    private int _processorNumber;

    public bool HasDefaultClient { get; set; }

    public bool ValidateOnStart { get; set; }

    /// <summary>The credential set by <c>UseTokenCredential</c>, for endpoint clients that have none of their own.</summary>
    public TokenCredential? SharedTokenCredential { get; set; }

    /// <summary>The account set by <c>UseStorageAccount</c>: a connection string, for clients with no connection of their own.</summary>
    public string? SharedConnectionString { get; set; }

    /// <summary>The account set by <c>UseStorageAccount</c>: the queue service endpoint, for clients with no connection of their own.</summary>
    public Uri? SharedServiceUri { get; set; }

    /// <summary>How often the depth monitor reads queue counts; set by <c>AddQueueDepthMetrics</c>.</summary>
    public TimeSpan DepthInterval { get; set; } = TimeSpan.FromMinutes(1);

    public IReadOnlyCollection<string> ClientNames => _clientNames;

    public void Add(string clientName) => _clientNames.Add(clientName);

    public bool Contains(string clientName) => _clientNames.Contains(clientName);

    /// <summary>False when a processor with these options was already registered: the second registration only adds to its options.</summary>
    public bool AddProcessor(string optionsName) => _processors.Add(optionsName);

    public int NextProcessorNumber() => Interlocked.Increment(ref _processorNumber);
}
