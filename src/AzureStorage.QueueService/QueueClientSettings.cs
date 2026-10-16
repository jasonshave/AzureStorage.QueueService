using System.Diagnostics.CodeAnalysis;
using Azure.Core;
using Azure.Storage.Queues;

namespace AzureStorage.QueueService;

[ExcludeFromCodeCoverage]
public class QueueClientSettings
{
    /// <summary>
    /// The storage account connection string. Use this together with <see cref="QueueName"/>,
    /// or use <see cref="EndpointUri"/> and <see cref="TokenCredential"/> instead.
    /// </summary>
    public string? ConnectionString { get; set; }

    public string? QueueName { get; set; }

    /// <summary>
    /// When <c>true</c>, the queue is created the first time the client is used if it does not already exist.
    /// </summary>
    public bool CreateIfNotExists { get; set; }

    /// <summary>
    /// The full queue URI, including the queue name. Use this together with <see cref="TokenCredential"/>.
    /// </summary>
    public Uri? EndpointUri { get; set; }

    public TokenCredential? TokenCredential { get; set; }

    /// <summary>
    /// How long a message received with <see cref="IQueueReceiver.ReceiveAsync{T}"/> stays hidden from other consumers
    /// when the call does not say. When not set, the service default of 30 seconds applies. A processor uses its own
    /// <see cref="QueueProcessorOptions.VisibilityTimeout"/> instead.
    /// </summary>
    public TimeSpan? VisibilityTimeout { get; set; }

    /// <summary>
    /// Where dead-lettered messages go. Defaults to the queue name plus <c>-poison</c>; set it explicitly when the queue
    /// name is too long for that (Azure allows 63 characters) or when a shared poison queue is wanted.
    /// </summary>
    public string? PoisonQueueName { get; set; }

    /// <summary>
    /// The serializer for this client only. Set with <c>UseSerializer</c> or <c>UseJsonSerializer</c> on the client
    /// builder; when null, the serializer shared by all clients is used.
    /// </summary>
    public IQueueMessageSerializer? Serializer { get; set; }

    /// <summary>
    /// Options for the underlying Azure SDK client, such as the retry policy or
    /// <see cref="QueueClientOptions.MessageEncoding"/> for queues that carry Base64-encoded messages.
    /// </summary>
    public QueueClientOptions? ClientOptions { get; set; }

    internal static readonly TimeSpan MinVisibility = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxVisibility = TimeSpan.FromDays(7);

    /// <summary>True when the SDK client is built from the connection string; otherwise from the endpoint URI.</summary>
    internal bool UsesConnectionString => !string.IsNullOrEmpty(ConnectionString);

    /// <summary>The queue named by the last path segment of <see cref="EndpointUri"/>, if any.</summary>
    internal string? EndpointQueueName =>
        EndpointUri?.AbsolutePath.TrimEnd('/').Split('/').Last() is { Length: > 0 } segment ? segment : null;

    /// <summary>
    /// The queue the SDK client will actually target, following the same precedence as construction: the connection
    /// string path uses <see cref="QueueName"/>; the endpoint path uses the queue in the URI and ignores <see cref="QueueName"/>.
    /// </summary>
    internal string? EffectiveQueueName => UsesConnectionString ? QueueName : EndpointQueueName;

    /// <summary>
    /// Where dead-lettered messages go: <see cref="PoisonQueueName"/>, or the queue name plus <c>-poison</c>. An empty
    /// name (a blank placeholder in configuration) counts as not set.
    /// </summary>
    internal string? EffectivePoisonQueueName => PoisonQueueNameFor(EffectiveQueueName);

    /// <summary>
    /// The queue an in-memory client stands in for (see <c>AddInMemoryQueues</c>): the one the SDK client would target,
    /// or <see cref="QueueName"/> alone, since a test host typically has no connection details at all.
    /// </summary>
    internal string? InMemoryQueueName =>
        EffectiveQueueName is { Length: > 0 } effective ? effective : (string.IsNullOrEmpty(QueueName) ? null : QueueName);

    /// <summary>The poison queue an in-memory client dead-letters to, named by the same rule as the real one.</summary>
    internal string? InMemoryPoisonQueueName => PoisonQueueNameFor(InMemoryQueueName);

