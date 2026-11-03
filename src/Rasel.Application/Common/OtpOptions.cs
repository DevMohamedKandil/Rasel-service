namespace Rasel.Application.Common;

public sealed class OtpOptions
{
    public int TtlSeconds { get; set; } = 300;
    public int MaxAttempts { get; set; } = 5;
    public int CodeLength { get; set; } = 6;
    public int ResendCooldownSeconds { get; set; } = 60;
    public int PerPhonePerHour { get; set; } = 5;
    public int PerClientPerMinute { get; set; } = 60;

    /// <summary>Name shown in the message text.</summary>
    public string AppName { get; set; } = "Rasel";

    /// <summary>Override the built-in variants. Placeholders: {app}, {code}, {minutes}. Each must contain {app} and {code}.</summary>
    public string[]? TemplatesEn { get; set; }
    public string[]? TemplatesAr { get; set; }
}
