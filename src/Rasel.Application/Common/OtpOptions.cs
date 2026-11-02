namespace Rasel.Application.Common;

public sealed class OtpOptions
{
    public int TtlSeconds { get; set; } = 300;
    public int MaxAttempts { get; set; } = 5;
    public int CodeLength { get; set; } = 6;
    public int ResendCooldownSeconds { get; set; } = 60;
    public int PerPhonePerHour { get; set; } = 5;
    public int PerClientPerMinute { get; set; } = 60;
}
