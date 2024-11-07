using System.Diagnostics.CodeAnalysis;
using Azure.Core;

namespace AzureStorage.QueueService;

[ExcludeFromCodeCoverage]
public class QueueClientSettings
{
    public const string SectionName = "AzureStorageQueueClientSettings";

    public string? ConnectionString { get; set; }

    public string QueueName { get; set; } = string.Empty;

    public bool CreateIfNotExists { get; set; }
    
    public Uri? EndpointUri { get; set; }

    public TokenCredential? TokenCredential { get; set; }
}