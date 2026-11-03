using Microsoft.Extensions.Options;

namespace Rasel.Api.Security;

/// <summary>Browser access (for the admin UI). Empty list = CORS disabled: no CORS headers are ever sent.</summary>
public sealed class CorsOptions
{
    public string[] AllowedOrigins { get; set; } = [];
}

public sealed class CorsOptionsValidator : IValidateOptions<CorsOptions>
{
    public ValidateOptionsResult Validate(string? name, CorsOptions o)
    {
        var errors = new List<string>();
        foreach (var origin in o.AllowedOrigins.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            if (origin.Trim() == "*") errors.Add("Cors:AllowedOrigins must list explicit origins; '*' is not allowed.");
            else if (!Uri.TryCreate(origin, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https") || u.PathAndQuery != "/" || origin.EndsWith('/'))
                errors.Add($"Cors:AllowedOrigins entry '{origin}' must be an origin like https://admin.example.com (scheme, host, optional port; no path or trailing slash).");
        }
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
