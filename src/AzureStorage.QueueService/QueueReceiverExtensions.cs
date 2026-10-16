using System.Runtime.CompilerServices;

namespace AzureStorage.QueueService;

/// <summary>How <see cref="QueueReceiverExtensions.ReadAllAsync{T}"/> polls.</summary>
public sealed class ReadAllOptions
{
    /// <summary>
    /// How many messages each receive asks for (1 to 32). Every message in a batch is hidden from the moment it is
    /// received, so the visibility timeout has to cover handling the whole batch; keep it at 1 unless handling is quick.
    /// </summary>
    public int MaxMessages { get; set; } = 1;

    /// <summary>How long each received message stays hidden; null for the client's <see cref="QueueClientSettings.VisibilityTimeout"/>, then the service's 30 seconds.</summary>
    public TimeSpan? VisibilityTimeout { get; set; }

    /// <summary>The wait after an empty receive; doubles up to <see cref="EmptyQueueBackoffMax"/> and resets when a message arrives.</summary>
    public TimeSpan EmptyQueueBackoffMin { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between receives of an empty queue.</summary>
    public TimeSpan EmptyQueueBackoffMax { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest wait after a failed receive; the wait doubles from <see cref="EmptyQueueBackoffMin"/>.</summary>
    public TimeSpan ErrorBackoffMax { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Called with each failed receive before the loop backs off and tries again: log it here. Failed receives never end
    /// the loop; only cancellation does.
    /// </summary>
    public Action<Exception>? OnReceiveError { get; set; }

    /// <summary>The clock the waits use; a fake one in tests.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

/// <summary>A polling loop over <see cref="IQueueReceiver"/>, for code that owns its loop rather than using a processor.</summary>
public static class QueueReceiverExtensions
{
    /// <summary>
    /// Receives messages for as long as <paramref name="cancellationToken"/> allows, waiting with a growing backoff while
    /// the queue is empty and after a failed receive, and hands them over one at a time. Settling each message —
    /// <see cref="QueueMessage{T}.CompleteAsync"/> and the rest — is the caller's job, as is anything a processor would do
    /// for you (lease renewal, retries, dead-lettering). Ends when the token is cancelled, without throwing.
    /// </summary>
    /// <example><code>
    /// await foreach (var message in receiver.ReadAllAsync&lt;Order&gt;(new ReadAllOptions { OnReceiveError = e => logger.LogError(e, "Receive failed") }, stoppingToken))
    /// {
    ///     if (message.Error is not null) { await message.DeadLetterAsync(message.Error.Message); continue; }
    ///     await Fulfil(message.Body!);
    ///     await message.CompleteAsync();
    /// }
    /// </code></example>
    public static async IAsyncEnumerable<QueueMessage<T>> ReadAllAsync<T>(this IQueueReceiver receiver, ReadAllOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        options ??= new ReadAllOptions();
        if (options.MaxMessages is < 1 or > StorageQueue.ServiceMaxMessages) throw new ArgumentOutOfRangeException(nameof(options), options.MaxMessages, "MaxMessages must be 1 to 32.");
        if (options.EmptyQueueBackoffMin <= TimeSpan.Zero || options.EmptyQueueBackoffMax < options.EmptyQueueBackoffMin || options.ErrorBackoffMax < options.EmptyQueueBackoffMin)
            throw new ArgumentOutOfRangeException(nameof(options), "EmptyQueueBackoffMin must be positive and no larger than EmptyQueueBackoffMax or ErrorBackoffMax.");

        var emptyBackoff = options.EmptyQueueBackoffMin;
        var errorBackoff = options.EmptyQueueBackoffMin;

        while (!cancellationToken.IsCancellationRequested)
        {
            IReadOnlyList<QueueMessage<T>> batch;
            try
            {
                batch = await receiver.ReceiveAsync<T>(options.MaxMessages, options.VisibilityTimeout, cancellationToken);
                errorBackoff = options.EmptyQueueBackoffMin;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            catch (Exception e)
            {
                options.OnReceiveError?.Invoke(e);
                if (!await DelayAsync(errorBackoff, options.TimeProvider, cancellationToken)) yield break;
                errorBackoff = Min(errorBackoff * 2, options.ErrorBackoffMax);
                continue;
            }

            if (batch.Count == 0)
            {
                if (!await DelayAsync(emptyBackoff, options.TimeProvider, cancellationToken)) yield break;
                emptyBackoff = Min(emptyBackoff * 2, options.EmptyQueueBackoffMax);
                continue;
            }

            emptyBackoff = options.EmptyQueueBackoffMin;
            foreach (var message in batch) yield return message;
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, TimeProvider time, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, time, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
