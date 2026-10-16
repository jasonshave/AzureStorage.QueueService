using Azure;
using Azure.Storage.Queues.Models;
using AzureStorage.QueueService.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.RateLimiting;

namespace AzureStorage.QueueService;

/// <summary>
/// What a registration adds to the plain processor: how to build the handler, what to call it in logs, and how to read
/// a body when the client's serializer alone is not the whole story (CloudEvents, routing).
/// </summary>
/// <param name="CreateHandler">Builds the handler from the message's scope; null resolves <c>THandler</c> from it.</param>
/// <param name="HandlerName">The name logs use; the application's handler rather than a library wrapper.</param>
/// <param name="ReadBody">Replaces deserialization with the client's serializer. Sets <see cref="QueueMessage{T}.Body"/> or <see cref="QueueMessage{T}.Error"/>.</param>
/// <param name="AfterDeserialize">Runs after the body deserialized without error; may set <see cref="QueueMessage{T}.Error"/>.</param>
/// <param name="Middleware">Runs around the handler, outermost first.</param>
internal sealed record ProcessorHooks<TMessage, THandler>(
    Func<IServiceProvider, THandler>? CreateHandler = null,
    string? HandlerName = null,
    Action<QueueMessage<TMessage>>? ReadBody = null,
    Action<QueueMessage<TMessage>>? AfterDeserialize = null,
    IReadOnlyList<Func<IServiceProvider, IQueueMessageMiddleware>>? Middleware = null);

