using AzureStorage.QueueService.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace AzureStorage.QueueService;

/// <summary>
/// The hosted loop behind <c>AddProcessor&lt;TMessage, THandler&gt;()</c>: <see cref="QueueProcessorOptions.MaxConcurrentMessages"/>
/// workers, each of which receives one message, delivers it, and goes back for the next. One instance per registration.
/// </summary>
/// <remarks>
/// Lifecycle: <see cref="IHostedService.StartAsync"/> snapshots and validates the options and resolves the queue (a
/// misconfigured client fails startup on purpose). The workers start when the host reports
/// <see cref="IHostApplicationLifetime.ApplicationStarted"/> — the signal that every startup callback succeeded — so a
/// slow start does not hold leases and a start that fails part-way never polls at all; without a host lifetime (a
/// direct test) they start in <see cref="IHostedLifecycleService.StartedAsync"/>. On shutdown, receiving stops; running handlers get
/// <see cref="QueueProcessorOptions.ShutdownDrainTimeout"/> to finish; then their token is cancelled and lease renewal
/// stops in the same moment; a handler that ignores its token gets the same time again, after which shutdown stops
/// waiting for it. If the host's own shutdown deadline arrives first, the handlers are cancelled at that moment and
/// <see cref="StopAsync"/> returns rather than throwing, as <c>BackgroundService</c> does. The cancellations happen no
/// matter how <see cref="StopAsync"/> exits. Nothing that happens at runtime stops the host, and a worker that fails
/// unexpectedly is logged loudly and restarted rather than silently lost.
/// </remarks>
internal sealed class QueueProcessor<TMessage, THandler> : IHostedService, IHostedLifecycleService, IDisposable
    where THandler : class, IQueueMessageHandler<TMessage>
{
    private readonly string _clientName;
    private readonly string _optionsName;
    private readonly IQueueClientFactory _factory;
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsFactory<QueueProcessorOptions> _optionsFactory;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly Func<IServiceProvider, THandler>? _createHandler;
    private readonly string _handlerName;

    private readonly CancellationTokenSource _stopReceiving = new();
    private readonly CancellationTokenSource _cancelHandlers = new();
    private readonly CancellationTokenSource _stopRenewals = new();

    // Taken once, up front: reading Token from a disposed source throws, and a worker can still be mid-delivery when
    // a host that gave up waiting disposes the processor.
    private readonly CancellationToken _handlerToken;
    private readonly CancellationToken _renewalToken;
    private readonly object _startLock = new();
    private CancellationTokenRegistration _startWhenApplicationStarted;

    private QueueProcessorOptions _options = new();
    private IStorageQueue? _queue;
    private Task[]? _workers;
    private string _name;

    public QueueProcessor(
        string clientName,
        string optionsName,
        IQueueClientFactory factory,
        IServiceScopeFactory scopes,
        IOptionsFactory<QueueProcessorOptions> optionsFactory,
        ILoggerFactory loggerFactory,
        TimeProvider time,
        IHostApplicationLifetime? lifetime = null,
        Func<IServiceProvider, THandler>? createHandler = null,
        string? handlerName = null)
    {
        // A wrapper handler (the CloudEvent adapter) passes the application's handler name, so logs name the class
        // the application wrote rather than the library's wrapper.
        _handlerName = handlerName ?? typeof(THandler).Name;
        _clientName = clientName;
        _optionsName = optionsName;
        _factory = factory;
        _scopes = scopes;
        _optionsFactory = optionsFactory;
        _logger = loggerFactory.CreateLogger($"AzureStorage.QueueService.QueueProcessor<{typeof(TMessage).Name}, {_handlerName}>");
        _time = time;
        _lifetime = lifetime;
        _createHandler = createHandler;
        _name = _handlerName;
        _handlerToken = _cancelHandlers.Token;
        _renewalToken = _stopRenewals.Token;
    }

    private string QueueName => _queue?.QueueName ?? _clientName;

    // ------------------------------------------------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------------------------------------------------

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // A snapshot: these are the options the processor runs with for its whole life.
        _options = _optionsFactory.Create(_optionsName);
        _options.Validate($"Processor {_name}");

        // Throws for a misconfigured client. That fails host startup, which is the right time to find out.
        _queue = _clientName == Options.DefaultName ? _factory.GetQueue() : _factory.GetQueue(_clientName);
        _name = $"{_handlerName} on queue '{_queue.QueueName}'";

        // Poll only once the whole application has started. If any later startup step fails, this never fires.
        if (_lifetime is not null)
            _startWhenApplicationStarted = _lifetime.ApplicationStarted.Register(StartWorkers);

        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        if (_queue is null) throw new InvalidOperationException("StartAsync must run before StartedAsync.");

        // No host lifetime to wait for: this callback is the best signal there is.
        if (_lifetime is null) StartWorkers();

        return Task.CompletedTask;
    }

    private void StartWorkers()
    {
        lock (_startLock)
        {
            if (_workers is not null || _stopReceiving.IsCancellationRequested) return;

            _logger.LogProcessorStarted(_name, _options.MaxConcurrentMessages);
            _workers = Enumerable.Range(0, _options.MaxConcurrentMessages)
                .Select(_ => Task.Run(() => WorkerAsync(_stopReceiving.Token), CancellationToken.None))
                .ToArray();
        }
    }

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        _stopReceiving.Cancel();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopReceiving.Cancel();
        if (_workers is null) return;

        try
        {
            var all = Task.WhenAll(_workers);
            if (all.IsCompleted) return;

            _logger.LogProcessorDraining(_name, _options.ShutdownDrainTimeout, Running());
            if (await FinishedWithinAsync(all, _options.ShutdownDrainTimeout, cancellationToken)) return;

            // The drain deadline: cancel the handlers and, in the same moment, stop scheduling renewals, so a handler
            // that ignores its token cannot keep its lease alive for ever. A renewal already in flight still completes.
            _logger.LogProcessorDrainTimedOut(_name);
            _cancelHandlers.Cancel();
            _stopRenewals.Cancel();
            if (await FinishedWithinAsync(all, _options.ShutdownDrainTimeout, cancellationToken)) return;

            // Bounded: a handler that ignores cancellation is application code the processor cannot terminate.
            // When the host's deadline cut the wait short, there was no second wait to report on.
            if (!cancellationToken.IsCancellationRequested)
                _logger.LogProcessorHandlersIgnoredCancellation(_name, Running(), _options.ShutdownDrainTimeout);
        }
        finally
        {
            // Unconditional: however this method exits — drained, cancelled by the host's deadline, or faulted —
            // nothing the processor owns keeps running afterwards.
            _cancelHandlers.Cancel();
            _stopRenewals.Cancel();
            _logger.LogProcessorStopped(_name);
        }
    }

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        // A host that failed part-way through startup disposes its services without ever calling StopAsync.
        // Cancel first: a disposed, uncancelled source tells nobody anything, and workers may already be polling.
        _startWhenApplicationStarted.Dispose();
        _stopReceiving.Cancel();
        _cancelHandlers.Cancel();
        _stopRenewals.Cancel();

        _stopReceiving.Dispose();
        _cancelHandlers.Dispose();
        _stopRenewals.Dispose();
    }

    private int Running() => _workers?.Count(w => !w.IsCompleted) ?? 0;

    private async Task<bool> FinishedWithinAsync(Task task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await task.WaitAsync(timeout, _time, cancellationToken);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host's shutdown deadline: stop waiting, but StopAsync still cancels everything on the way out.
            return false;
        }
    }

    // ------------------------------------------------------------------------------------------------------------
    // A worker: receive one, deliver it, repeat
    // ------------------------------------------------------------------------------------------------------------

    private async Task WorkerAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await ReceiveAndDeliverUntilStoppedAsync(stop);
                return;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Nothing in the loop is supposed to throw. If something does, say so loudly and start again
                // rather than silently running one worker short for the rest of the process's life.
                _logger.LogProcessorWorkerFailed(_name, _options.ErrorBackoffMax, ex);
                if (!await DelayAsync(_options.ErrorBackoffMax, stop)) return;
            }
        }
    }

    private async Task ReceiveAndDeliverUntilStoppedAsync(CancellationToken stop)
    {
        var queue = _queue!;
        var owned = queue as IOwnedReceiver;
        var emptyBackoff = _options.EmptyQueueBackoffMin;
        var errorBackoff = _options.EmptyQueueBackoffMin;

        while (!stop.IsCancellationRequested)
        {
            QueueMessage<TMessage>? message;
            try
            {
                // The library's own queue hands the message over before deserializing it, so renewal is armed first.
                // Any other IStorageQueue (a fake in a test) deserializes on receive.
                message = owned is not null
                    ? await owned.ReceiveUndeserializedAsync<TMessage>(_options.VisibilityTimeout, stop)
                    : (await queue.ReceiveAsync<TMessage>(1, _options.VisibilityTimeout, stop)).FirstOrDefault();
                errorBackoff = _options.EmptyQueueBackoffMin;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Transport failures, a missing queue, no permission: log, back off, keep going.
                _logger.LogProcessorReceiveFailed(_name, errorBackoff, ex);
                if (!await DelayAsync(errorBackoff, stop)) return;
                errorBackoff = Min(errorBackoff * 2, _options.ErrorBackoffMax);
                continue;
            }

            if (message is null)
            {
                if (!await DelayAsync(emptyBackoff, stop)) return;
                emptyBackoff = Min(emptyBackoff * 2, _options.EmptyQueueBackoffMax);
                continue;
            }

            emptyBackoff = _options.EmptyQueueBackoffMin;
            await DeliverAsync(message, owned);
        }
    }

    private async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stop)
    {
        try
        {
            await Task.Delay(delay, _time, stop);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    // ------------------------------------------------------------------------------------------------------------
    // One delivery: the complete lifecycle of one message, with one exception boundary. Never throws.
    // ------------------------------------------------------------------------------------------------------------

    private async Task DeliverAsync(QueueMessage<TMessage> message, IOwnedReceiver? deserializer)
    {
        var start = Stopwatch.GetTimestamp();
        var outcome = "abandoned";

        var tags = QueueTags();
        tags.Add(QueueServiceDiagnostics.Names.MessageId, message.MessageId);
        tags.Add(QueueServiceDiagnostics.Names.DequeueCount, message.DequeueCount);
        using Activity? activity = QueueServiceDiagnostics.Activities.StartProcessingMessageActivity(tags);

        // Armed before anything else — including deserialization — so nothing that happens to this message
        // while it is ours can let the lease lapse.
        await using var renewal = _options.RenewVisibility
            ? LeaseRenewal.Start(
                () => message.RenewLockAsync(_options.VisibilityTimeout, CancellationToken.None),
                () => message.IsSettled,
                TimeSpan.FromTicks(_options.VisibilityTimeout.Ticks / 2),
                _time,
                _renewalToken,
                ex => _logger.LogProcessorRenewFailed(_name, message.MessageId, ex))
            : null;

        try
        {
            deserializer?.Deserialize(message);

            if (message.Error is not null && _options.DeadLetterOnDeserializationFailure)
            {
                await StopRenewalAsync(renewal);
                outcome = await DeadLetterOrAbandonAsync(message, $"the body could not be read: {message.Error.Message}");
                return;
            }

            if (message.DequeueCount > _options.MaxDequeueCount)
            {
                await StopRenewalAsync(renewal);
                outcome = await DeadLetterOrAbandonAsync(message, $"delivered {message.DequeueCount} times; the limit is {_options.MaxDequeueCount}");
                return;
            }

            try
            {
                // Resolving the handler is inside the boundary: a constructor that throws is a failed delivery too.
                await using var scope = _scopes.CreateAsyncScope();
                var handler = _createHandler is null ? scope.ServiceProvider.GetRequiredService<THandler>() : _createHandler(scope.ServiceProvider);
                await handler.HandleAsync(message, _handlerToken);
            }
            catch (OperationCanceledException) when (_handlerToken.IsCancellationRequested && !message.IsSettled)
            {
                await StopRenewalAsync(renewal);
                _logger.LogProcessorHandlerCancelled(_name, message.MessageId);
                if (!message.IsSettled) await AbandonSafelyAsync(message, TimeSpan.Zero);
                return;
            }
            catch (Exception ex) when (!message.IsSettled)
            {
                await StopRenewalAsync(renewal);
                _logger.LogHandlerFailed(QueueName, message.MessageId, message.DequeueCount, ex);
                activity?.AddException(ex);
                QueueServiceDiagnostics.Metrics.MessageFailures.Add(1, QueueTags());
                if (!message.IsSettled) await AbandonSafelyAsync(message, RetryDelayFor(message.DequeueCount));
                return;
            }
            catch (Exception ex)
            {
                // The handler settled the message and then threw. The settlement stands; report it as what it was.
                _logger.LogHandlerFailedAfterSettling(QueueName, message.MessageId, message.Settlement, ex);
                activity?.AddException(ex);
            }

            // Wait for any renewal already in flight so completion uses the receipt the service holds.
            await StopRenewalAsync(renewal);

            switch (message.Settlement)
            {
                case MessageSettlement.Completed:
                    outcome = "completed";
                    break;
                case MessageSettlement.Abandoned:
                    outcome = "abandoned_by_handler";
                    return;
                case MessageSettlement.DeadLettered:
                    outcome = "dead_lettered_by_handler";
                    return;
                case MessageSettlement.None when _options.AutoComplete:
                    try
                    {
                        // Not tied to the shutdown token: cancelling a delete after the handler succeeded only redelivers finished work.
                        await message.CompleteAsync(CancellationToken.None);
                        outcome = "completed";
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDeleteFailed(QueueName, message.MessageId, _options.VisibilityTimeout, ex);
                        activity?.AddException(ex);
                        QueueServiceDiagnostics.Metrics.MessageFailures.Add(1, QueueTags());
                        outcome = "complete_failed";
                        return;
                    }
                    break;
                default:
                    // AutoComplete is off and the handler returned without settling: nothing was removed.
                    outcome = "left_to_handler";
                    return;
            }

            _logger.LogMessageProcessed(QueueName, message.MessageId);
            QueueServiceDiagnostics.Metrics.MessagesProcessed.Add(1, QueueTags());
        }
        catch (Exception ex)
        {
            // Anything the inner boundaries did not cover: scope disposal, an option callback, a logging sink.
            // A delivery must never fault silently and leave its message in limbo.
            _logger.LogProcessorDeliveryFailed(_name, message.MessageId, ex);
            activity?.AddException(ex);
            QueueServiceDiagnostics.Metrics.MessageFailures.Add(1, QueueTags());
            if (!message.IsSettled) await AbandonSafelyAsync(message, QueueProcessorOptions.MaxRetryDelay);
            outcome = "abandoned";
        }
        finally
        {
            await StopRenewalAsync(renewal);

            var finalTags = QueueTags();
            finalTags.Add(QueueServiceDiagnostics.Names.QueueProcessSuccess, outcome is "completed" or "left_to_handler");
            finalTags.Add(QueueServiceDiagnostics.Names.Outcome, outcome);
            QueueServiceDiagnostics.Metrics.MessageProcessingDuration.Record(Stopwatch.GetElapsedTime(start).TotalSeconds, finalTags);
            activity?.SetTag(QueueServiceDiagnostics.Names.Outcome, outcome);
        }
    }

    private static ValueTask StopRenewalAsync(LeaseRenewal? renewal) => renewal?.StopAsync() ?? ValueTask.CompletedTask;

    private TimeSpan RetryDelayFor(long dequeueCount)
    {
        TimeSpan delay;
        try
        {
            delay = _options.RetryDelay(dequeueCount);
        }
        catch (Exception ex)
        {
            _logger.LogProcessorRetryDelayFailed(_name, dequeueCount, QueueProcessorOptions.MaxRetryDelay, ex);
            return QueueProcessorOptions.MaxRetryDelay;
        }

        // The service accepts 0 to 7 days; anything else would make the abandon itself fail.
        return delay < TimeSpan.Zero ? TimeSpan.Zero : Min(delay, QueueProcessorOptions.MaxVisibility);
    }

    private async Task<string> DeadLetterOrAbandonAsync(QueueMessage<TMessage> message, string reason)
    {
        try
        {
            await message.DeadLetterAsync(reason, CancellationToken.None);
            QueueServiceDiagnostics.Metrics.MessageFailures.Add(1, QueueTags());
            return "dead_lettered";
        }
        catch (Exception ex)
        {
            // No permission to create the poison queue, or the lease lapsed. Never lose the message silently.
            _logger.LogProcessorDeadLetterFailed(_name, message.MessageId, ex);
            if (!message.IsSettled) await AbandonSafelyAsync(message, RetryDelayFor(message.DequeueCount));
            return "abandoned";
        }
    }

    private async ValueTask AbandonSafelyAsync(QueueMessage<TMessage> message, TimeSpan delay)
    {
        try
        {
            await message.AbandonAsync(delay, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogProcessorAbandonFailed(_name, message.MessageId, ex);
        }
    }

    private TagList QueueTags() => new() { new(QueueServiceDiagnostics.Names.QueueName, QueueName) };
}
