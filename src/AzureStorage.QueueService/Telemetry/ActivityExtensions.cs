using System.Diagnostics;

namespace AzureStorage.QueueService.Telemetry;

internal static class ActivityExtensions
{
    /// <summary>
    /// Starts an activity with the given tags, parented to <see cref="Activity.Current"/> if there is one.
    /// Takes the populated list by value: <see cref="TagList"/> is a struct, so the earlier callback-based
    /// version mutated a copy and every span was emitted without its tags.
    /// </summary>
    public static Activity? StartActivityWithTags(this ActivitySource activitySource, string name, ActivityKind kind, in TagList tags) =>
        activitySource.StartActivity(name, kind, parentId: null, tags: tags);
}
