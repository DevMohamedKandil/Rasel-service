using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Domain.Entities;

namespace Rasel.Application.UseCases;

/// <summary>Soft per-client daily quota (counted from UTC midnight). Concurrent requests may overshoot by the in-flight count.</summary>
public sealed class DailyLimitGuard(IMessageRepository messages, TimeProvider clock)
{
    public async Task EnsureWithinAsync(ApiClient client, CancellationToken ct)
    {
        var since = new DateTimeOffset(clock.GetUtcNow().UtcDateTime.Date, TimeSpan.Zero);
        if (await messages.CountSinceAsync(client.Id, since, ct) >= client.DailyMessageLimit)
            throw new AppException(ErrorKind.TooManyRequests, "daily_limit_reached", "Daily message limit reached.");
    }
}
