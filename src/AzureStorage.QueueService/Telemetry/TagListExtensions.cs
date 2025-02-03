using System.Diagnostics;

namespace AzureStorage.QueueService.Telemetry;

public static class TagListExtensions
{
    public static TagList ToTagList(this (string Key, object? Value)[] tags)
    {
        var tagList = new TagList();
        foreach (var (key, value) in tags)
        {
            tagList.Add(key, value);
        }
        return tagList;
    }
}
