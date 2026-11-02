using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Domain.Entities;

namespace Rasel.Api;

public sealed class ApiKeyMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Api-Key";
    private const string ItemKey = "rasel.client";

    public async Task InvokeAsync(HttpContext context, IApiClientRepository clients)
    {
        if (!context.Request.Path.StartsWithSegments("/v1"))
        {
            await next(context);
            return;
        }

        var key = context.Request.Headers[HeaderName].ToString();
        var client = key.Length == 0 ? null : await clients.FindByKeyHashAsync(ApiKeyHasher.Hash(key), context.RequestAborted);
        if (client is not { Active: true })
        {
            await Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Unauthorized",
                detail: "A valid X-Api-Key header is required.").ExecuteAsync(context);
            return;
        }

        context.Items[ItemKey] = client;
        await next(context);
    }

    public static ApiClient GetClient(HttpContext context) => (ApiClient)context.Items[ItemKey]!;
}
