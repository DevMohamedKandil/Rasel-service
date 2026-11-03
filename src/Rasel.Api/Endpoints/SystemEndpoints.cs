using Microsoft.Extensions.Options;
using Rasel.Api.Security;
using Rasel.Application.Common;
using Rasel.Domain.Enums;
using Rasel.Application.Ports;
using Rasel.Infrastructure.Configuration;

namespace Rasel.Api.Endpoints;

/// <param name="Sidecar">Aggregate state (shared preparation, or the best state across sessions).</param>
/// <param name="Scope">Scope of the calling key (Standard or Admin), so a UI can show or disable admin-only controls. The server enforces it either way.</param>
/// <param name="Sessions">One entry per supervised session (Managed mode). Admin keys only; empty for Standard keys.</param>
public sealed record SystemStatusDto(string Provider, SidecarStateDto Sidecar, IReadOnlyList<SessionEngineDto> Sessions, string Scope = "Admin");

public sealed record SidecarStateDto(SidecarState State, string? Step, string? Reason, int Crashes, bool AutoStart);

public sealed record SessionEngineDto(Guid SessionId, SidecarState State, string? Step, string? Reason, int Crashes);

public static class SystemEndpoints
{
    private static SystemStatusDto Snapshot(ISidecarControl control, IOptions<WhatsAppOptions> whatsApp, bool detail = true)
    {
        var s = control.Status;
        // Step and Reason are engine internals (failure text) and session ids are not for every client: Admin keys only.
        var sessions = detail
            ? control.Sessions.Select(x => new SessionEngineDto(x.SessionId, x.Status.State, x.Status.Step, x.Status.Reason, x.Status.Crashes)).ToList()
            : [];
        return new SystemStatusDto(whatsApp.Value.Provider, new SidecarStateDto(s.State, detail ? s.Step : null, detail ? s.Reason : null, s.Crashes, s.AutoStart), sessions, detail ? nameof(ApiKeyScope.Admin) : nameof(ApiKeyScope.Standard));
    }

    public static void MapSystemEndpoints(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/system").WithTags("System");

        group.MapGet("/status", (ISidecarControl control, IOptions<WhatsAppOptions> whatsApp, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(Snapshot(control, whatsApp, http.User.HasClaim(ApiKeyDefaults.ScopeClaim, nameof(ApiKeyScope.Admin))));
        }).RequireStandard();

        // Idempotent: 202 when the request changes something, 200 when already in that state, 409 when the sidecar is not managed.
        // Safest rule: the caller must name one session (sessionId) or say all=true; a bare call never touches every number.
        IResult Command(Func<ISidecarControl, Guid?, SidecarCommandResult> act, Guid? sessionId, bool? all, ISidecarControl control, IOptions<WhatsAppOptions> whatsApp)
        {
            if (sessionId is null && all != true)
                throw new AppException(ErrorKind.BadRequest, "session_required", "Pass sessionId=<guid> for one session, or all=true for every session.");
            if (sessionId is not null && all == true)
                throw new AppException(ErrorKind.BadRequest, "session_ambiguous", "Pass either sessionId or all=true, not both.");
            return act(control, sessionId) switch
            {
                SidecarCommandResult.Accepted => Results.Json(Snapshot(control, whatsApp), statusCode: StatusCodes.Status202Accepted),
                SidecarCommandResult.AlreadyInState => Results.Ok(Snapshot(control, whatsApp)),
                SidecarCommandResult.SessionNotFound => throw new AppException(ErrorKind.NotFound, "session_not_found", "No supervised session with this id."),
                _ => throw new AppException(ErrorKind.Conflict, "sidecar_not_managed", "The WhatsApp engine is not managed by Rasel (WhatsApp:Provider is not Managed)."),
            };
        }

        group.MapPost("/sidecar/start", (Guid? sessionId, bool? all, ISidecarControl c, IOptions<WhatsAppOptions> w) => Command((x, id) => x.RequestStart(id), sessionId, all, c, w)).RequireAdmin();
        group.MapPost("/sidecar/stop", (Guid? sessionId, bool? all, ISidecarControl c, IOptions<WhatsAppOptions> w) => Command((x, id) => x.RequestStop(id), sessionId, all, c, w)).RequireAdmin();
        group.MapPost("/sidecar/restart", (Guid? sessionId, bool? all, ISidecarControl c, IOptions<WhatsAppOptions> w) => Command((x, id) => x.RequestRestart(id), sessionId, all, c, w)).RequireAdmin();
    }
}
