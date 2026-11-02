using Rasel.Application.UseCases;

namespace Rasel.Api.Endpoints;

public static class MessageEndpoints
{
    public static void MapMessageEndpoints(this RouteGroupBuilder v1) =>
        v1.MapPost("/messages", async (SendMessageRequest body, HttpContext http, SendTextHandler handler, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(
                ApiKeyMiddleware.GetClient(http), new SendTextCommand(body.SessionId, body.Phone, body.GroupName, body.Text), ct);
            return Results.Accepted($"/v1/messages/{result.MessageId}", result);
        }).WithTags("Messages");
}
