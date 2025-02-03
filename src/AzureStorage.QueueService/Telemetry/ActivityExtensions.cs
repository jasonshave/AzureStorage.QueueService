using System.Diagnostics;

namespace AzureStorage.QueueService.Telemetry;

public static class ActivityExtensions
{
    public static Activity? StartActivityWithTags(this ActivitySource activitySource, string name,
        Action<TagList> tags)
    {
        var tagList = new TagList();
        tags(tagList);
        return activitySource.StartActivity(name, ActivityKind.Internal,
            Activity.Current?.Context ?? new ActivityContext(), tagList);
    }
}
