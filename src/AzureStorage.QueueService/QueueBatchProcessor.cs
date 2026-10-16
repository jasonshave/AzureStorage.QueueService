using AzureStorage.QueueService.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace AzureStorage.QueueService;

/// <summary>
/// The hosted processor behind <c>AddBatchProcessor&lt;TMessage, THandler&gt;()</c>. The receive loop, start, stop,
/// pausing and status are <see cref="QueueProcessorBase{TMessage}"/>'s, with a batch where the single-message
/// processor has a message: each receive fills one or more free batch slots with up to
/// <see cref="QueueBatchProcessorOptions.MaxBatchSize"/> messages each, and each batch is delivered on its own task.
/// </summary>
/// <remarks>
/// Each message keeps its own lease renewal, poison checks, error policy and settlement; the batch shares one scope,
/// one handler call, one span (linked to every sender's) and one log scope. Middleware is per message, so it does not
/// run here.
/// </remarks>
internal sealed class QueueBatchProcessor<TMessage, THandler> : QueueProcessorBase<TMessage>
    where THandler : class, IQueueBatchHandler<TMessage>
{
    private readonly string _optionsName;
    private readonly IOptionsFactory<QueueBatchProcessorOptions> _optionsFactory;
    private QueueBatchProcessorOptions _typedOptions = new();

    public QueueBatchProcessor(
        string clientName,
        string optionsName,
        IQueueClientFactory factory,
        IServiceScopeFactory scopes,
        IOptionsFactory<QueueBatchProcessorOptions> optionsFactory,
        ILoggerFactory loggerFactory,
        QueueServiceMetrics metrics,
        TimeProvider time,
        IHostApplicationLifetime? lifetime = null,
        QueueProcessorController? controller = null)
        : base("QueueBatchProcessor", clientName, typeof(THandler).Name, factory, scopes, loggerFactory, metrics, time, lifetime, controller, readBody: null, afterDeserialize: null)
    {
        _optionsName = optionsName;
        _optionsFactory = optionsFactory;
    }

    private protected override QueueProcessorOptionsBase CreateOptions() => _typedOptions = _optionsFactory.Create(_optionsName);

    private protected override int MessagesPerDelivery => _typedOptions.MaxBatchSize;

    private protected override int MaxConcurrentDeliveries => _typedOptions.MaxConcurrentBatches;

    // ------------------------------------------------------------------------------------------------------------
    // One batch: every message's lifecycle, with one handler call and one exception boundary. Never throws.
    // ------------------------------------------------------------------------------------------------------------

    private protected override async Task DeliverAsync(IReadOnlyList<QueueMessage<TMessage>> received, IOwnedReceiver? owned, long started)
    {
        var count = received.Count;
        var outcomes = Enumerable.Repeat("abandoned", count).ToArray();
        var renewals = new LeaseRenewal?[count];
        Exception? failure = null;

        using var logScope = _logger.BeginBatchDeliveryScope(QueueName, count, string.Join(", ", received.Select(m => m.MessageId)));

        var tags = QueueServiceDiagnostics.OperationTags(QueueName, QueueServiceDiagnostics.Messaging.Process);
        tags.Add(QueueServiceDiagnostics.Messaging.BatchMessageCount, count);
        using Activity? activity = QueueServiceDiagnostics.Activities.StartProcessingBatchActivity(tags, received.Select(m => m.CreationContext));

        _metrics.ActiveHandlers.Add(count, QueueTags());

        using var longRunning = _options.LongRunningHandlerWarning is { } threshold
            ? _time.CreateTimer(_ => _logger.LogProcessorBatchHandlerLongRunning(_name, count, _time.GetElapsedTime(started), threshold, RenewalNote()), null, threshold, Timeout.InfiniteTimeSpan)
            : null;

        try
        {
            // Every lease is armed before anything else, downloading and deserializing included, so nothing that happens
            // to one message can let another's lease lapse.
            for (var i = 0; i < count; i++)
            {
                var message = received[i];
                message.Lease.IsLastAttempt |= message.DequeueCount >= _options.MaxDequeueCount;
                message.Lease.DeliveryActivity = activity;
                RecordTimeInQueue(message);
                renewals[i] = StartRenewal(message);
            }

            // Read every body; the unreadable and the over-delivered are dead-lettered on their own and left out.
            var batch = new List<QueueMessage<TMessage>>(count);
            var positions = new List<int>(count);
            for (var i = 0; i < count; i++)
            {
                var message = received[i];
                if (!await ReadBodyAsync(message, owned))
                {
                    // Cancelled while downloading a body at shutdown: give back this message, the ones still to be read
                    // and the ones already read.
                    await GiveBackAsync(received, renewals, outcomes, positions.Concat(Enumerable.Range(i, count - i)));
                    return;
                }

                if (await RejectIfUnprocessableAsync(message, renewals[i]) is { } rejected)
                {
                    outcomes[i] = rejected;
                    continue;
                }

                batch.Add(message);
                positions.Add(i);
            }

            if (batch.Count == 0) return;
            activity?.SetTag(QueueServiceDiagnostics.Messaging.BatchMessageCount, batch.Count);

            try
            {
                // Resolving the handler is inside the boundary: a constructor that throws is a failed batch too.
                await using var scope = _scopes.CreateAsyncScope();
                var handler = scope.ServiceProvider.GetRequiredService<THandler>();
                await handler.HandleAsync(batch, _handlerToken);
            }
            catch (OperationCanceledException) when (_handlerToken.IsCancellationRequested && batch.Any(m => !m.IsSettled))
            {
                await GiveBackAsync(received, renewals, outcomes, positions);
                return;
            }
            catch (Exception ex) when (batch.Any(m => !m.IsSettled))
            {
                failure = ex;
                activity.RecordFailure(ex);
                foreach (var i in positions)
                {
                    var message = received[i];
                    await StopRenewalAsync(renewals[i]);
                    if (message.IsSettled)
                    {
                        (outcomes[i], _) = await SettleAfterReturnAsync(message, activity);
                        continue;
                    }

                    _metrics.MessageFailures.Add(1, QueueTags());
                    outcomes[i] = await HandleFailureAsync(message, ex);
                }

                return;
            }
            catch (Exception ex)
            {
                // The handler settled every message and then threw. The settlements stand; report it as what it was:
                // an exception on a span that did not fail, so no error status.
                _logger.LogBatchHandlerFailedAfterSettling(QueueName, batch.Count, ex);
                activity?.AddException(ex);
            }

            foreach (var i in positions)
            {
                // Wait for any renewal already in flight so completion uses the receipt the service holds.
                await StopRenewalAsync(renewals[i]);
                (outcomes[i], var settleFailure) = await SettleAfterReturnAsync(received[i], activity);
                failure ??= settleFailure;
            }
        }
        catch (Exception ex)
        {
            // Anything the inner boundaries did not cover: scope disposal, an option callback, a logging sink.
            // A delivery must never fault silently and leave its messages in limbo.
            failure = ex;
            activity.RecordFailure(ex);
            for (var i = 0; i < count; i++)
            {
                var message = received[i];
                if (message.IsSettled) continue;

                _logger.LogProcessorDeliveryFailed(_name, message.MessageId, ex);
                _metrics.MessageFailures.Add(1, QueueTags());
                await AbandonSafelyAsync(message, QueueProcessorOptionsBase.MaxRetryDelay);
                outcomes[i] = "abandoned";
            }
        }
        finally
        {
            foreach (var renewal in renewals) await StopRenewalAsync(renewal);
            _metrics.ActiveHandlers.Add(-count, QueueTags());

            var elapsed = _time.GetElapsedTime(started).TotalSeconds;
            foreach (var outcome in outcomes) RecordOutcome(outcome, elapsed);
            _metrics.ProcessDuration.Record(elapsed, QueueServiceDiagnostics.MessagingTags(QueueName, QueueServiceDiagnostics.Messaging.Process, QueueServiceDiagnostics.Messaging.Process, failure));
            activity?.SetTag(QueueServiceDiagnostics.Names.Outcome, outcomes.Distinct().Count() == 1 ? outcomes[0] : "mixed");
            foreach (var message in received) message.Lease.DeliveryActivity = null;
        }
    }

    /// <summary>At shutdown: the messages at <paramref name="positions"/> the handler had not settled go straight back.</summary>
    private async Task GiveBackAsync(IReadOnlyList<QueueMessage<TMessage>> received, LeaseRenewal?[] renewals, string[] outcomes, IEnumerable<int> positions)
    {
        foreach (var i in positions)
        {
            var message = received[i];
            await StopRenewalAsync(renewals[i]);
            if (message.IsSettled)
            {
                // The handler settled it before it saw the cancellation.
                (outcomes[i], _) = await SettleAfterReturnAsync(message, null);
                continue;
            }

            _logger.LogProcessorHandlerCancelled(_name, message.MessageId);
            await AbandonSafelyAsync(message, TimeSpan.Zero);
        }
    }
}
