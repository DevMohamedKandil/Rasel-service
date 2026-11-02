using Microsoft.EntityFrameworkCore;
using Rasel.Application.Ports;
using Rasel.Domain.Entities;
using Rasel.Domain.Enums;

namespace Rasel.Infrastructure.Persistence;

public sealed class ApiClientRepository(RaselDbContext db) : IApiClientRepository
{
    public Task<ApiClient?> FindByKeyHashAsync(string apiKeyHash, CancellationToken ct) =>
        db.ApiClients.FirstOrDefaultAsync(c => c.ApiKeyHash == apiKeyHash, ct);

    public Task<bool> AnyAsync(CancellationToken ct) => db.ApiClients.AnyAsync(ct);

    public async Task AddAsync(ApiClient client, CancellationToken ct)
    {
        db.ApiClients.Add(client);
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

    public Task<int> CountSinceAsync(Guid clientId, DateTimeOffset since, CancellationToken ct) =>
        db.Messages.CountAsync(m => m.ClientId == clientId && m.CreatedAt >= since, ct);

    public async Task AddAsync(Message message, CancellationToken ct)
    {
        db.Messages.Add(message);
        await db.SaveChangesAsync(ct);
    }

    public Task UpdateAsync(Message message, CancellationToken ct) => db.SaveChangesAsync(ct);
}

public sealed class OtpRepository(RaselDbContext db) : IOtpRepository
{
    public Task<OtpChallenge?> GetAsync(Guid id, Guid clientId, CancellationToken ct) =>
        db.OtpChallenges.FirstOrDefaultAsync(c => c.Id == id && c.ClientId == clientId, ct);

    public async Task<IReadOnlyList<OtpChallenge>> GetActiveAsync(Guid clientId, string phoneE164, string purpose, CancellationToken ct) =>
        await db.OtpChallenges
            .Where(c => c.ClientId == clientId && c.PhoneE164 == phoneE164 && c.Purpose == purpose && c.Status == OtpStatus.Active)
            .ToListAsync(ct);

    public async Task AddAsync(OtpChallenge challenge, CancellationToken ct)
    {
        db.OtpChallenges.Add(challenge);
        await db.SaveChangesAsync(ct);
    }

    // Entities are tracked by the scoped context, so saving persists every change made by the handler.
    public Task UpdateAsync(IEnumerable<OtpChallenge> challenges, CancellationToken ct) => db.SaveChangesAsync(ct);
}
