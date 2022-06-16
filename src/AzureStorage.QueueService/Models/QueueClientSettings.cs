using System.Diagnostics.CodeAnalysis;

namespace JasonShave.AzureStorage.QueueService.Models;

[ExcludeFromCodeCoverage]
public class QueueClientSettings
{
    public string ConnectionString { get; set; }

    public string QueueName { get; set; }
}