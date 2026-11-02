using System.Security.Cryptography;
using Rasel.Application.Ports;

namespace Rasel.Infrastructure.Security;

public sealed class CsprngCodeGenerator : ICodeGenerator
{
    public string GenerateNumeric(int digits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(digits, 4);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(digits, 9);
        return RandomNumberGenerator.GetInt32((int)Math.Pow(10, digits)).ToString().PadLeft(digits, '0');
    }
}
