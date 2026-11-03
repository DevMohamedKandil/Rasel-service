using Microsoft.Extensions.Options;

namespace Rasel.Api.Security;

public sealed class ApiOptions
{
    /// <summary>Authenticated requests allowed per API key per minute (all endpoints combined).</summary>
    public int RequestsPerMinute { get; set; } = 600;

    /// <summary>Failed API-key attempts allowed per client IP per minute before that IP is throttled.</summary>
    public int FailedAuthPerMinute { get; set; } = 20;

    public int MaxKeyLength { get; set; } = 128;

    public int MaxRequestBodyBytes { get; set; } = 64 * 1024;

    public int ShutdownTimeoutSeconds { get; set; } = 30;

    /// <summary>How long a rotated key keeps working next to its replacement, so clients can switch over. 0 revokes it immediately. A rotate request may override it.</summary>
    public int RotationGraceSeconds { get; set; }

    /// <summary>
    /// Trust X-Forwarded-For/-Proto from the (single) reverse proxy in front of the API, so the per-IP failed-auth throttle sees the
    /// real client instead of the proxy. Enable ONLY when a proxy always overwrites the header and the API port is not reachable directly.
    /// </summary>
    public bool TrustForwardedHeaders { get; set; }
}

public sealed class ApiOptionsValidator : IValidateOptions<ApiOptions>
{
    public ValidateOptionsResult Validate(string? name, ApiOptions o)
    {
        var errors = new List<string>();
        if (o.RequestsPerMinute < 1) errors.Add("Api:RequestsPerMinute must be at least 1.");
        if (o.FailedAuthPerMinute < 1) errors.Add("Api:FailedAuthPerMinute must be at least 1.");
        if (o.MaxKeyLength is < 16 or > 512) errors.Add("Api:MaxKeyLength must be between 16 and 512.");
        if (o.MaxRequestBodyBytes is < 1024 or > 10 * 1024 * 1024) errors.Add("Api:MaxRequestBodyBytes must be between 1 KiB and 10 MiB.");
        if (o.ShutdownTimeoutSeconds is < 1 or > 300) errors.Add("Api:ShutdownTimeoutSeconds must be between 1 and 300.");
        if (o.RotationGraceSeconds is < 0 or > Rasel.Application.UseCases.KeyManagementHandler.MaxGraceSeconds) errors.Add("Api:RotationGraceSeconds must be between 0 and 604800 (7 days).");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
