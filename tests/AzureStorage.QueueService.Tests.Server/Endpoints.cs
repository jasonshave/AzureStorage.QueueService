using Microsoft.AspNetCore.Http.HttpResults;

namespace AzureStorage.QueueService.Tests.Server;

public static class Endpoints
{
    public static IEndpointRouteBuilder MapEndpoints(this IEndpointRouteBuilder builder)
    {
        builder.MapPost("/send", SendMessage);

        return builder;
    }

    public static async ValueTask<Results<Ok<Person>, BadRequest>> SendMessage(Person input, IQueueClientFactory clientFactory)
    {
        var client = clientFactory.GetQueueClient();
        await client.SendMessageAsync(input);

        await Task.Delay(1000);

        Person? response = null;
        await client.ReceiveMessagesAsync<Person>(HandleMessage, HandleException, 10);

        async ValueTask HandleMessage(Person? message, IDictionary<string, string>? metadata)
        {
            response = message;
        }

        async ValueTask HandleException(Exception ex, IDictionary<string, string>? metadata)
        {
        }

        if (response is not null)
            return TypedResults.Ok(response);

        return TypedResults.BadRequest();
    }
}