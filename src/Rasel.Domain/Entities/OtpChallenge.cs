using Rasel.Domain.Enums;

namespace Rasel.Domain.Entities;

public class OtpChallenge
{
    private OtpChallenge() { }

    public Guid Id { get; private set; }
    public Guid ClientId { get; private set; }
    public string PhoneE164 { get; private set; } = "";
    public string Purpose { get; private set; } = "";
    public string CodeHash { get; private set; } = "";
    public DateTimeOffset ExpiresAt { get; private set; }
    public int Attempts { get; private set; }
    public int MaxAttempts { get; private set; }
    public DateTimeOffset ResendAvailableAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }
    public OtpStatus Status { get; private set; }

    /// <summary>
    /// Optimistic concurrency token. Bumped by every state change, so two writers that loaded the same version
    /// cannot both succeed (one gets a concurrency conflict and must reload).
    /// </summary>
    public long Version { get; private set; }

    public static OtpChallenge Issue(
        Guid id, Guid clientId, string phoneE164, string purpose, string codeHash,
        DateTimeOffset now, TimeSpan ttl, TimeSpan resendCooldown, int maxAttempts) => new()
    {
        Id = id, ClientId = clientId, PhoneE164 = phoneE164, Purpose = purpose, CodeHash = codeHash,
        ExpiresAt = now + ttl, ResendAvailableAt = now + resendCooldown, MaxAttempts = maxAttempts, Status = OtpStatus.Active,
    };

    public void Supersede()
    {
        if (Status != OtpStatus.Active) return;
        Status = OtpStatus.Superseded;
        Version++;
    }

    /// <summary>Applies one verification attempt. <paramref name="codeMatches"/> is the result of a constant-time hash comparison.</summary>
    public OtpAttemptResult Attempt(bool codeMatches, DateTimeOffset now)
    {
        switch (Status)
        {
            case OtpStatus.Consumed: return OtpAttemptResult.AlreadyUsed;
            case OtpStatus.Locked: return OtpAttemptResult.Locked;
            case OtpStatus.Superseded: return OtpAttemptResult.Superseded;
            case OtpStatus.Expired: return OtpAttemptResult.Expired;
        }

        Version++;
        if (now >= ExpiresAt)
        {
            Status = OtpStatus.Expired;
            return OtpAttemptResult.Expired;
        }

        if (!codeMatches)
        {
            Attempts++;
            if (Attempts >= MaxAttempts) Status = OtpStatus.Locked;
            return OtpAttemptResult.Invalid;
        }

        Status = OtpStatus.Consumed;
        ConsumedAt = now;
        return OtpAttemptResult.Verified;
    }
}
