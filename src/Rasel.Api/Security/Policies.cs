using Microsoft.AspNetCore.Authorization;
using Rasel.Domain.Enums;

namespace Rasel.Api.Security;

/// <summary>Authorization policies. Every /v1 endpoint must declare one explicitly (enforced by a test); Admin implies Standard.</summary>
public static class Policies
{
    public const string StandardPolicy = nameof(RequireStandard);
    public const string AdminPolicy = nameof(RequireAdmin);

    public static void AddRaselPolicies(this AuthorizationOptions o)
    {
        o.AddPolicy(StandardPolicy, p => p.AddAuthenticationSchemes(ApiKeyDefaults.Scheme).RequireAuthenticatedUser()
            .RequireClaim(ApiKeyDefaults.ScopeClaim, nameof(ApiKeyScope.Standard), nameof(ApiKeyScope.Admin)));
        o.AddPolicy(AdminPolicy, p => p.AddAuthenticationSchemes(ApiKeyDefaults.Scheme).RequireAuthenticatedUser()
            .RequireClaim(ApiKeyDefaults.ScopeClaim, nameof(ApiKeyScope.Admin)));
    }

    public static TBuilder RequireStandard<TBuilder>(this TBuilder b) where TBuilder : IEndpointConventionBuilder => b.RequireAuthorization(StandardPolicy);

    public static TBuilder RequireAdmin<TBuilder>(this TBuilder b) where TBuilder : IEndpointConventionBuilder => b.RequireAuthorization(AdminPolicy);
}
