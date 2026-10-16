namespace AzureStorage.QueueService;

/// <summary>
/// Renews one message's lease at a fixed interval until stopped. Only the wait between renewals is cancellable;
/// a renewal request already sent is always awaited, so the receipt the service holds is the one the message has.
/// </summary>
/// <remarks>
/// One instance per delivery, with one end of life: <see cref="DisposeAsync"/> stops the loop, waits for it, and
/// disposes the linked token source. The disposal matters — a linked source that is cancelled but never disposed
/// stays registered on its parent for the parent's lifetime, which for a long-lived processor is a leak of one
/// registration per message ever processed.
/// </remarks>
internal sealed class LeaseRenewal : IAsyncDisposable
{
    private readonly CancellationTokenSource _stopWaiting;
    private readonly Task _loop;
    private bool _disposed;

    private LeaseRenewal(Func<ValueTask> renewAsync, Func<bool> isSettled, TimeSpan interval, TimeProvider time, CancellationToken stopAll, Action<Exception> onFailed)
    {
        _stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(stopAll);
        _loop = RunAsync(renewAsync, isSettled, interval, time, _stopWaiting.Token, onFailed);
    }

    /// <param name="renewAsync">Renews the lease once. Must not observe a cancellation token: an accepted renewal changes the receipt, so the response must be read.</param>
    /// <param name="isSettled">True once the message no longer needs renewing.</param>
    /// <param name="stopAll">The processor-wide signal that ends every renewal, for example at shutdown.</param>
    public static LeaseRenewal Start(Func<ValueTask> renewAsync, Func<bool> isSettled, TimeSpan interval, TimeProvider time, CancellationToken stopAll, Action<Exception> onFailed) =>
        new(renewAsync, isSettled, interval, time, stopAll, onFailed);

    /// <summary>For lifetime tests only: the linked source whose disposal unregisters from the parent.</summary>
    internal CancellationTokenSource LinkedSource => _stopWaiting;

    /// <summary>Stops scheduling renewals and waits for one already in flight. Safe to call more than once.</summary>
    public async ValueTask StopAsync()
    {
        if (!_disposed && !_stopWaiting.IsCancellationRequested) _stopWaiting.Cancel();
        await _loop;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync();
        _disposed = true;
        _stopWaiting.Dispose();
    }

    private static async Task RunAsync(Func<ValueTask> renewAsync, Func<bool> isSettled, TimeSpan interval, TimeProvider time, CancellationToken stopWaiting, Action<Exception> onFailed)
    {
        try
        {
            while (true)
            {
                await Task.Delay(interval, time, stopWaiting);
                if (isSettled()) return;
                await renewAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped; no more renewals are wanted.
        }
        catch (InvalidOperationException) when (isSettled())
        {
            // The handler settled the message itself between the check and the renew.
        }
        catch (Exception ex)
        {
            // Stop renewing. The lease lapses and the message is redelivered; the handler's own completion may then fail.
            onFailed(ex);
        }
    }
}
