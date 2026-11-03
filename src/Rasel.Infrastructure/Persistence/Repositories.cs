using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Rasel.Application.Ports;
using Rasel.Domain.Entities;
using Rasel.Domain.Enums;

namespace Rasel.Infrastructure.Persistence;

internal static class SaveHelpers
{
    /// <summary>Saves; on an optimistic-concurrency conflict detaches the stale entities and returns false.</summary>
    public static async Task<bool> TrySaveAsync(this DbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException ex)
        {
            foreach (var entry in ex.Entries) entry.State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>Adds the entity; on a unique-constraint violation detaches it and returns false.</summary>
    public static async Task<bool> TryAddAsync<T>(this DbContext db, T entity, CancellationToken ct) where T : class
    {
        db.Add(entity);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            db.Entry(entity).State = EntityState.Detached;
            return false;
        }
    }
}

public sealed class ApiClientRepository(RaselDbContext db) : IApiClientRepository
{
    public Task<ApiClient?> FindByKeyHashAsync(string apiKeyHash, CancellationToken ct) =>
        db.ApiClients.AsNoTracking().FirstOrDefaultAsync(c => c.ApiKeyHash == apiKeyHash, ct);

    public Task<bool> AnyAsync(CancellationToken ct) => db.ApiClients.AnyAsync(ct);

    public async Task AddAsync(ApiClient client, CancellationToken ct)
    {
        db.ApiClients.Add(client);
        await db.SaveChangesAsync(ct);
    }

    public Task<ApiClient?> GetAsync(Guid id, CancellationToken ct) => db.ApiClients.FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<ApiClient>> ListAsync(CancellationToken ct) =>
        (await db.ApiClients.AsNoTracking().ToListAsync(ct)).OrderBy(c => c.CreatedAt).ToList(); // tiny table; order client-side

    public async Task<int> CountUsableAdminsAsync(DateTimeOffset now, CancellationToken ct) =>
        (await db.ApiClients.AsNoTracking().Where(c => c.Active && c.Scope == ApiKeyScope.Admin).ToListAsync(ct)).Count(c => c.IsUsable(now));

    public Task SaveAsync(ApiClient client, CancellationToken ct) => db.SaveChangesAsync(ct);

    public async Task TouchAsync(Guid id, DateTimeOffset now, DateTimeOffset olderThan, CancellationToken ct)
    {
        var c = await db.ApiClients.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null || (c.LastUsedAt is { } last && last >= olderThan)) return;
        c.MarkUsed(now);
        await db.SaveChangesAsync(ct);
    }
}

public sealed class SessionRepository(RaselDbContext db) : ISessionRepository
{
    public Task<Session?> GetAsync(Guid id, Guid clientId, CancellationToken ct) =>
        db.Sessions.FirstOrDefaultAsync(s => s.Id == id && s.ClientId == clientId, ct);

    public Task<Session?> FindConnectedAsync(Guid clientId, CancellationToken ct) =>
        db.Sessions.FirstOrDefaultAsync(s => s.ClientId == clientId && s.Status == SessionStatus.Connected, ct);

    public async Task AddAsync(Session session, CancellationToken ct)
    {
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
    }

    public Task UpdateAsync(Session session, CancellationToken ct) => db.SaveChangesAsync(ct);
}

public sealed class MessageRepository(RaselDbContext db) : IMessageRepository
{
    public Task<Message?> GetAsync(Guid id, CancellationToken ct) => db.Messages.FirstOrDefaultAsync(m => m.Id == id, ct);

    public Task<Message?> FindByIdempotencyKeyAsync(Guid clientId, string key, CancellationToken ct) =>
        db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.ClientId == clientId && m.IdempotencyKey == key, ct);

    public Task<int> CountSinceAsync(Guid clientId, DateTimeOffset since, CancellationToken ct) =>
        db.Messages.CountAsync(m => m.ClientId == clientId && m.CreatedAt >= since, ct);

    public Task<int> CountForRecipientSinceAsync(Guid clientId, string recipientKey, DateTimeOffset since, CancellationToken ct) =>
        db.Messages.CountAsync(m => m.ClientId == clientId && m.RecipientKey == recipientKey && m.CreatedAt >= since, ct);

    public Task<bool> TryAddAsync(Message message, CancellationToken ct) => db.TryAddAsync(message, ct);

    public Task<bool> TrySaveAsync(Message message, CancellationToken ct) => db.TrySaveAsync(ct);

    public async Task<IReadOnlyList<Message>> ClaimDueAsync(DateTimeOffset now, TimeSpan lease, int take, CancellationToken ct)
    {
        var due = await db.Messages
            .Where(m => m.Status == MessageStatus.Queued && m.NextAttemptAt <= now && (m.LeasedUntil == null || m.LeasedUntil <= now))
            .OrderBy(m => m.NextAttemptAt)
            .Take(take)
            .ToListAsync(ct);

        var claimed = new List<Message>(due.Count);
        foreach (var message in due)
        {
            message.Lease(now, lease);
            // One save per message: a row lost to another claimer is skipped without discarding the others.
            if (await db.TrySaveAsync(ct)) claimed.Add(message);
        }

        return claimed;
    }
}

public sealed class OtpRepository(RaselDbContext db) : IOtpRepository
{
    public Task<OtpChallenge?> GetAsync(Guid id, Guid clientId, CancellationToken ct) =>
        db.OtpChallenges.FirstOrDefaultAsync(c => c.Id == id && c.ClientId == clientId, ct);

    public async Task<IReadOnlyList<OtpChallenge>> GetActiveAsync(Guid clientId, string phoneE164, string purpose, CancellationToken ct) =>
        await db.OtpChallenges
            .Where(c => c.ClientId == clientId && c.PhoneE164 == phoneE164 && c.Purpose == purpose && c.Status == OtpStatus.Active)
            .ToListAsync(ct);

    public Task<bool> TryAddAsync(OtpChallenge challenge, CancellationToken ct) => db.TryAddAsync(challenge, ct);

    // Entities are tracked by the scoped context, so saving persists every change made by the handler.
    public Task<bool> TrySaveAsync(IEnumerable<OtpChallenge> challenges, CancellationToken ct) => db.TrySaveAsync(ct);
}
