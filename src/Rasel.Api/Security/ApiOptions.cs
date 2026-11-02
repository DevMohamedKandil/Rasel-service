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
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
