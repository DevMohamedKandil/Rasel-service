using System.Text.RegularExpressions;

namespace Rasel.Application.Common;

/// <summary>Scrubs phone-number-like digit runs and bounds the length of free text (provider errors) before it is stored or logged.</summary>
public static partial class PiiRedactor
{
    public const int MaxLength = 200;

    [GeneratedRegex(@"\+?\d[\d\s\-().]{5,}\d")]
    private static partial Regex PhoneLike();

    public static string Redact(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "unknown error";
        var scrubbed = PhoneLike().Replace(text, "[redacted]");
        return scrubbed.Length <= MaxLength ? scrubbed : scrubbed[..MaxLength];
    }
}
