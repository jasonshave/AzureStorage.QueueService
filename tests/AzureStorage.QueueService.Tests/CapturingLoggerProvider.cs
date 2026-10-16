using Microsoft.Extensions.Logging;

namespace AzureStorage.QueueService.Tests;

/// <summary>Keeps every log entry, with a signal per entry, so a test can await a log line instead of sleeping.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly Signal _logged = new();

    public List<(LogLevel Level, int EventId, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public Task WaitForAsync(int eventId) => _logged.WaitUntilAsync(() => { lock (Entries) return Entries.Any(e => e.EventId == eventId); });

    public (LogLevel Level, int EventId, string Message) Single(int eventId)
    {
        lock (Entries) return Entries.Single(e => e.EventId == eventId);
    }

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (provider.Entries) provider.Entries.Add((logLevel, eventId.Id, formatter(state, exception)));
            provider._logged.Set();
        }
    }
}
