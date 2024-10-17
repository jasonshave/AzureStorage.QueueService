using System.Diagnostics.CodeAnalysis;

namespace AzureStorage.QueueService;

[ExcludeFromCodeCoverage]
public class QueueClientSettings
{
    public const string SectionName = "AzureStorageQueueClientSettings";

    public string ConnectionString { get; set; } = default!;

    public string QueueName { get; set; } = default!;

    public bool CreateIfNotExists { get; set; }
}