/// <summary>
/// The hosted processor behind <c>AddProcessor&lt;TMessage, THandler&gt;()</c>: one receive loop that takes as many
/// messages as there are free handlers (up to <see cref="QueueProcessorOptions.MaxConcurrentMessages"/> at once, 32 per
/// request) and delivers each on its own task. One instance per registration.
/// </summary>
/// <remarks>
/// <para>
/// No message waits behind another's handler, and the processor never holds a message no handler is free for: a receive
/// asks only for as many messages as there are free slots, and each message's lease renewal starts as its delivery does.
/// A full batch means more may be waiting, so the loop asks again at once; an empty one backs off.
/// </para>
/// <para>
/// Lifecycle: <see cref="IHostedService.StartAsync"/> snapshots and validates the options and resolves the queue (a
/// misconfigured client fails startup on purpose). Receiving starts when the host reports
/// <see cref="IHostApplicationLifetime.ApplicationStarted"/> — the signal that every startup callback succeeded — so a
/// slow start does not hold leases and a start that fails part-way never polls at all; without a host lifetime (a
/// direct test) it starts in <see cref="IHostedLifecycleService.StartedAsync"/>. On shutdown, receiving stops; running handlers get
/// <see cref="QueueProcessorOptions.ShutdownDrainTimeout"/> to finish; then their token is cancelled and lease renewal
/// stops in the same moment; a handler that ignores its token gets the same time again, after which shutdown stops
/// waiting for it. If the host's own shutdown deadline arrives first, the handlers are cancelled at that moment and
/// <see cref="StopAsync"/> returns rather than throwing, as <c>BackgroundService</c> does. The cancellations happen no
/// matter how <see cref="StopAsync"/> exits. Nothing that happens at runtime stops the host, and a receive loop that fails
/// unexpectedly is logged loudly and restarted rather than silently lost.
/// </para>
/// </remarks>
internal sealed class QueueProcessor<TMessage, THandler> : IHostedService, IHostedLifecycleService, IControlledProcessor, IDisposable
    where THandler : class, IQueueMessageHandler<TMessage>
{
    private readonly string _clientName;
    private readonly string _optionsName;
    private readonly IQueueClientFactory _factory;
    private readonly IServiceScopeFactory _scopes;
    private readonly IOptionsFactory<QueueProcessorOptions> _optionsFactory;
    private readonly ILogger _logger;
    private readonly QueueServiceMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly IHostApplicationLifetime? _lifetime;
    private readonly QueueProcessorController? _controller;
    private readonly ProcessorHooks<TMessage, THandler> _hooks;
    private readonly string _handlerName;

    private readonly CancellationTokenSource _stopReceiving = new();
    private readonly CancellationTokenSource _cancelHandlers = new();
    private readonly CancellationTokenSource _stopRenewals = new();

    // Taken once, up front: reading Token from a disposed source throws, and a delivery can still be running when a
    // host that gave up waiting disposes the processor.
    private readonly CancellationToken _handlerToken;
    private readonly CancellationToken _renewalToken;
    private readonly object _startLock = new();
    private CancellationTokenRegistration _startWhenApplicationStarted;

    // Deliveries in flight, by a sequence number, with the timestamp each started at: for draining, and for the
    // oldest-handler gauge and status.
    private readonly ConcurrentDictionary<long, (Task Task, long Started)> _deliveries = new();
    private long _deliverySequence;

    private QueueProcessorOptions _options = new();
    private SemaphoreSlim? _slots;
    private IStorageQueue? _queue;
    private Task? _receiver;
    private string _name;
    private IDisposable? _registration;
    private IDisposable? _ageGauge;
    private volatile QueueProcessorState _state = QueueProcessorState.NotStarted;
    private bool _pausedLogged;

    // Receive health, for status. Written by the receive loop only.
    private DateTimeOffset? _lastReceiveSucceededAt;
    private DateTimeOffset? _lastReceiveFailedAt;
    private int _consecutiveReceiveFailures;
    private string? _lastReceiveError;

    public QueueProcessor(
        string clientName,
        string optionsName,
        IQueueClientFactory factory,
        IServiceScopeFactory scopes,
        IOptionsFactory<QueueProcessorOptions> optionsFactory,
        ILoggerFactory loggerFactory,
        QueueServiceMetrics metrics,
        TimeProvider time,
        IHostApplicationLifetime? lifetime = null,
        ProcessorHooks<TMessage, THandler>? hooks = null,
        QueueProcessorController? controller = null)
    {
        _hooks = hooks ?? new ProcessorHooks<TMessage, THandler>();
        // A wrapper handler (the CloudEvent adapter, a router, a lambda) passes the application's handler name, so logs
        // name what the application wrote rather than the library's wrapper.
        _handlerName = _hooks.HandlerName ?? typeof(THandler).Name;
        _clientName = clientName;
        _optionsName = optionsName;
        _factory = factory;
        _scopes = scopes;
        _optionsFactory = optionsFactory;
        _logger = loggerFactory.CreateLogger($"AzureStorage.QueueService.QueueProcessor<{typeof(TMessage).Name}, {_handlerName}>");
        _metrics = metrics;
        _time = time;
        _lifetime = lifetime;
        _controller = controller;
        _name = _handlerName;
        _handlerToken = _cancelHandlers.Token;
        _renewalToken = _stopRenewals.Token;
    }

    private string QueueName => _queue?.QueueName ?? _clientName;

    public string ClientName => _clientName;

    // ------------------------------------------------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------------------------------------------------

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // A snapshot: these are the options the processor runs with for its whole life.
        _options = _optionsFactory.Create(_optionsName);
        _options.Validate($"Processor {_name}");
        _slots = new SemaphoreSlim(_options.MaxConcurrentMessages, _options.MaxConcurrentMessages);

        // Throws for a misconfigured client. That fails host startup, which is the right time to find out.
        _queue = _clientName == Options.DefaultName ? _factory.GetQueue() : _factory.GetQueue(_clientName);
        _name = $"{_handlerName} on queue '{_queue.QueueName}'";

        _registration = _controller?.Register(this);
        _ageGauge = _metrics.ObserveHandlerAge(ObserveOldestHandler);

        // Poll only once the whole application has started. If any later startup step fails, this never fires.
        if (_lifetime is not null)
            _startWhenApplicationStarted = _lifetime.ApplicationStarted.Register(StartReceiving);

        return Task.CompletedTask;
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        if (_queue is null) throw new InvalidOperationException("StartAsync must run before StartedAsync.");

        // No host lifetime to wait for: this callback is the best signal there is.
        if (_lifetime is null) StartReceiving();

        return Task.CompletedTask;
    }

    private void StartReceiving()
    {
        lock (_startLock)
        {
            if (_receiver is not null || _stopReceiving.IsCancellationRequested) return;

            _logger.LogProcessorStarted(_name, _options.MaxConcurrentMessages);
            _state = QueueProcessorState.Running;
            _receiver = Task.Run(() => ReceiverAsync(_stopReceiving.Token), CancellationToken.None);
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
        if (_receiver is null)
        {
            _state = QueueProcessorState.Stopped;
            return;
        }

        _state = QueueProcessorState.Stopping;
        try
        {
            var all = AllWorkAsync();
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
            _state = QueueProcessorState.Stopped;
            _logger.LogProcessorStopped(_name);
        }
    }

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
        // A host that failed part-way through startup disposes its services without ever calling StopAsync.
        // Cancel first: a disposed, uncancelled source tells nobody anything, and the loop may already be polling.
        _startWhenApplicationStarted.Dispose();
        _stopReceiving.Cancel();
        _cancelHandlers.Cancel();
        _stopRenewals.Cancel();
        _registration?.Dispose();
        _ageGauge?.Dispose();

        _stopReceiving.Dispose();
        _cancelHandlers.Dispose();
        _stopRenewals.Dispose();
    }

    // The receive loop first (it stops adding deliveries once it has exited), then every delivery it started.
    private async Task AllWorkAsync()
    {
        if (_receiver is { } receiver) await receiver;
        await Task.WhenAll(_deliveries.Values.Select(d => d.Task));
    }

    private int Running() => _deliveries.Count;

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

    public QueueProcessorStatus GetStatus()
    {
        var state = _state == QueueProcessorState.Running && _controller?.IsPaused(_clientName) == true ? QueueProcessorState.Paused : _state;
        TimeSpan? oldest = _deliveries.IsEmpty ? null : _time.GetElapsedTime(_deliveries.Values.Min(d => d.Started));
        return new QueueProcessorStatus(
            _clientName, QueueName, _handlerName, typeof(TMessage), state, _deliveries.Count, _options.MaxConcurrentMessages,
            _lastReceiveSucceededAt, _lastReceiveFailedAt, _consecutiveReceiveFailures, _lastReceiveError, oldest);
    }

    private IEnumerable<Measurement<double>> ObserveOldestHandler()
    {
        // 0 when idle, so the series is there to alert on before anything hangs.
        var oldest = _deliveries.IsEmpty ? 0 : _time.GetElapsedTime(_deliveries.Values.Min(d => d.Started)).TotalSeconds;
        yield return new Measurement<double>(oldest, new KeyValuePair<string, object?>(QueueServiceDiagnostics.Names.QueueName, QueueName));
    }

    // ------------------------------------------------------------------------------------------------------------
    // The receive loop: take as many as there are free handlers, hand each to its own delivery, repeat
    // ------------------------------------------------------------------------------------------------------------

    private async Task ReceiverAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await ReceiveUntilStoppedAsync(stop);
                return;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Nothing in the loop is supposed to throw. If something does, say so loudly and start again rather
                // than silently stop processing for the rest of the process's life.
                _logger.LogProcessorWorkerFailed(_name, _options.ErrorBackoffMax, ex);
                if (!await DelayAsync(_options.ErrorBackoffMax, stop)) return;
            }
        }
    }

    private async Task ReceiveUntilStoppedAsync(CancellationToken stop)
    {
        var queue = _queue!;
        var slots = _slots!;
        var owned = queue as IOwnedReceiver;
        var emptyBackoff = _options.EmptyQueueBackoffMin;
        var errorBackoff = _options.EmptyQueueBackoffMin;

        while (!stop.IsCancellationRequested)
        {
            await WaitWhilePausedAsync(stop);

            // At least one free handler, then every other one that is free right now.
            await slots.WaitAsync(stop);
            var take = 1;
            while (take < StorageQueue.ServiceMaxMessages && slots.Wait(0)) take++;

            IReadOnlyList<QueueMessage<TMessage>> messages;
            try
            {
                if (_options.RateLimiter is { } limiter)
                {
                    var permitted = await AcquirePermitsAsync(limiter, take, stop);
                    slots.Release(take - permitted);
                    take = permitted;
                }

                // The library's own queue hands messages over before reading their bodies, so renewal is armed first.
                // Any other IStorageQueue (a fake in a test) deserializes on receive.
                messages = owned is not null
                    ? await owned.ReceiveForProcessingAsync<TMessage>(take, _options.VisibilityTimeout, stop)
                    : await queue.ReceiveAsync<TMessage>(take, _options.VisibilityTimeout, stop);

                _lastReceiveSucceededAt = _time.GetUtcNow();
                _consecutiveReceiveFailures = 0;
                _lastReceiveError = null;
                errorBackoff = _options.EmptyQueueBackoffMin;
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                slots.Release(take);
                return;
            }
            catch (Exception ex)
            {
                slots.Release(take);
                _lastReceiveFailedAt = _time.GetUtcNow();
                _consecutiveReceiveFailures++;
                _lastReceiveError = ex.Message;

                // Transport failures, a missing queue, no permission: log once, back off, keep going.
                if (ex is RequestFailedException { ErrorCode: var code } && code == QueueErrorCode.QueueNotFound)
                    _logger.LogProcessorQueueNotFound(_name, QueueName, errorBackoff);
                else
                    _logger.LogProcessorReceiveFailed(_name, errorBackoff, ex);

                if (!await DelayAsync(errorBackoff, stop)) return;
                errorBackoff = Min(errorBackoff * 2, _options.ErrorBackoffMax);
                continue;
            }

            foreach (var message in messages) StartDelivery(message, owned);
            if (messages.Count < take) slots.Release(take - messages.Count);

            if (messages.Count == 0)
            {
                if (!await DelayAsync(emptyBackoff, stop)) return;
                emptyBackoff = Min(emptyBackoff * 2, _options.EmptyQueueBackoffMax);
                continue;
            }

            // Something arrived, so more may be waiting: ask again as soon as a handler is free.
            emptyBackoff = _options.EmptyQueueBackoffMin;
        }
    }

    private async Task WaitWhilePausedAsync(CancellationToken stop)
    {
        if (_controller is null || !_controller.IsPaused(_clientName)) return;

        if (!_pausedLogged) _logger.LogProcessorPaused(_name);
        _pausedLogged = true;
        await _controller.WhenResumedAsync(_clientName, stop);
        _pausedLogged = false;
        _logger.LogProcessorResumed(_name);
    }

    /// <summary>At least one permit, waiting for it; then as many more as are available now, up to <paramref name="wanted"/>.</summary>
    private async Task<int> AcquirePermitsAsync(RateLimiter limiter, int wanted, CancellationToken stop)
    {
        while (true)
        {
            using var first = await limiter.AcquireAsync(1, stop);
            if (first.IsAcquired) break;

            // The limiter's own queue is full: wait as long as it says, or a second.
            var retryAfter = first.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : TimeSpan.FromSeconds(1);
            if (!await DelayAsync(retryAfter, stop)) stop.ThrowIfCancellationRequested();
        }

        var permitted = 1;
        while (permitted < wanted)
        {
            using var lease = limiter.AttemptAcquire(1);
            if (!lease.IsAcquired) break;
            permitted++;
        }

        return permitted;
    }

    private void StartDelivery(QueueMessage<TMessage> message, IOwnedReceiver? owned)
    {
        var id = Interlocked.Increment(ref _deliverySequence);
        var started = _time.GetTimestamp();
        var delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _deliveries[id] = (delivery.Task, started);

        // On its own task, so neither renewal nor a handler that blocks before its first await can hold up the loop.
        _ = Task.Run(async () =>
        {
            try
            {
                await DeliverAsync(message, owned, started);
            }
            finally
            {
                _deliveries.TryRemove(id, out _);
                _slots!.Release();
                delivery.TrySetResult();
            }
        });
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

    private async Task DeliverAsync(QueueMessage<TMessage> message, IOwnedReceiver? owned, long started)
    {
        var outcome = "abandoned";
        Exception? failure = null;
        var lease = message.Lease;
        lease.IsLastAttempt |= message.DequeueCount >= _options.MaxDequeueCount;

        using var logScope = _logger.BeginDeliveryScope(QueueName, message.MessageId, message.DequeueCount);

        var tags = QueueServiceDiagnostics.OperationTags(QueueName, QueueServiceDiagnostics.Messaging.Process);
        tags.Add(QueueServiceDiagnostics.Names.MessageId, message.MessageId);
        tags.Add(QueueServiceDiagnostics.Names.DequeueCount, message.DequeueCount);
        tags.Add(QueueServiceDiagnostics.Messaging.MessageId, message.MessageId);
        using Activity? activity = QueueServiceDiagnostics.Activities.StartProcessingMessageActivity(tags, message.CreationContext);
        lease.DeliveryActivity = activity;

        _metrics.ActiveHandlers.Add(1, QueueTags());
        if (message.InsertedOn is { } inserted)
            _metrics.TimeInQueue.Record(Math.Max(0, (_time.GetUtcNow() - inserted).TotalSeconds), QueueTags());

        // Armed before anything else — including downloading and deserializing — so nothing that happens to this
        // message while it is ours can let the lease lapse.
        await using var renewal = _options.RenewVisibility
            ? LeaseRenewal.Start(
                async () =>
                {
                    await message.RenewLockAsync(_options.VisibilityTimeout, CancellationToken.None);
                    _metrics.LeaseRenewals.Add(1, QueueTags());
                },
                () => message.IsSettled,
                TimeSpan.FromTicks(_options.VisibilityTimeout.Ticks / 2),
                _time,
                _renewalToken,
                ex =>
                {
                    _metrics.LeaseRenewalFailures.Add(1, QueueTags());
                    _logger.LogProcessorRenewFailed(_name, message.MessageId, ex);
                },
                _options.MaxRenewalDuration,
                () => _logger.LogProcessorRenewalLimitReached(_name, message.MessageId, _options.MaxRenewalDuration!.Value))
            : null;

        using var longRunning = _options.LongRunningHandlerWarning is { } threshold
            ? _time.CreateTimer(_ => _logger.LogProcessorHandlerLongRunning(_name, message.MessageId, _time.GetElapsedTime(started), threshold,
                _options.MaxRenewalDuration is { } max ? $" until MaxRenewalDuration ({max})" : " for as long as it runs"), null, threshold, Timeout.InfiniteTimeSpan)
            : null;

        try
        {
            if (!await ReadBodyAsync(message, owned))
            {
                // Cancelled while downloading the body at shutdown: give it straight back.
                await StopRenewalAsync(renewal);
                _logger.LogProcessorHandlerCancelled(_name, message.MessageId);
                if (!message.IsSettled) await AbandonSafelyAsync(message, TimeSpan.Zero);
                return;
            }

            if (message.Error is UnroutableMessageException unroutable)
            {
                await StopRenewalAsync(renewal);
                outcome = await DeadLetterOrAbandonAsync(message, unroutable.Message, DeadLetterCategories.Unroutable);
                return;
            }

            if (message.Error is not null && _options.DeadLetterOnDeserializationFailure)
            {
                await StopRenewalAsync(renewal);
                outcome = await DeadLetterOrAbandonAsync(message, $"the body could not be read: {message.Error.Message}", DeadLetterCategories.Deserialization);
                return;
            }

            if (message.DequeueCount > _options.MaxDequeueCount)
            {
                await StopRenewalAsync(renewal);
                outcome = await DeadLetterOrAbandonAsync(message, $"delivered {message.DequeueCount} times; the limit is {_options.MaxDequeueCount}", DeadLetterCategories.MaxDequeueCount);
                return;
            }

            try
            {
                // Resolving the handler and the middleware is inside the boundary: a constructor that throws is a failed delivery too.
                await using var scope = _scopes.CreateAsyncScope();
                var context = new QueueMessageContext(message, scope.ServiceProvider, _clientName, typeof(TMessage), _handlerName, _handlerToken);
                await Pipeline(message)(context);
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
                failure = ex;
                activity.RecordFailure(ex);
                _metrics.MessageFailures.Add(1, QueueTags());
                outcome = await HandleFailureAsync(message, ex);
                return;
            }
            catch (Exception ex)
            {
                // The handler settled the message and then threw. The settlement stands; report it as what it was:
                // an exception on a span that did not fail, so no error status.
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
                        failure = ex;
                        activity.RecordFailure(ex);
                        _metrics.MessageFailures.Add(1, QueueTags());
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
            _metrics.MessagesProcessed.Add(1, QueueTags());
        }
        catch (Exception ex)
        {
            // Anything the inner boundaries did not cover: scope disposal, an option callback, a logging sink.
            // A delivery must never fault silently and leave its message in limbo.
            _logger.LogProcessorDeliveryFailed(_name, message.MessageId, ex);
            failure = ex;
            activity.RecordFailure(ex);
            _metrics.MessageFailures.Add(1, QueueTags());
            if (!message.IsSettled) await AbandonSafelyAsync(message, QueueProcessorOptions.MaxRetryDelay);
            outcome = "abandoned";
        }
        finally
        {
            await StopRenewalAsync(renewal);
            _metrics.ActiveHandlers.Add(-1, QueueTags());

            var elapsed = _time.GetElapsedTime(started).TotalSeconds;
            var finalTags = QueueTags();
            finalTags.Add(QueueServiceDiagnostics.Names.QueueProcessSuccess, outcome is "completed" or "left_to_handler");
            finalTags.Add(QueueServiceDiagnostics.Names.Outcome, outcome);
            _metrics.MessageProcessingDuration.Record(elapsed, finalTags);
            _metrics.ProcessDuration.Record(elapsed, QueueServiceDiagnostics.MessagingTags(QueueName, QueueServiceDiagnostics.Messaging.Process, QueueServiceDiagnostics.Messaging.Process, failure));
            activity?.SetTag(QueueServiceDiagnostics.Names.Outcome, outcome);
            lease.DeliveryActivity = null;
        }
    }

    /// <summary>
    /// Brings in a body kept outside the queue and reads it, setting <see cref="QueueMessage{T}.Body"/> or
    /// <see cref="QueueMessage{T}.Error"/>. False when shutdown cancelled the download.
    /// </summary>
    private async Task<bool> ReadBodyAsync(QueueMessage<TMessage> message, IOwnedReceiver? owned)
    {
        if (owned is not null)
        {
            try
            {
                if (message.Error is null && await owned.ResolvePayloadAsync(message.Lease, _handlerToken) is { } error)
                    message.SetBody(default, error);
            }
            catch (OperationCanceledException) when (_handlerToken.IsCancellationRequested)
            {
                return false;
            }
        }

        if (message.Error is null)
        {
            if (_hooks.ReadBody is { } read) read(message);
            else owned?.Deserialize(message);
        }

        // A wrapper's own reading of the body (the CloudEvent adapter reads the event's data), which sets Error when it
        // fails, so that failure takes the dead-letter path with this processor's options snapshot, metric and outcome.
        if (message.Error is null) _hooks.AfterDeserialize?.Invoke(message);
        return true;
    }

    private QueueMessageDelegate Pipeline(QueueMessage<TMessage> message)
    {
        QueueMessageDelegate next = async context =>
        {
            var handler = _hooks.CreateHandler is { } create ? create(context.Services) : context.Services.GetRequiredService<THandler>();
            await handler.HandleAsync(message, context.CancellationToken);
        };

        if (_hooks.Middleware is not { Count: > 0 } middleware) return next;

        for (var i = middleware.Count - 1; i >= 0; i--)
        {
            var resolve = middleware[i];
            var inner = next;
            next = context => resolve(context.Services).InvokeAsync(context, inner);
        }

        return next;
    }

    /// <summary>The error policy: <see cref="QueueProcessorOptions.DeadLetterOn{TException}"/>, then <see cref="QueueProcessorOptions.OnHandlerError"/>, then retry.</summary>
    private async Task<string> HandleFailureAsync(QueueMessage<TMessage> message, Exception ex)
    {
        var action = DecideOnFailure(message, ex);
        switch (action)
        {
            case HandlerErrorAction.DeadLetter:
                _logger.LogHandlerFailedPolicy(QueueName, message.MessageId, ex.GetType().Name, action, ex);
                return await DeadLetterOrAbandonAsync(message, $"the handler threw {ex.GetType().Name}: {ex.Message}", DeadLetterCategories.Handler);

            case HandlerErrorAction.Complete:
                _logger.LogHandlerFailedPolicy(QueueName, message.MessageId, ex.GetType().Name, action, ex);
                try
                {
                    await message.CompleteAsync(CancellationToken.None);
                    return "completed_after_failure";
                }
                catch (Exception completeFailure)
                {
                    _logger.LogDeleteFailed(QueueName, message.MessageId, _options.VisibilityTimeout, completeFailure);
                    return "complete_failed";
                }

            default:
                var delay = RetryDelayFor(message.DequeueCount);
                if (message.IsLastAttempt) _logger.LogHandlerFailedLastAttempt(QueueName, message.MessageId, message.DequeueCount, ex);
                else _logger.LogHandlerFailed(LogLevel.Warning, QueueName, message.MessageId, message.DequeueCount, delay, ex);
                if (!message.IsSettled) await AbandonSafelyAsync(message, delay);
                return "abandoned";
        }
    }

    private HandlerErrorAction DecideOnFailure(QueueMessage<TMessage> message, Exception ex)
    {
        foreach (var type in _options.DeadLetterExceptionTypes)
        {
            if (type.IsInstanceOfType(ex)) return HandlerErrorAction.DeadLetter;
        }

        if (_options.OnHandlerError is not { } decide) return HandlerErrorAction.Retry;

        try
        {
            return decide(new HandlerErrorContext(ex, message));
        }
        catch (Exception callbackFailure)
        {
            _logger.LogProcessorErrorPolicyFailed(_name, message.MessageId, callbackFailure);
            return HandlerErrorAction.Retry;
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

    private async Task<string> DeadLetterOrAbandonAsync(QueueMessage<TMessage> message, string reason, string category)
    {
        try
        {
            await message.DeadLetterAsync(reason, category, CancellationToken.None);
            _metrics.MessageFailures.Add(1, QueueTags());
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

    private TagList QueueTags() => QueueServiceDiagnostics.MetricTags(QueueName);
}

/// <summary>A message a routed processor has no handler for; always dead-lettered, as no retry can find it one.</summary>
internal sealed class UnroutableMessageException(string message) : DeserializationException(message);
