using Microsoft.AspNetCore.Mvc;
using Rasel.Api.Security;
using Rasel.Application.UseCases;

namespace Rasel.Api.Endpoints;

public static class MessageEndpoints
{
    public static void MapMessageEndpoints(this RouteGroupBuilder v1) =>
        v1.MapPost("/messages", async (SendMessageRequest body, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, HttpContext http, SendTextHandler handler, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(
                http.GetClient(), new SendTextCommand(body.SessionId, body.Phone, body.GroupName, body.Text, idempotencyKey), ct);
            if (result.Replayed) http.Response.Headers["Idempotent-Replayed"] = "true";
            return Results.Accepted($"/v1/messages/{result.MessageId}", result);
        }).WithTags("Messages");
}
