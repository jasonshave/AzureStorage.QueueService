using System.Diagnostics;

namespace AzureStorage.QueueService.Telemetry;

internal static class ActivityExtensions
{
    /// <summary>
    /// Starts an activity with the given tags, parented to <paramref name="parent"/>, or to <see cref="Activity.Current"/>
    /// when that is the default. Takes the populated list by value: <see cref="TagList"/> is a struct, so the earlier
    /// callback-based version mutated a copy and every span was emitted without its tags.
    /// </summary>
    public static Activity? StartActivityWithTags(this ActivitySource activitySource, string name, ActivityKind kind, in TagList tags, ActivityContext parent = default, IEnumerable<ActivityLink>? links = null) =>
        activitySource.StartActivity(name, kind, parent, tags, links);

    /// <summary>
    /// Records a failed operation: the exception as an event, <c>error.type</c> as the conventions require, and an
    /// error status so trace views count the span as failed rather than only annotated.
    /// </summary>
    public static void RecordFailure(this Activity? activity, Exception exception)
    {
        if (activity is null) return;
        activity.AddException(exception);
        activity.SetTag(QueueServiceDiagnostics.Messaging.ErrorType, exception.GetType().FullName);
        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
    }
}
