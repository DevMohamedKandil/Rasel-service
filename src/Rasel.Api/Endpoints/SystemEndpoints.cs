using Microsoft.Extensions.Options;
using Rasel.Api.Security;
using Rasel.Application.Common;
using Rasel.Domain.Enums;
using Rasel.Application.Ports;
using Rasel.Infrastructure.Configuration;

namespace Rasel.Api.Endpoints;

public sealed record SystemStatusDto(string Provider, SidecarStateDto Sidecar);

public sealed record SidecarStateDto(SidecarState State, string? Step, string? Reason, int Crashes, bool AutoStart);

public static class SystemEndpoints
{
    private static SystemStatusDto Snapshot(ISidecarControl control, IOptions<WhatsAppOptions> whatsApp, bool detail = true)
    {
        var s = control.Status;
        // Step and Reason are engine internals (failure text): Admin keys only.
        return new SystemStatusDto(whatsApp.Value.Provider, new SidecarStateDto(s.State, detail ? s.Step : null, detail ? s.Reason : null, s.Crashes, s.AutoStart));
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
        IResult Command(Func<ISidecarControl, SidecarCommandResult> act, ISidecarControl control, IOptions<WhatsAppOptions> whatsApp) => act(control) switch
        {
            SidecarCommandResult.Accepted => Results.Json(Snapshot(control, whatsApp), statusCode: StatusCodes.Status202Accepted),
            SidecarCommandResult.AlreadyInState => Results.Ok(Snapshot(control, whatsApp)),
            _ => throw new AppException(ErrorKind.Conflict, "sidecar_not_managed", "The WhatsApp engine is not managed by Rasel (WhatsApp:Provider is not Managed)."),
        };

        group.MapPost("/sidecar/start", (ISidecarControl c, IOptions<WhatsAppOptions> w) => Command(x => x.RequestStart(), c, w)).RequireAdmin();
        group.MapPost("/sidecar/stop", (ISidecarControl c, IOptions<WhatsAppOptions> w) => Command(x => x.RequestStop(), c, w)).RequireAdmin();
        group.MapPost("/sidecar/restart", (ISidecarControl c, IOptions<WhatsAppOptions> w) => Command(x => x.RequestRestart(), c, w)).RequireAdmin();
    }
}
