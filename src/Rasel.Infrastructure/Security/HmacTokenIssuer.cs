using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rasel.Application.Ports;

namespace Rasel.Infrastructure.Security;

/// <summary>Issues compact HS256 JWTs the calling project can validate with the shared secret.</summary>
public sealed class HmacTokenIssuer(string secret, TimeProvider clock) : ITokenIssuer
{
    public const string Issuer = "rasel";
    public const string Audience = "rasel-otp-verified";

    private readonly byte[] _key = Encoding.UTF8.GetBytes(secret);

    public string Issue(Guid clientId, string phoneE164, string purpose, TimeSpan lifetime)
    {
        var now = clock.GetUtcNow();
        var header = Encode(new { alg = "HS256", typ = "JWT" });
        var payload = Encode(new
        {
            iss = Issuer,
            aud = Audience,
            jti = Guid.NewGuid(),
            clientId,
            phone = phoneE164,
            purpose,
            iat = now.ToUnixTimeSeconds(),
            exp = (now + lifetime).ToUnixTimeSeconds(),
        });
        var signature = Base64Url(HMACSHA256.HashData(_key, Encoding.ASCII.GetBytes($"{header}.{payload}")));
        return $"{header}.{payload}.{signature}";
    }

    private static string Encode(object value) => Base64Url(JsonSerializer.SerializeToUtf8Bytes(value));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
