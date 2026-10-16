using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;

namespace AzureStorage.QueueService;

/// <summary>
/// Catches messages the Azure SDK could not Base64-decode, so they come back from a receive as messages with
/// <see cref="QueueMessage{T}.Error"/> set instead of failing the whole batch.
/// </summary>
/// <remarks>
/// With <c>MessageEncoding = Base64</c> the SDK decodes before the library sees anything. Without a
/// <see cref="QueueClientOptions.MessageDecodingFailed"/> handler, one undecodable message throws for the entire
/// receive and every healthy message in the batch stays hidden until its lease lapses. The library's SDK client
/// captures an internal handler at construction, without leaving it on caller-owned options. That handler runs
/// inside the receive call, on the same async flow, which is why an
/// <see cref="AsyncLocal{T}"/> is enough to hand it back to the caller.
/// </remarks>
internal static class DecodingFailures
{
    private static readonly AsyncLocal<List<QueueMessageDecodingFailedEventArgs>?> Current = new();

    public static List<QueueMessageDecodingFailedEventArgs> Begin()
    {
        var failures = new List<QueueMessageDecodingFailedEventArgs>();
        Current.Value = failures;
        return failures;
    }

    public static void End() => Current.Value = null;

    internal static Task OnDecodingFailedAsync(QueueMessageDecodingFailedEventArgs args)
    {
        if (!TryCollect(args))
            throw new FormatException("The queue message could not be decoded using the configured Base64 encoding.");

        return Task.CompletedTask;
    }

    // Only intercept failures belonging to a wrapped receive or peek. The SDK keeps its usual behavior otherwise.
    internal static bool TryCollect(QueueMessageDecodingFailedEventArgs args)
    {
        if (Current.Value is not { } failures) return false;
        failures.Add(args);
        return true;
    }
}
