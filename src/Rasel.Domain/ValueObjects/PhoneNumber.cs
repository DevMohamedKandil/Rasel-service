namespace Rasel.Domain.ValueObjects;

/// <summary>A phone number normalized to E.164. Always requires an international prefix (no default region).</summary>
public readonly record struct PhoneNumber
{
    private PhoneNumber(string e164) => E164 = e164;

    public string E164 { get; }

    /// <summary>Safe for logs and storage of display values: keeps only the country prefix and last 3 digits.</summary>
    public string Masked => E164 is { Length: > 7 } ? $"{E164[..3]}{new string('*', E164.Length - 6)}{E164[^3..]}" : "***";

    public static bool TryParse(string? input, out PhoneNumber phone)
    {
        phone = default;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var cleaned = new string(input.Where(c => c is not (' ' or '-' or '(' or ')' or '.')).ToArray());
        if (cleaned.StartsWith("00", StringComparison.Ordinal)) cleaned = "+" + cleaned[2..];
        if (cleaned.Length < 2 || cleaned[0] != '+') return false;

        var digits = cleaned[1..];
        if (digits.Length is < 8 or > 15 || digits[0] == '0' || !digits.All(char.IsAsciiDigit)) return false;

        phone = new PhoneNumber(cleaned);
        return true;
    }

    public static PhoneNumber Parse(string? input) =>
        TryParse(input, out var phone) ? phone : throw new ArgumentException("Invalid phone number.", nameof(input));

    public override string ToString() => Masked;
}
