using Rasel.Application.Common;
using Rasel.Application.Ports;

namespace Rasel.Application.UseCases;

/// <summary>Per client + recipient number daily cap (UTC day). The recipient is stored only as a keyed hash.</summary>
public sealed class RecipientLimitGuard(IMessageRepository messages, IOtpHasher hasher, PacingOptions options, TimeProvider clock)
{
    /// <summary>Keyed, non-reversible identifier of (client, number); lets us count without storing the number.</summary>
    public string KeyFor(Guid clientId, string phoneE164) => hasher.Hash(clientId, "recipient:" + phoneE164);

    public async Task EnsureWithinAsync(Guid clientId, string phoneE164, CancellationToken ct)
    {
        var since = new DateTimeOffset(clock.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero);
        if (await messages.CountForRecipientSinceAsync(clientId, KeyFor(clientId, phoneE164), since, ct) >= options.DailyPerRecipient)
            throw new AppException(ErrorKind.TooManyRequests, "recipient_daily_limit", "Daily message limit for this recipient reached.");
    }
}
