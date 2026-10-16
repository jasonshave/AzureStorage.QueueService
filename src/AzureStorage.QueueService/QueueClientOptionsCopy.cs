using System.Reflection;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Specialized;

namespace AzureStorage.QueueService;

/// <summary>
/// Makes a private copy of caller-owned <see cref="QueueClientOptions"/>, so the library can attach its decoding handler
/// to options nobody else builds clients from.
/// </summary>
/// <remarks>
/// The public surface can't express a faithful copy: policies added with <see cref="ClientOptions.AddPolicy"/> and the
/// caller's own <see cref="QueueClientOptions.MessageDecodingFailed"/> handlers have no public getter. Those two are read by
/// reflection, here and nowhere else. If an Azure SDK update renames them, or the options can't be copied faithfully for
/// another reason, <see cref="Copy"/> throws rather than return a copy that silently drops caller configuration.
/// </remarks>
internal static class QueueClientOptionsCopy
{
    private const BindingFlags NonPublicInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    // ClientOptions.Policies: what AddPolicy appends to, each policy with its position.
    private static readonly PropertyInfo? Policies =
        typeof(ClientOptions).GetProperty("Policies", NonPublicInstance) is { } policies &&
        policies.PropertyType == typeof(List<(HttpPipelinePosition, HttpPipelinePolicy)>)
            ? policies
            : null;

    // QueueClientOptions.GetMessageDecodingFailedHandlers(): the caller's own handlers, which the library's client keeps
    // raising, as it did when it was built from the caller's options.
    private static readonly MethodInfo? DecodingFailedHandlers =
        typeof(QueueClientOptions).GetMethod("GetMessageDecodingFailedHandlers", NonPublicInstance, Type.EmptyTypes) is { } handlers &&
        handlers.ReturnType == typeof(SyncAsyncEventHandler<QueueMessageDecodingFailedEventArgs>)
            ? handlers
            : null;

    /// <summary>Whether the installed Azure SDK still has every member the copy reads by reflection.</summary>
    internal static bool IsSupported => Policies is not null && DecodingFailedHandlers is not null;

    /// <exception cref="NotSupportedException">
    /// The installed Azure SDK no longer has a member the copy needs, or <paramref name="source"/> is a subclass.
    /// </exception>
    public static QueueClientOptions Copy(QueueClientOptions source)
    {
        if (!IsSupported)
            throw new NotSupportedException("The installed Azure.Core or Azure.Storage.Queues no longer exposes the members needed to copy QueueClientOptions faithfully.");

        // A subclass can change settings the copy can't see (such as the protected application id length limit), and the
        // SDK names the options type's assembly in its User-Agent and telemetry, so a base-type copy wouldn't be faithful.
        if (source.GetType() != typeof(QueueClientOptions) && source.GetType() != typeof(SpecializedQueueClientOptions))
            throw new NotSupportedException($"{source.GetType()} derives from QueueClientOptions, so it can't be copied faithfully.");

        // The service version is a constructor argument; client-side encryption is public only on the specialized type.
        var copy = source is SpecializedQueueClientOptions specialized
            ? new SpecializedQueueClientOptions(source.Version) { ClientSideEncryption = specialized.ClientSideEncryption }
            : new QueueClientOptions(source.Version);

        copy.MessageEncoding = source.MessageEncoding;
        copy.Audience = source.Audience;
        copy.EnableTenantDiscovery = source.EnableTenantDiscovery;
        copy.GeoRedundantSecondaryUri = source.GeoRedundantSecondaryUri;
        copy.RetryPolicy = source.RetryPolicy;
        // Copied even when the caller never set one: the storage SDK doesn't pass transport options, so it never asks
        // whether a transport was set, and the caller's options keep the default transport they started with.
        copy.Transport = source.Transport;

        copy.Retry.Mode = source.Retry.Mode;
        copy.Retry.MaxRetries = source.Retry.MaxRetries;
        copy.Retry.Delay = source.Retry.Delay;
        copy.Retry.MaxDelay = source.Retry.MaxDelay;
        copy.Retry.NetworkTimeout = source.Retry.NetworkTimeout;

        var diagnostics = copy.Diagnostics;
        diagnostics.IsLoggingEnabled = source.Diagnostics.IsLoggingEnabled;
        diagnostics.IsDistributedTracingEnabled = source.Diagnostics.IsDistributedTracingEnabled;
        diagnostics.IsTelemetryEnabled = source.Diagnostics.IsTelemetryEnabled;
        diagnostics.IsLoggingContentEnabled = source.Diagnostics.IsLoggingContentEnabled;
        diagnostics.LoggedContentSizeLimit = source.Diagnostics.LoggedContentSizeLimit;
        diagnostics.ApplicationId = source.Diagnostics.ApplicationId;
        Replace(diagnostics.LoggedHeaderNames, source.Diagnostics.LoggedHeaderNames);
        Replace(diagnostics.LoggedQueryParameters, source.Diagnostics.LoggedQueryParameters);

        // New options start with ClientOptions.Default's policies, which the caller's list already has (the caller's
        // options started the same way). Start empty so none run twice, then add the caller's in their order.
        ((List<(HttpPipelinePosition, HttpPipelinePolicy)>?)Policies!.GetValue(copy))?.Clear();
        if (Policies.GetValue(source) is List<(HttpPipelinePosition Position, HttpPipelinePolicy Policy)> callerPolicies)
        {
            foreach (var (position, policy) in callerPolicies.ToArray())
                copy.AddPolicy(policy, position);
        }

        if (DecodingFailedHandlers!.Invoke(source, null) is SyncAsyncEventHandler<QueueMessageDecodingFailedEventArgs> callerHandlers)
            copy.MessageDecodingFailed += callerHandlers;

        return copy;
    }

    private static void Replace(IList<string> target, IList<string> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item);
    }
}
