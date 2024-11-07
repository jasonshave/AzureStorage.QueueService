using Microsoft.AspNetCore.Http.HttpResults;

namespace AzureStorage.QueueService.Tests.Server;

public static class Endpoints
{
    public static IEndpointRouteBuilder MapEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/send", SendMessage);

        return builder;
    }

    public static async ValueTask<Ok> SendMessage(string input, IQueueClientFactory clientFactory)
    {
        var client = clientFactory.GetQueueClient();
        await client.SendMessageAsync(input);
        return TypedResults.Ok();
    }
}