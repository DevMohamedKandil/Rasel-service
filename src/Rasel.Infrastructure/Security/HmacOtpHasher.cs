using System.Security.Cryptography;
using System.Text;
using Rasel.Application.Ports;

namespace Rasel.Infrastructure.Security;

public sealed class HmacOtpHasher(string pepper) : IOtpHasher
{
    private readonly byte[] _key = Encoding.UTF8.GetBytes(pepper);

    // The challenge id is mixed in so identical codes never share a hash.
    public string Hash(Guid challengeId, string code) =>
        Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{challengeId:N}:{code}")));

    public bool Verify(Guid challengeId, string code, string hash) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(challengeId, code)), Encoding.ASCII.GetBytes(hash));
}
