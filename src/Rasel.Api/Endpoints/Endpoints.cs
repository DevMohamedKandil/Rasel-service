using Rasel.Application.UseCases;

namespace Rasel.Api.Endpoints;

public sealed record CreateSessionRequest(string? Name);

public sealed record SendMessageRequest(Guid? SessionId, string? Phone, string? GroupName, string? Text);

public sealed record RequestOtpRequest(string? Phone, string? Purpose, string? Channel);

public sealed record VerifyOtpRequest(Guid ChallengeId, string? Code);

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
