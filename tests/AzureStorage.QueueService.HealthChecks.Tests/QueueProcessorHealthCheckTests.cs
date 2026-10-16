using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AzureStorage.QueueService.HealthChecks.Tests;

/// <summary>
/// The processor check, fed a fake controller: the thresholds that decide Healthy, Degraded and Unhealthy. Run through
/// <see cref="HealthCheckService"/> as an application would, so the registration (name, failure status) is covered too.
/// </summary>
public class QueueProcessorHealthCheckTests
{
    private const string CheckName = "azure-storage-queue-processors";

    private sealed class FakeController(params QueueProcessorStatus[] statuses) : IQueueProcessorController
    {
        public IReadOnlyList<QueueProcessorStatus> GetStatus() => statuses;
        public void Pause(string clientName) => throw new NotSupportedException();
        public void Resume(string clientName) => throw new NotSupportedException();
        public bool IsPaused(string clientName) => false;
    }

    private static QueueProcessorStatus Processor(
        string queue = "orders",
        QueueProcessorState state = QueueProcessorState.Running,
        int failures = 0,
        string? error = null,
        TimeSpan? oldestHandlerAge = null) =>
        new("", queue, "OrderHandler", typeof(string), state, oldestHandlerAge is null ? 0 : 1, 16,
            DateTimeOffset.UnixEpoch, failures > 0 ? DateTimeOffset.UnixEpoch : null, failures, error, oldestHandlerAge);

    private static async Task<HealthReportEntry> CheckAsync(IQueueProcessorController controller, Action<QueueProcessorHealthOptions>? configure = null)
    {
        var services = new ServiceCollection().AddLogging().AddSingleton(controller);
        services.AddHealthChecks().AddAzureStorageQueueProcessors(configure: configure);
        await using var provider = services.BuildServiceProvider();
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        return report.Entries[CheckName];
    }

    [Fact(DisplayName = "Processors: running and not-yet-started processors are healthy, with one data entry each")]
    public async Task Running_And_Starting_Processors_Are_Healthy()
    {
        var result = await CheckAsync(new FakeController(Processor("orders"), Processor("invoices", QueueProcessorState.NotStarted)));

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Be("1 queue processor running, 1 waiting for the host to finish starting.");
        result.Data.Keys.Should().BeEquivalentTo("orders/OrderHandler", "invoices/OrderHandler");
        result.Data["orders/OrderHandler"].Should().BeAssignableTo<IReadOnlyDictionary<string, object?>>()
            .Which["state"].Should().Be("Running");
    }

    [Theory(DisplayName = "Processors: receive failures turn the check unhealthy only at the threshold, naming the processor and the error")]
    [InlineData(2, HealthStatus.Healthy)]
    [InlineData(3, HealthStatus.Unhealthy)]
    public async Task Consecutive_Receive_Failures_Are_Unhealthy_At_The_Threshold(int failures, HealthStatus expected)
    {
        var result = await CheckAsync(new FakeController(Processor(failures: failures, error: "Connection refused")));

        result.Status.Should().Be(expected);
        if (expected == HealthStatus.Unhealthy)
            result.Description.Should().Be("orders/OrderHandler cannot receive: the last 3 attempts failed (last error: Connection refused).");
    }

    [Theory(DisplayName = "Processors: a paused processor is degraded unless PausedStatus says pausing is normal")]
    [InlineData(HealthStatus.Degraded)]
    [InlineData(HealthStatus.Healthy)]
    public async Task Paused_Processor_Reports_PausedStatus(HealthStatus pausedStatus)
    {
        var result = await CheckAsync(new FakeController(Processor(state: QueueProcessorState.Paused)), o => o.PausedStatus = pausedStatus);

        result.Status.Should().Be(pausedStatus);
    }

    [Fact(DisplayName = "Processors: a handler running longer than MaxHandlerAge is degraded; with no limit set it is not")]
    public async Task Long_Running_Handler_Is_Degraded_Only_With_MaxHandlerAge()
    {
        var controller = new FakeController(Processor(oldestHandlerAge: TimeSpan.FromMinutes(11)));

        (await CheckAsync(controller)).Status.Should().Be(HealthStatus.Healthy, "the age limit is off by default");

        var limited = await CheckAsync(controller, o => o.MaxHandlerAge = TimeSpan.FromMinutes(10));
        limited.Status.Should().Be(HealthStatus.Degraded);
        limited.Description.Should().Contain("for 11m 0s, longer than the 10m 0s allowed");
    }
}
