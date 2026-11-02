using Rasel.Application.UseCases;

namespace Rasel.Api.Endpoints;

public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/sessions").WithTags("Sessions");

        group.MapPost("/", async (CreateSessionRequest body, HttpContext http, CreateSessionHandler handler, CancellationToken ct) =>
        {
            var session = await handler.HandleAsync(ApiKeyMiddleware.GetClient(http).Id, body.Name, ct);
            return Results.Created($"/v1/sessions/{session.Id}", session);
        });

        group.MapGet("/{id:guid}", async (Guid id, HttpContext http, GetSessionStatusHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(ApiKeyMiddleware.GetClient(http).Id, id, ct)));

        group.MapGet("/{id:guid}/qr", async (Guid id, HttpContext http, GetSessionQrHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(ApiKeyMiddleware.GetClient(http).Id, id, ct)));

        group.MapDelete("/{id:guid}", async (Guid id, HttpContext http, StopSessionHandler handler, CancellationToken ct) =>
        {
            await handler.HandleAsync(ApiKeyMiddleware.GetClient(http).Id, id, ct);
            return Results.NoContent();
        });
    }
}
