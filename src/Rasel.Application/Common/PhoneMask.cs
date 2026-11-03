namespace Rasel.Application.Common;

public static class PhoneMask
{
    /// <summary>"201000000880" -> "+20100*****880": first 5 and last 3 digits, the rest hidden. Short numbers keep only the last 2.</summary>
    public static string? Mask(string? digits)
    {
        if (string.IsNullOrWhiteSpace(digits)) return null;
        var d = new string(digits.Where(char.IsAsciiDigit).ToArray());
        if (d.Length == 0) return null;
        if (d.Length < 9) return "+" + new string('*', Math.Max(1, d.Length - 2)) + d[^Math.Min(2, d.Length)..];
        return "+" + d[..5] + new string('*', d.Length - 8) + d[^3..];
    }

    public static string? Full(string? digits)
    {
        if (string.IsNullOrWhiteSpace(digits)) return null;
        var d = new string(digits.Where(char.IsAsciiDigit).ToArray());
        return d.Length == 0 ? null : "+" + d;
    }
}
