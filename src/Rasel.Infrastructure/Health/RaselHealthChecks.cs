using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Rasel.Domain.Enums;
using Rasel.Infrastructure.Persistence;

namespace Rasel.Infrastructure.Health;

/// <summary>Readiness: the database accepts connections.</summary>
public sealed class DatabaseHealthCheck(RaselDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await db.Database.CanConnectAsync(ct)
            ? HealthCheckResult.Healthy("database reachable")
            : HealthCheckResult.Unhealthy("database unreachable");
}

/// <summary>Degraded (never unhealthy) when a pending message is overdue by more than <see cref="StuckAfter"/>: the worker or provider is stuck.</summary>
public sealed class OutboxHealthCheck(RaselDbContext db, TimeProvider clock) : IHealthCheck
{
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(5);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var overdueBefore = clock.GetUtcNow() - StuckAfter;
        var overdue = await db.Messages.CountAsync(
            m => m.Status == MessageStatus.Queued && m.NextAttemptAt <= overdueBefore, ct);
        return overdue == 0
            ? HealthCheckResult.Healthy("outbox draining")
            : HealthCheckResult.Degraded($"{overdue} message(s) overdue by more than {StuckAfter.TotalMinutes:0} minutes");
    }
}
