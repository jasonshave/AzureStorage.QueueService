using Microsoft.AspNetCore.Http.HttpResults;

namespace AzureStorage.QueueService.Tests.Server;

public static class Endpoints
{
    public static IEndpointRouteBuilder MapEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/send", SendMessage);

        return builder;
    }

    // Round-trips a message through the real queue (Azurite in CI) using the 3.0 API: send, receive, complete.
    public static async ValueTask<Results<Ok<Person>, BadRequest>> SendMessage(Person input, IStorageQueue queue)
    {
        await queue.SendAsync(input);

        Person? response = null;
        foreach (var message in await queue.ReceiveAsync<Person>(maxMessages: 10))
        {
            if (message.Error is null) response = message.Body;
            await message.CompleteAsync();
        }

        if (response is not null)
            return TypedResults.Ok(response);

        return TypedResults.BadRequest();
    }
}
