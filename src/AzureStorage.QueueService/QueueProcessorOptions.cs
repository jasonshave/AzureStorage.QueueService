using System.Threading.RateLimiting;

namespace AzureStorage.QueueService;

/// <summary>
/// How a processor registered with <c>AddProcessor&lt;TMessage, THandler&gt;()</c> runs. One receive loop takes messages
/// for whichever handlers are free (up to <see cref="MaxConcurrentMessages"/> at once) and delivers each in its own
/// scope, with its own lease renewal.
/// </summary>
/// <remarks>
/// Everything except the callbacks (<see cref="RetryDelay"/>, <see cref="OnHandlerError"/>), <see cref="RateLimiter"/>
/// and <see cref="DeadLetterOn{TException}"/> can be bound from configuration with the
/// <c>AddProcessor&lt;TMessage, THandler&gt;(IConfiguration)</c> overload.
/// </remarks>
public sealed class QueueProcessorOptions
{
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>The longest wait the timer APIs accept (a little under 50 days).</summary>
    internal static readonly TimeSpan MaxTimer = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>The longest visibility timeout the service accepts.</summary>
    internal static readonly TimeSpan MaxVisibility = TimeSpan.FromDays(7);

    private readonly List<Type> _deadLetterOn = new();


    /// <summary>
    /// How many messages may be handled at once, each in its own scope with its own lease renewal. The processor receives
    /// only as many messages as there are free handlers (up to 32 per request), so it never holds a message no handler is
    /// free for.
    /// </summary>
    public int MaxConcurrentMessages { get; set; } = 1;

    /// <summary>
    /// How long a received message stays hidden while a handler runs: 1 second to 7 days, in whole seconds (the
    /// service drops fractions). Renewed while the handler is still running when <see cref="RenewVisibility"/> is on,
    /// so it only needs to cover the renewal interval, not the whole handler.
    /// </summary>
    public TimeSpan VisibilityTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Renew the lease at half <see cref="VisibilityTimeout"/> while the handler runs. Delivery is still at-least-once;
    /// renewal closes the avoidable window where a slow handler lets another consumer take the same message.
    /// </summary>
    public bool RenewVisibility { get; set; } = true;

    /// <summary>
    /// The longest time a lease is renewed for one delivery; null (the default) renews for as long as the handler runs.
    /// Past it, renewal stops and the message becomes visible to other consumers when its lease lapses, so a hung handler
    /// cannot hide its message for ever. Like Service Bus's <c>MaxAutoLockRenewalDuration</c>.
    /// </summary>
    public TimeSpan? MaxRenewalDuration { get; set; }

