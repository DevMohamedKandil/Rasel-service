using Rasel.Application.UseCases;

namespace Rasel.Api.Endpoints;

public static class OtpEndpoints
{
    public static void MapOtpEndpoints(this RouteGroupBuilder v1)
    {
        var group = v1.MapGroup("/otp").WithTags("OTP");

        group.MapPost("/request", async (RequestOtpRequest body, HttpContext http, RequestOtpHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(ApiKeyMiddleware.GetClient(http), new RequestOtpCommand(body.Phone, body.Purpose, body.Channel), ct)));

        group.MapPost("/verify", async (VerifyOtpRequest body, HttpContext http, VerifyOtpHandler handler, CancellationToken ct) =>
            Results.Ok(await handler.HandleAsync(ApiKeyMiddleware.GetClient(http).Id, body.ChallengeId, body.Code, ct)));
    }
}
