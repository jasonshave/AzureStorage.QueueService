namespace AzureStorage.QueueService;

/// <summary>
/// Records which client names were registered. The settings themselves live in the named-options system
/// (<c>IOptionsMonitor&lt;QueueClientSettings&gt;</c>), keyed by the same names.
/// </summary>
internal sealed class QueueClientSettingsRegistry
{
    private readonly HashSet<string> _clientNames = new(StringComparer.Ordinal);

    public bool HasDefaultClient { get; set; }

    public IReadOnlyCollection<string> ClientNames => _clientNames;

    public void Add(string clientName) => _clientNames.Add(clientName);

    public bool Contains(string clientName) => _clientNames.Contains(clientName);
}
