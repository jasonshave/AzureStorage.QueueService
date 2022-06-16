using System.Diagnostics.CodeAnalysis;

namespace JasonShave.AzureStorage.QueueService.Models
{
    [ExcludeFromCodeCoverage]
    public class AzureStorageQueueSettings
    {
        public IEnumerable<QueueClientSettings> QueueClients { get; } = new List<QueueClientSettings>();
    }
}