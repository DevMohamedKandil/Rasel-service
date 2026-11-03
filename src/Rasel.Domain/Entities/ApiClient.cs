using Rasel.Domain.Enums;

namespace Rasel.Domain.Entities;

public class ApiClient
{
    private ApiClient() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = "";
    public string ApiKeyHash { get; private set; } = "";
    public int DailyMessageLimit { get; private set; }
    public bool Active { get; private set; }
    public ApiKeyScope Scope { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? LastUsedAt { get; private set; }
    /// <summary>Set on a rotated key during its grace period: the key stops working at this instant.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    public static ApiClient Create(string name, string apiKeyHash, int dailyMessageLimit, DateTimeOffset now, ApiKeyScope scope = ApiKeyScope.Standard) => new()
    {
        Id = Guid.NewGuid(), Name = name, ApiKeyHash = apiKeyHash, DailyMessageLimit = dailyMessageLimit, Active = true, Scope = scope, CreatedAt = now,
    };

    /// <summary>True when the key may authenticate at <paramref name="now"/> (not revoked, grace period not over).</summary>
    public bool IsUsable(DateTimeOffset now) => Active && (ExpiresAt is null || ExpiresAt > now);

    public void MarkUsed(DateTimeOffset now) => LastUsedAt = now;

    public void Revoke() { Active = false; ExpiresAt = null; }

    public void ExpireAt(DateTimeOffset at) => ExpiresAt = at;
}
