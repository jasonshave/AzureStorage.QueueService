using Microsoft.Extensions.DependencyInjection;

namespace AzureStorage.QueueService;

/// <summary>
/// A handle for one registered client, returned by <c>AddClient</c> and <c>AddDefaultClient</c>. Extension methods
/// hang off it (<see cref="QueueClientBuilderExtensions"/>), the way they do off <c>IHttpClientBuilder</c>, so
/// features can be attached to one specific client — including by other packages.
/// </summary>
public interface IQueueClientBuilder
{
    /// <summary>The registered name; <see cref="Microsoft.Extensions.Options.Options.DefaultName"/> for the default client.</summary>
    string Name { get; }

    IServiceCollection Services { get; }
}

internal sealed class QueueClientBuilder : IQueueClientBuilder
{
    public QueueClientBuilder(string name, IServiceCollection services)
    {
        Name = name;
        Services = services;
    }

    public string Name { get; }

    public IServiceCollection Services { get; }
}
