using Microsoft.Extensions.Options;

namespace Rasel.Application.Common;

/// <summary>Ban-risk protection: send pacing per WhatsApp session and a per-recipient daily cap.</summary>
public sealed class PacingOptions
{
    /// <summary>Random pause before a send that follows another send on the same session (inclusive bounds).</summary>
    public int MinDelayMs { get; set; } = 1000;
    public int MaxDelayMs { get; set; } = 5000;

    /// <summary>Minimum time between two bulk messages on the same session.</summary>
    public int MinGapMs { get; set; } = 1500;

    /// <summary>Messages per client and recipient number per UTC day.</summary>
    public int DailyPerRecipient { get; set; } = 5;
}

public sealed class PacingOptionsValidator : IValidateOptions<PacingOptions>
{
    public ValidateOptionsResult Validate(string? name, PacingOptions o)
    {
        var errors = new List<string>();
        if (o.MinDelayMs < 0 || o.MaxDelayMs < o.MinDelayMs) errors.Add("Pacing:MaxDelayMs must be >= MinDelayMs >= 0.");
        if (o.MaxDelayMs > 60_000) errors.Add("Pacing:MaxDelayMs must not exceed 60000.");
        if (o.MinGapMs < 0) errors.Add("Pacing:MinGapMs must be >= 0.");
        if (o.DailyPerRecipient < 1) errors.Add("Pacing:DailyPerRecipient must be at least 1.");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
