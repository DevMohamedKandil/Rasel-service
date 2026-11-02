using Microsoft.Extensions.Options;
using Rasel.Application.Common;
using Rasel.Application.Ports;

namespace Rasel.Api.Security;

/// <summary>Per-API-key request quota. Runs after authentication; anonymous requests (health) are not counted.</summary>
public sealed class ClientRateLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IRateLimiter limiter, IOptions<ApiOptions> options)
    {
        if (context.Items.ContainsKey(ApiKeyDefaults.ClientItem)
            && !limiter.TryAcquire($"api:{context.GetClient().Id}", options.Value.RequestsPerMinute, TimeSpan.FromMinutes(1)))
            throw new AppException(ErrorKind.TooManyRequests, "rate_limited", "Request rate limit exceeded for this API key.", 60);

        await next(context);
    }
}