    /// <summary>
    /// Logs a warning, once per delivery, when a handler has been running this long; null turns it off. Default 10
    /// minutes. The <c>queue_processor_oldest_handler_age_seconds</c> gauge shows the same thing as a number.
    /// </summary>
    public TimeSpan? LongRunningHandlerWarning { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Complete the message when the handler returns. Off, a message the handler returns without settling stays
    /// hidden until its lease lapses and is then delivered again. Either way, a message whose handler throws is abandoned for retry.
    /// </summary>
    public bool AutoComplete { get; set; } = true;

    /// <summary>A message received more times than this is moved to the poison queue without calling the handler.</summary>
    public int MaxDequeueCount { get; set; } = 5;

    /// <summary>A message whose body cannot be decoded or deserialized is moved to the poison queue without calling the handler.</summary>
    public bool DeadLetterOnDeserializationFailure { get; set; } = true;

    /// <summary>
    /// How long a failed message stays hidden before it is retried, given its dequeue count. The default doubles from
    /// 5 seconds and caps at 5 minutes: 5 s, 10 s, 20 s, 40 s, ... If the callback throws, 5 minutes is used; a
    /// result outside 0 to 7 days is clamped.
    /// </summary>
    public Func<long, TimeSpan> RetryDelay { get; set; } = dequeueCount =>
    {
        var exponent = Math.Clamp(dequeueCount - 1, 0, 10);
        var delay = TimeSpan.FromSeconds(5 * Math.Pow(2, exponent));
        return delay < MaxRetryDelay ? delay : MaxRetryDelay;
    };

    /// <summary>
    /// Decides what happens to a message whose handler threw, when no <see cref="DeadLetterOn{TException}"/> rule matched:
    /// retry it (the default without a callback), dead-letter it, or complete it. If the callback throws, the message is retried.
    /// </summary>
    public Func<HandlerErrorContext, HandlerErrorAction>? OnHandlerError { get; set; }

    /// <summary>
    /// Dead-letters a message straight away, without retrying it, when its handler throws <typeparamref name="TException"/>
    /// or an exception derived from it: one that retrying cannot fix, such as a validation failure. Checked before
    /// <see cref="OnHandlerError"/>.
    /// </summary>
    public QueueProcessorOptions DeadLetterOn<TException>() where TException : Exception
    {
        if (!_deadLetterOn.Contains(typeof(TException))) _deadLetterOn.Add(typeof(TException));
        return this;
    }

    internal IReadOnlyList<Type> DeadLetterExceptionTypes => _deadLetterOn;

    /// <summary>
    /// Limits how fast the processor takes messages: one permit per message, acquired before each receive, and a receive
    /// asks for no more messages than it has permits for. For example a <c>TokenBucketRateLimiter</c> to stay under a
    /// downstream API's limit. The processor does not dispose it.
    /// </summary>
    public RateLimiter? RateLimiter { get; set; }

    /// <summary>
    /// How long the receive loop waits after an empty receive before polling again; doubles up to <see cref="EmptyQueueBackoffMax"/>
    /// and resets on the next message. One loop polls per processor, however many messages it handles at once.
    /// </summary>
    public TimeSpan EmptyQueueBackoffMin { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest wait between polls of an empty queue.</summary>
    public TimeSpan EmptyQueueBackoffMax { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest wait after a failed receive; the wait doubles from <see cref="EmptyQueueBackoffMin"/> and resets on the next success.</summary>
    public TimeSpan ErrorBackoffMax { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long shutdown waits for handlers already running before cancelling their token and stopping their lease
    /// renewal. A handler that ignores its token is given the same time again, after which shutdown stops waiting
    /// for it; its message becomes visible again when its lease lapses.
    /// </summary>
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(30);

    internal void Validate(string processorName)
    {
        if (MaxConcurrentMessages < 1) Fail(processorName, "MaxConcurrentMessages must be at least 1.");
        // The SDK sends whole seconds, so anything under a second becomes 0 and the service rejects every receive.
        if (VisibilityTimeout < TimeSpan.FromSeconds(1) || VisibilityTimeout > MaxVisibility) Fail(processorName, $"VisibilityTimeout must be between 1 second and 7 days (it is {VisibilityTimeout}).");
        // Renewal runs at half the timeout; with a fraction the service's lease is shorter than that half assumes.
        if (VisibilityTimeout.Ticks % TimeSpan.TicksPerSecond != 0) Fail(processorName, $"VisibilityTimeout must be a whole number of seconds (it is {VisibilityTimeout}).");
        if (MaxDequeueCount < 1) Fail(processorName, "MaxDequeueCount must be at least 1.");
        if (EmptyQueueBackoffMin <= TimeSpan.Zero || EmptyQueueBackoffMax < EmptyQueueBackoffMin) Fail(processorName, "EmptyQueueBackoffMin must be positive and no larger than EmptyQueueBackoffMax.");
        if (ErrorBackoffMax < EmptyQueueBackoffMin) Fail(processorName, "ErrorBackoffMax must be at least EmptyQueueBackoffMin.");
        if (ShutdownDrainTimeout < TimeSpan.Zero) Fail(processorName, "ShutdownDrainTimeout cannot be negative.");
        if (RetryDelay is null) Fail(processorName, "RetryDelay must be set.");
        if (MaxRenewalDuration is { } maxRenewal && maxRenewal <= TimeSpan.Zero) Fail(processorName, "MaxRenewalDuration must be positive, or null to renew for as long as the handler runs.");
        if (LongRunningHandlerWarning is { } warning && warning <= TimeSpan.Zero) Fail(processorName, "LongRunningHandlerWarning must be positive, or null for no warning.");

        // Anything a timer waits on must be within what the timer APIs accept, or the wait throws at runtime instead.
        foreach (var (name, value) in new[]
                 {
                     (nameof(EmptyQueueBackoffMax), EmptyQueueBackoffMax), (nameof(ErrorBackoffMax), ErrorBackoffMax), (nameof(ShutdownDrainTimeout), ShutdownDrainTimeout),
                     (nameof(MaxRenewalDuration), MaxRenewalDuration ?? TimeSpan.Zero), (nameof(LongRunningHandlerWarning), LongRunningHandlerWarning ?? TimeSpan.Zero),
                 })
        {
            if (value > MaxTimer) Fail(processorName, $"{name} must be no longer than {MaxTimer.TotalDays:0} days (it is {value}).");
        }
    }

    private static void Fail(string processorName, string problem) => throw new InvalidOperationException($"{processorName}: {problem}");
}

/// <summary>What a processor does with a message whose handler threw.</summary>
public enum HandlerErrorAction
{
    /// <summary>Abandon it with <see cref="QueueProcessorOptions.RetryDelay"/>, to be delivered again; dead-lettered once it passes <see cref="QueueProcessorOptions.MaxDequeueCount"/>.</summary>
    Retry,

    /// <summary>Move it to the poison queue now.</summary>
    DeadLetter,

    /// <summary>Delete it: the failure does not matter, or the handler has dealt with it.</summary>
    Complete,
}

/// <summary>What <see cref="QueueProcessorOptions.OnHandlerError"/> is told about a failed delivery.</summary>
public sealed class HandlerErrorContext
{
    internal HandlerErrorContext(Exception exception, IQueueMessage message)
    {
        Exception = exception;
        Message = message;
    }

    /// <summary>What the handler threw.</summary>
    public Exception Exception { get; }

    /// <summary>The message, still unsettled. Its <see cref="IQueueMessage.IsLastAttempt"/> says whether a retry is left.</summary>
    public IQueueMessage Message { get; }
}
