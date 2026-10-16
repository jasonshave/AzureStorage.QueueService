using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace AzureStorage.QueueService.Tests;

public class LeaseRenewalTests
{
    [Theory(DisplayName = "A renewal InvalidOperationException is reported unless the message settled during renewal")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Renewal_InvalidOperationException_Is_Ignored_Only_After_Settlement(bool settlesDuringRenewal)
    {
        var time = new FakeTimeProvider();
        var settled = false;
        var failure = new InvalidOperationException("The renewal policy failed.");
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new List<Exception>();
        await using var renewal = LeaseRenewal.Start(
            renewAsync: () =>
            {
                settled = settlesDuringRenewal;
                attempted.TrySetResult();
                throw failure;
            },
            isSettled: () => settled,
            interval: TimeSpan.FromSeconds(1),
            time: time,
            stopAll: CancellationToken.None,
            onFailed: reported.Add);

        time.Advance(TimeSpan.FromSeconds(1));
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await renewal.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        if (settlesDuringRenewal)
            reported.Should().BeEmpty("settlement can race with the renewal attempt");
        else
            reported.Should().ContainSingle().Which.Should().BeSameAs(failure);
    }

    [Fact(DisplayName = "R1: a disposed renewal releases its registration on the processor-wide token; an undisposed one would not")]
    public async Task Disposed_Renewal_Is_Not_Retained_By_The_Parent_Token()
    {
        // The parent outlives every delivery, so a linked source that stays registered on it is a leak per message.
        using var parent = new CancellationTokenSource();

        var disposed = await StartAndFinishAsync(parent.Token, dispose: true);
        var leaked = await StartAndFinishAsync(parent.Token, dispose: false);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        disposed.TryGetTarget(out _).Should().BeFalse("DisposeAsync unregisters the linked source from the parent");
        leaked.TryGetTarget(out _).Should().BeTrue("the control case shows the parent really does retain an undisposed linked source");
    }

    // Separate, non-inlined, so no local keeps the renewal or its source alive once the method returns.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference<CancellationTokenSource>> StartAndFinishAsync(CancellationToken parent, bool dispose)
    {
        var renewal = LeaseRenewal.Start(
            renewAsync: () => ValueTask.CompletedTask,
            isSettled: () => true,
            interval: TimeSpan.FromMinutes(1),
            time: new FakeTimeProvider(),
            stopAll: parent,
            onFailed: _ => { });
        var weak = new WeakReference<CancellationTokenSource>(renewal.LinkedSource);

        if (dispose) await renewal.DisposeAsync();
        else await renewal.StopAsync();

        return weak;
    }

    [Fact(DisplayName = "Stop and dispose are safe to call repeatedly")]
    public async Task Stop_And_Dispose_Are_Idempotent()
    {
        var renewal = LeaseRenewal.Start(() => ValueTask.CompletedTask, () => true, TimeSpan.FromMinutes(1), new FakeTimeProvider(), CancellationToken.None, _ => { });

        await renewal.StopAsync();
        await renewal.StopAsync();
        await renewal.DisposeAsync();
        await renewal.DisposeAsync();
        await renewal.StopAsync();
    }
}
