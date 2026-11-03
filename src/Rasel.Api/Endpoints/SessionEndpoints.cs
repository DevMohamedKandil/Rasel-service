using Rasel.Api.Security;
using Rasel.Application.UseCases;

namespace Rasel.Api.Endpoints;

public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/sessions").WithTags("Sessions");

        group.MapPost("/", async (CreateSessionRequest body, HttpContext http, CreateSessionHandler handler, CancellationToken ct) =>
        {
            var session = await handler.HandleAsync(http.GetClient().Id, body.Name, ct);
            return Results.Created($"/v1/sessions/{session.Id}", session);
        }).RequireStandard();

        group.MapGet("/{id:guid}", async (Guid id, HttpContext http, GetSessionStatusHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(http.GetClient().Id, id, ct))).RequireStandard();

        group.MapGet("/{id:guid}/qr", async (Guid id, HttpContext http, GetSessionQrHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(http.GetClient().Id, id, ct))).RequireStandard();

        // Full number: separate endpoint on purpose. Client-scoped (404 for other clients' sessions), never cached.
        group.MapGet("/{id:guid}/linked-number", async (Guid id, HttpContext http, GetLinkedNumberHandler handler, CancellationToken ct) =>
        {
            var dto = await handler.HandleAsync(http.GetClient().Id, id, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(dto);
        }).RequireStandard();

        group.MapDelete("/{id:guid}", async (Guid id, bool? purge, HttpContext http, StopSessionHandler handler, CancellationToken ct) =>
        {
            await handler.HandleAsync(http.GetClient().Id, id, ct, purge == true);
            return Results.NoContent();
        }).RequireAdmin(); // stopping a session disconnects the WhatsApp number: Admin only
    }
}