    private string? PoisonQueueNameFor(string? queueName) =>
        HasPoisonQueueName ? PoisonQueueName : (queueName is { Length: > 0 } queue ? $"{queue}-poison" : null);

    private bool HasPoisonQueueName => !string.IsNullOrEmpty(PoisonQueueName);

    /// <summary>
    /// What any SDK client the library builds needs: a way to connect and one queue to target. Used for source queues
    /// and for poison destinations alike. Returns the problem, or null.
    /// </summary>
    internal string? ValidateConnection()
    {
        if (UsesConnectionString)
            return string.IsNullOrEmpty(QueueName) ? "has a ConnectionString but no QueueName." : null;

        if (EndpointUri is null || TokenCredential is null)
            return "is not configured. Set ConnectionString and QueueName, or set EndpointUri (including the queue name) and TokenCredential.";

        if (EndpointQueueName is null)
            return "has an EndpointUri with no queue name in its path.";

        // With endpoint authentication the SDK takes the queue from the URI, so a different QueueName would be
        // silently ignored and every check based on it would be checking the wrong queue.
        if (!string.IsNullOrEmpty(QueueName) && !string.Equals(QueueName, EndpointQueueName, StringComparison.Ordinal))
            return $"has QueueName '{QueueName}' but EndpointUri points at queue '{EndpointQueueName}'. With EndpointUri the queue comes from the URI; remove QueueName or make them agree.";

        return null;
    }

    /// <summary>
    /// The connection plus the processing policy of a source queue: receive timing and the poison destination.
    /// Shared by startup validation and source-queue construction so the two can never disagree. Not applied to
    /// destination clients, which have no policy of their own.
    /// </summary>
    internal string? Validate() => ValidateConnection() ?? ValidatePolicy(EffectiveQueueName);

    /// <summary>
    /// What an in-memory client needs: no connection, but the same processing policy, so a poison queue name that
    /// would fail in production fails the test too. Returns the problem, or null.
    /// </summary>
    internal string? ValidateInMemory() => InMemoryQueueName is { } queueName
        ? ValidatePolicy(queueName)
        : "has no QueueName. In-memory queues need no connection details, but still need a queue name to name the queue and its poison queue.";

    private string? ValidatePolicy(string? queueName)
    {
        if (VisibilityTimeout is { } visibility && (visibility < MinVisibility || visibility > MaxVisibility))
            return $"has a VisibilityTimeout of {visibility}; the service accepts 1 second to 7 days.";

        var poison = PoisonQueueNameFor(queueName);
        if (poison is not null && QueueNameRules.Check(poison) is { } problem)
        {
            return !HasPoisonQueueName
                ?$"derives the poison queue name '{poison}' from the queue name, and {problem} Set PoisonQueueName to a valid name."
                : $"has an invalid PoisonQueueName: {problem}";
        }

        if (poison is not null && string.Equals(poison, queueName, StringComparison.Ordinal))
            return $"has PoisonQueueName equal to its own queue name ('{poison}'). Dead-lettered messages would go straight back onto the queue with a fresh dequeue count; use a different poison queue.";

        return null;
    }
}

/// <summary>
/// Azure's rules for a queue name: 3 to 63 characters, lowercase letters, digits and single hyphens, starting and
/// ending with a letter or digit.
/// </summary>
internal static class QueueNameRules
{
    public const int MaxLength = 63;

    /// <summary>Returns a description of the problem, or null when the name is valid.</summary>
    public static string? Check(string name)
    {
        if (name.Length is < 3 or > MaxLength) return $"'{name}' is {name.Length} characters; a queue name must be 3 to {MaxLength}.";
        if (!char.IsAsciiLetterLower(name[0]) && !char.IsAsciiDigit(name[0])) return $"'{name}' must start with a lowercase letter or digit.";
        if (!char.IsAsciiLetterLower(name[^1]) && !char.IsAsciiDigit(name[^1])) return $"'{name}' must end with a lowercase letter or digit.";
        if (name.Contains("--")) return $"'{name}' must not contain consecutive hyphens.";
        foreach (var c in name)
        {
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-') return $"'{name}' may only contain lowercase letters, digits and hyphens.";
        }

        return null;
    }
}
