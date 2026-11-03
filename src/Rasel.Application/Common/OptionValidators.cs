using Microsoft.Extensions.Options;

namespace Rasel.Application.Common;

public sealed class OtpOptionsValidator : IValidateOptions<OtpOptions>
{
    public ValidateOptionsResult Validate(string? name, OtpOptions o)
    {
        var errors = new List<string>();
        if (o.TtlSeconds is < 30 or > 3600) errors.Add("Otp:TtlSeconds must be between 30 and 3600.");
        if (o.MaxAttempts is < 1 or > 10) errors.Add("Otp:MaxAttempts must be between 1 and 10.");
        if (o.CodeLength is < 4 or > 9) errors.Add("Otp:CodeLength must be between 4 and 9.");
        if (o.ResendCooldownSeconds < 0 || o.ResendCooldownSeconds >= o.TtlSeconds) errors.Add("Otp:ResendCooldownSeconds must be >= 0 and below TtlSeconds.");
        if (o.PerPhonePerHour < 1) errors.Add("Otp:PerPhonePerHour must be at least 1.");
        if (o.PerClientPerMinute < 1) errors.Add("Otp:PerClientPerMinute must be at least 1.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

public sealed class OutboxOptionsValidator : IValidateOptions<OutboxOptions>
{
    public ValidateOptionsResult Validate(string? name, OutboxOptions o)
    {
        var errors = new List<string>();
        if (o.PollIntervalMs < 1) errors.Add("Outbox:PollIntervalMs must be positive.");
        if (o.MaxAttempts is < 1 or > 20) errors.Add("Outbox:MaxAttempts must be between 1 and 20.");
        if (o.BaseBackoffSeconds < 0 || o.MaxBackoffSeconds < o.BaseBackoffSeconds) errors.Add("Outbox:MaxBackoffSeconds must be >= BaseBackoffSeconds >= 0.");
        if (o.LeaseSeconds < 5) errors.Add("Outbox:LeaseSeconds must be at least 5.");
        if (o.BatchSize is < 1 or > 100) errors.Add("Outbox:BatchSize must be between 1 and 100.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

public sealed class OtpTemplatesValidator : IValidateOptions<OtpOptions>
{
    public ValidateOptionsResult Validate(string? name, OtpOptions o)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(o.AppName)) errors.Add("Otp:AppName is required.");
        Check("TemplatesEn", o.TemplatesEn, errors);
        Check("TemplatesAr", o.TemplatesAr, errors);
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static void Check(string field, string[]? templates, List<string> errors)
    {
        if (templates is null) return;
        if (templates.Length == 0) errors.Add($"Otp:{field} must not be empty (omit it to use the defaults).");
        foreach (var t in templates)
            if (t is null || !t.Contains("{app}") || !t.Contains("{code}"))
                errors.Add($"Otp:{field}: every template must contain {{app}} and {{code}}.");
    }
}
