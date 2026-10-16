using Azure.Core;

namespace AzureStorage.QueueService;

/// <summary>
/// Records which client names were registered. The settings themselves live in the named-options system
/// (<c>IOptionsMonitor&lt;QueueClientSettings&gt;</c>), keyed by the same names.
/// </summary>
internal sealed class QueueClientSettingsRegistry
{
    private readonly HashSet<string> _clientNames = new(StringComparer.Ordinal);

    public bool HasDefaultClient { get; set; }

    public bool ValidateOnStart { get; set; }

    /// <summary>The credential set by <c>UseTokenCredential</c>, for endpoint clients that have none of their own.</summary>
    public TokenCredential? SharedTokenCredential { get; set; }

    public IReadOnlyCollection<string> ClientNames => _clientNames;

    public void Add(string clientName) => _clientNames.Add(clientName);

    public bool Contains(string clientName) => _clientNames.Contains(clientName);
}
