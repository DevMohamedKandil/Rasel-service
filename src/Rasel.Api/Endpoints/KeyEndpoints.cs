using Microsoft.Extensions.Options;
using Rasel.Api.Security;
using Rasel.Application.UseCases;

namespace Rasel.Api.Endpoints;

public sealed record CreateKeyRequest(string? Name, Rasel.Domain.Enums.ApiKeyScope? Scope, int? DailyMessageLimit = null);

public sealed record RotateKeyRequest(int? GraceSeconds = null);

/// <summary>Admin-only key management. Plaintext keys appear only in the create/rotate response, once, and are never cached.</summary>
public static class KeyEndpoints
{
    public static void MapKeyEndpoints(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/keys").WithTags("Keys");

        group.MapGet("/", async (HttpContext http, KeyManagementHandler handler, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await handler.ListAsync(ct));
        }).RequireAdmin();

        group.MapPost("/", async (CreateKeyRequest body, HttpContext http, KeyManagementHandler handler, CancellationToken ct) =>
        {
            var created = await handler.CreateAsync(http.GetClient().Id, new CreateKeyCommand(body.Name, body.Scope, body.DailyMessageLimit), ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Created($"/v1/keys/{created.Key.Id}", created);
        }).RequireAdmin();

        group.MapPost("/{id:guid}/revoke", async (Guid id, HttpContext http, KeyManagementHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.RevokeAsync(http.GetClient().Id, id, ct))).RequireAdmin();

        group.MapPost("/{id:guid}/rotate", async (Guid id, RotateKeyRequest? body, HttpContext http, KeyManagementHandler handler, IOptions<ApiOptions> options, CancellationToken ct) =>
        {
            var rotated = await handler.RotateAsync(http.GetClient().Id, id, body?.GraceSeconds ?? options.Value.RotationGraceSeconds, ct);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(rotated);
        }).RequireAdmin();
    }
}
