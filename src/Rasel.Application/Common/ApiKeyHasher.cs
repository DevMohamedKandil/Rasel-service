using System.Security.Cryptography;
using System.Text;

namespace Rasel.Application.Common;

public static class ApiKeyHasher
{
    /// <summary>API keys are high-entropy random values, so a plain SHA-256 is sufficient and allows indexed lookup.</summary>
    public static string Hash(string apiKey) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));

    /// <summary>Constant-time comparison of two hex hashes.</summary>
    public static bool Matches(string apiKey, string expectedHash) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(apiKey)), Encoding.ASCII.GetBytes(expectedHash));
}
