using Azure;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AzureStorage.QueueService.HealthChecks;

/// <summary>
/// Checks that a client's queue can be reached by reading its properties, and optionally that its poison queue is
/// empty. Every run makes one or two billable storage calls.
/// </summary>
internal sealed class QueueHealthCheck(IQueueClientFactory factory, string clientName, QueueHealthOptions options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        string? queueName = null;
        try
        {
            var queue = clientName.Length == 0 ? factory.GetQueue() : factory.GetQueue(clientName);
            queueName = queue.QueueName;
            data["queue"] = queueName;

            var properties = await queue.GetPropertiesAsync(cancellationToken);
            data["approximateMessageCount"] = properties.ApproximateMessagesCount;

            if (options.DegradedWhenPoisonMessages)
            {
                var poison = factory.GetDeadLetterQueue(clientName);
                var poisonCount = await poison.GetApproximateCountAsync(cancellationToken);
                data["poisonQueue"] = poison.QueueName;
                data["poisonMessageCount"] = poisonCount;

                if (poisonCount > 0)
                    return HealthCheckResult.Degraded(
                        $"Queue '{queueName}' is reachable, but its poison queue '{poison.QueueName}' holds about {poisonCount} message(s) that failed processing.",
                        data: data);
            }

            return HealthCheckResult.Healthy($"Queue '{queueName}' is reachable.", data);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, Explain(e, queueName), e, data);
        }
    }

    private string Explain(Exception e, string? queueName)
    {
        var queue = queueName is null ? $"The queue for client {Client()}" : $"Queue '{queueName}'";
        return e switch
        {
            RequestFailedException { ErrorCode: var code } when code == QueueErrorCode.QueueNotFound =>
                $"{queue} does not exist.",
            RequestFailedException { Status: 401 or 403 } r =>
                $"{queue} could not be read: the storage account refused access ({r.Status} {r.ErrorCode}). Check the credential and its role assignment.",
            RequestFailedException r =>
                $"{queue} could not be read: the storage service returned {r.Status} {r.ErrorCode}.",
            _ => $"{queue} could not be read: {e.Message}",
        };
    }

    private string Client() => clientName.Length == 0 ? "(the default client)" : $"'{clientName}'";
}
