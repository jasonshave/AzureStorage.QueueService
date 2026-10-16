namespace AzureStorage.QueueService.Tests;

/// <summary>
/// A "something happened" signal. WaitUntilAsync checks the condition, and if it is not yet true waits for the
/// next Set() and checks again — grabbing the pending task *before* checking, so a Set() that lands between the
/// check and the wait is never missed. The real-time cap is only a safety net against a hung test.
/// </summary>
internal sealed class Signal
{
    private static readonly TimeSpan SafetyNet = TimeSpan.FromSeconds(10);
    private TaskCompletionSource _tcs = New();
    private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Set() => Interlocked.Exchange(ref _tcs, New()).TrySetResult();

    public async Task WaitUntilAsync(Func<bool> condition)
    {
        while (true)
        {
            var next = Volatile.Read(ref _tcs).Task;
            if (condition()) return;
            await next.WaitAsync(SafetyNet);
        }
    }
}
