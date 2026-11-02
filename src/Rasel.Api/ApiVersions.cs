namespace Rasel.Api;

/// <summary>
/// URL-segment versioning. A breaking change ships as a new group (<c>/v2</c>) next to the old one; every versioned
/// response carries <c>X-Api-Version</c> so clients and logs can tell which contract answered.
/// </summary>
public static class ApiVersions
{
    public const string V1 = "v1";
    public const string HeaderName = "X-Api-Version";

    public static RouteGroupBuilder MapVersion(this IEndpointRouteBuilder app, string version)
    {
        var group = app.MapGroup($"/{version}");
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers[HeaderName] = version;
            return await next(context);
        });
        return group;
    }
}
