using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Domain.Entities;

namespace Rasel.Api.Security;

public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    internal const string ClientItem = "rasel.client";
    internal const string ThrottledItem = "rasel.auth.throttled";

    /// <summary>The authenticated API client. Only valid on endpoints that require authentication.</summary>
    public static ApiClient GetClient(this HttpContext context) => (ApiClient)context.Items[ClientItem]!;
}

/// <summary>
/// API-key authentication. The key is hashed, looked up by hash and re-checked in constant time. Failed attempts are
/// counted per client IP; once an IP exceeds the limit it is answered with 429 before any lookup happens.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions, ILoggerFactory loggerFactory, UrlEncoder encoder,
    IApiClientRepository clients, IRateLimiter limiter, IOptions<ApiOptions> apiOptions, IProblemDetailsService problems)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyDefaults.HeaderName, out var values)) return AuthenticateResult.NoResult();

        var options = apiOptions.Value;
        var failureKey = $"auth-fail:{Context.Connection.RemoteIpAddress}";
        if (limiter.IsExceeded(failureKey, options.FailedAuthPerMinute, FailureWindow))
        {
            Context.Items[ApiKeyDefaults.ThrottledItem] = true;
            return AuthenticateResult.Fail("Too many failed attempts.");
        }

        var key = values.ToString();
        ApiClient? client = null;
        if (key.Length > 0 && key.Length <= options.MaxKeyLength)
            client = await clients.FindByKeyHashAsync(ApiKeyHasher.Hash(key), Context.RequestAborted);

        if (client is not { Active: true } || !ApiKeyHasher.Matches(key, client.ApiKeyHash))
        {
            limiter.TryAcquire(failureKey, options.FailedAuthPerMinute, FailureWindow);
            Logger.LogWarning("Rejected API key from {RemoteIp}", Context.Connection.RemoteIpAddress);
            return AuthenticateResult.Fail("Invalid API key.");
        }

        Context.Items[ApiKeyDefaults.ClientItem] = client;
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, client.Id.ToString()), new Claim(ClaimTypes.Name, client.Name)], Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var throttled = Context.Items.ContainsKey(ApiKeyDefaults.ThrottledItem);
        Response.StatusCode = throttled ? StatusCodes.Status429TooManyRequests : StatusCodes.Status401Unauthorized;
        if (throttled) Response.Headers.RetryAfter = "60";
        else Response.Headers.WWWAuthenticate = ApiKeyDefaults.Scheme;

        var code = throttled ? "auth_throttled" : "unauthorized";
        var problem = new ProblemDetails
        {
            Status = Response.StatusCode,
            Title = code,
            Detail = throttled ? "Too many failed authentication attempts." : "A valid X-Api-Key header is required.",
        };
        problem.Extensions["code"] = code;
        await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = Context, ProblemDetails = problem });
    }
}
