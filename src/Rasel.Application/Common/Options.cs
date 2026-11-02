using System.Security.Cryptography;
using System.Text;

namespace Rasel.Application.Common;

public sealed class OtpOptions
{
    public int TtlSeconds { get; set; } = 300;
    public int MaxAttempts { get; set; } = 5;
    public int ResendCooldownSeconds { get; set; } = 60;
    public int PerPhonePerHour { get; set; } = 5;
    public int PerClientPerMinute { get; set; } = 60;
}

public static class ApiKeyHasher
{
    /// <summary>API keys are high-entropy random values, so a plain SHA-256 is sufficient and allows indexed lookup.</summary>
    public static string Hash(string apiKey) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
}
