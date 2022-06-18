using System.Diagnostics.CodeAnalysis;

namespace JasonShave.AzureStorage.QueueService.Models;

[ExcludeFromCodeCoverage]
public class QueueClientSettings
{
    public string ConnectionString { get; set; } = default!;

    public string QueueName { get; set; } = default!;

    public bool CreateIfNotExists { get; set; }
}