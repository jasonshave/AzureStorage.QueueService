using AzureStorage.QueueService.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzureStorage.QueueService;

/// <summary>
/// Reads every registered client's queue and poison queue counts on an interval, for the <c>queue_approximate_messages</c>
/// gauge. Registered by <c>AddQueueDepthMetrics</c>. A count that cannot be read is dropped from the gauge, rather than
/// reported stale, until it can be read again.
/// </summary>
internal sealed class QueueDepthMonitor(
    QueueClientSettingsRegistry registry,
    IQueueClientFactory factory,
    QueueServiceMetrics metrics,
    ILogger<QueueDepthMonitor> logger,
    TimeProvider? time = null) : BackgroundService
{
    private const string Main = "main";
    private const string Poison = "poison";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly HashSet<string> _failing = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(registry.DepthInterval, _time);
        do
        {
            await ReadAllAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task ReadAllAsync(CancellationToken cancellationToken)
    {
        var clients = registry.ClientNames.ToList();
        if (registry.HasDefaultClient) clients.Add(Options.DefaultName);

        foreach (var client in clients)
        {
            IStorageQueue queue;
            try
            {
                queue = client.Length == 0 ? factory.GetQueue() : factory.GetQueue(client);
            }
            catch (InvalidOperationException)
            {
                continue; // a misconfigured client fails where it is used, not here
            }

            await ReadAsync(queue.QueueName, Main, async ct => (await queue.GetPropertiesAsync(ct)).ApproximateMessagesCount, cancellationToken);

            var poison = factory.GetDeadLetterQueue(client);
            await ReadAsync(poison.QueueName, Poison, async ct => await poison.GetApproximateCountAsync(ct), cancellationToken);
        }
    }

    private async Task ReadAsync(string queueName, string kind, Func<CancellationToken, Task<int>> read, CancellationToken cancellationToken)
    {
        try
        {
            metrics.SetDepth(queueName, kind, await read(cancellationToken));
            _failing.Remove(queueName);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            metrics.ForgetDepth(queueName, kind);
            if (_failing.Add(queueName)) logger.LogDepthReadFailed(queueName, e);
        }
    }
}
