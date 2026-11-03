namespace Rasel.Application.Common;

/// <summary>Injectable randomness so pacing and template choice are deterministic in tests.</summary>
public interface IRandomSource
{
    /// <summary>Returns a value in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
    int Next(int minInclusive, int maxExclusive);
}

public sealed class SystemRandomSource : IRandomSource
{
    public int Next(int minInclusive, int maxExclusive) => Random.Shared.Next(minInclusive, maxExclusive);
}
