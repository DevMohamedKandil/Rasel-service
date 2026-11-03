using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Rasel.Infrastructure.Configuration;

public sealed class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, DatabaseOptions o) =>
        string.IsNullOrWhiteSpace(o.Path) ? ValidateOptionsResult.Fail("Database:Path is required.") : ValidateOptionsResult.Success;
}

/// <summary>Secrets must be long, and the committed dev placeholders are accepted only in the Development environment.</summary>
public sealed class SecurityOptionsValidator(IHostEnvironment env) : IValidateOptions<SecurityOptions>
{
    public const int MinSecretLength = 32;

    public ValidateOptionsResult Validate(string? name, SecurityOptions o)
    {
        var errors = new List<string>();
        Check(errors, "Security:OtpPepper", o.OtpPepper, env);
        Check(errors, "Security:TokenSecret", o.TokenSecret, env);
        if (o.OtpPepper.Length > 0 && o.OtpPepper == o.TokenSecret) errors.Add("Security:OtpPepper and Security:TokenSecret must differ.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void Check(List<string> errors, string key, string value, IHostEnvironment env)
    {
        if (string.IsNullOrEmpty(value)) errors.Add($"Missing configuration: {key}.");
        else if (value.Length < MinSecretLength) errors.Add($"{key} must be at least {MinSecretLength} characters.");
        else if (!env.IsDevelopment() && value.Contains("dev-only", StringComparison.OrdinalIgnoreCase))
            errors.Add($"{key} is still the development placeholder; set a real secret (environment variable) outside Development.");
    }
}

public sealed class WebhookOptionsValidator(IHostEnvironment env) : IValidateOptions<WebhookOptions>
{
    public ValidateOptionsResult Validate(string? name, WebhookOptions o)
    {
        if (!o.Enabled) return ValidateOptionsResult.Success;
        var errors = new List<string>();
        if (!Uri.TryCreate(o.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            errors.Add("Webhooks:Url must be an absolute http(s) URL when webhooks are enabled.");
        else if (uri.Scheme == "http" && !env.IsDevelopment() && !uri.IsLoopback)
            errors.Add("Webhooks:Url must use https outside Development.");
        if (o.Secret.Length < 16) errors.Add("Webhooks:Secret must be at least 16 characters when webhooks are enabled.");
        else if (!env.IsDevelopment() && o.Secret.Contains("dev-only", StringComparison.OrdinalIgnoreCase))
            errors.Add("Webhooks:Secret is still the development placeholder.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

public sealed class WhatsAppOptionsValidator : IValidateOptions<WhatsAppOptions>
{
    public ValidateOptionsResult Validate(string? name, WhatsAppOptions o)
    {
        if (o.Provider.Equals(WhatsAppOptions.FakeProvider, StringComparison.OrdinalIgnoreCase)) return ValidateOptionsResult.Success;
        if (o.Provider.Equals(WhatsAppOptions.BridgeProvider, StringComparison.OrdinalIgnoreCase)) return ValidateBridge(o.Bridge);
        if (!o.Provider.Equals(WhatsAppOptions.WebJsProvider, StringComparison.OrdinalIgnoreCase))
            return ValidateOptionsResult.Fail("WhatsApp:Provider must be Fake, Bridge or WebJs.");

        var w = o.WebJs;
        var errors = new List<string>();
        if (!Uri.TryCreate(w.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            errors.Add("WhatsApp:WebJs:BaseUrl must be an absolute http(s) URL.");
        if (string.IsNullOrWhiteSpace(w.ApiToken)) errors.Add("WhatsApp:WebJs:ApiToken is required.");
        if (w.AttemptTimeoutSeconds is < 1 or > 120) errors.Add("WhatsApp:WebJs:AttemptTimeoutSeconds must be between 1 and 120.");
        if (w.ReadRetries is < 0 or > 5) errors.Add("WhatsApp:WebJs:ReadRetries must be between 0 and 5.");
        if (w.BreakerMinimumThroughput < 2) errors.Add("WhatsApp:WebJs:BreakerMinimumThroughput must be at least 2.");
        if (w.BreakerFailureRatio is <= 0 or > 1) errors.Add("WhatsApp:WebJs:BreakerFailureRatio must be in (0, 1].");
        if (w.BreakerSamplingSeconds < 1) errors.Add("WhatsApp:WebJs:BreakerSamplingSeconds must be positive.");
        if (w.BreakerBreakSeconds < 1) errors.Add("WhatsApp:WebJs:BreakerBreakSeconds must be positive.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static ValidateOptionsResult ValidateBridge(BridgeOptions b)
    {
        var errors = new List<string>();
        if (!Uri.TryCreate(b.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            errors.Add("WhatsApp:Bridge:BaseUrl must be an absolute http(s) URL.");
        else if (!uri.IsLoopback && uri.Scheme == "http" && b.SharedToken.Length < 16)
            errors.Add("WhatsApp:Bridge:BaseUrl must be a loopback address or https, or plain http with a WhatsApp:Bridge:SharedToken of at least 16 characters.");
        if (b.AttemptTimeoutSeconds is < 1 or > 120) errors.Add("WhatsApp:Bridge:AttemptTimeoutSeconds must be between 1 and 120.");
        if (b.ReadRetries is < 0 or > 5) errors.Add("WhatsApp:Bridge:ReadRetries must be between 0 and 5.");
        if (b.BreakerMinimumThroughput < 2) errors.Add("WhatsApp:Bridge:BreakerMinimumThroughput must be at least 2.");
        if (b.BreakerFailureRatio is <= 0 or > 1) errors.Add("WhatsApp:Bridge:BreakerFailureRatio must be in (0, 1].");
        if (b.BreakerSamplingSeconds < 1) errors.Add("WhatsApp:Bridge:BreakerSamplingSeconds must be positive.");
        if (b.BreakerBreakSeconds < 1) errors.Add("WhatsApp:Bridge:BreakerBreakSeconds must be positive.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
