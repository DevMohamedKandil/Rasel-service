using Rasel.Domain.Entities;
using Rasel.Domain.Enums;

namespace Rasel.Application.Ports;

public sealed record GroupInfo(string Id, string Name);

/// <summary>A provider failure. Transient failures (timeouts, 5xx, open circuit) are retried by the outbox; permanent ones are not.</summary>
public sealed class ProviderException(string message, bool isTransient = false, bool isNotReady = false, string? code = null) : Exception(message)
{
    /// <summary>Safe machine code for the cause (see ErrorCodes); null = unknown (reported as provider_error).</summary>
    public string? Code { get; } = code;
    public bool IsTransient { get; } = isTransient;
    /// <summary>The session's own engine is not up yet (preparing, needs a QR, restarting). Queued messages wait for it without using up attempts (see OutboxOptions.NotReadyMaxAgeMinutes).</summary>
    public bool IsNotReady { get; } = isNotReady;
}

public interface IWhatsAppProvider
{
    Task CreateSessionAsync(Guid sessionId, CancellationToken ct);
    Task StopSessionAsync(Guid sessionId, CancellationToken ct);
    Task<string?> GetQrAsync(Guid sessionId, CancellationToken ct);
    Task<SessionStatus> GetStatusAsync(Guid sessionId, CancellationToken ct);
    /// <summary>Digits of the number linked to the session (no '+'), or null when not connected / unknown. Sensitive: never log it.</summary>
    Task<string?> GetLinkedPhoneAsync(Guid sessionId, CancellationToken ct) => Task.FromResult<string?>(null);
    /// <summary>Stops the session and deletes its stored login data (Managed mode) once the engine process is dead. Default: same as Stop.</summary>
    Task PurgeSessionAsync(Guid sessionId, CancellationToken ct) => StopSessionAsync(sessionId, ct);
    /// <summary>Unlinks the WhatsApp number from the session and prepares a fresh QR. The session then needs a new scan.</summary>
    Task LogoutAsync(Guid sessionId, CancellationToken ct);
    /// <param name="messageId">Stable per message, so a real provider can de-duplicate a retried send.</param>
    Task SendTextToPhoneAsync(Guid sessionId, string phoneE164, string text, Guid messageId, CancellationToken ct);
    /// <summary>Throws <see cref="ProviderException"/> unless exactly one group has this exact name.</summary>
    Task SendTextToGroupAsync(Guid sessionId, string groupName, string text, Guid messageId, CancellationToken ct);
    Task<IReadOnlyList<GroupInfo>> FindGroupsByNameAsync(Guid sessionId, string exactName, CancellationToken ct);
}

public interface ISmsProvider
{
    Task SendAsync(string phoneE164, string text, CancellationToken ct);
}

/// <summary>Transactional email transport. The application layer owns templates and business decisions.</summary>
public interface IEmailProvider
{
    Task SendAsync(string to, string subject, string body, bool isHtml, CancellationToken ct);
}

public interface IApiClientRepository
{
    Task<ApiClient?> FindByKeyHashAsync(string apiKeyHash, CancellationToken ct);
    Task<bool> AnyAsync(CancellationToken ct);
    Task AddAsync(ApiClient client, CancellationToken ct);
    Task<ApiClient?> GetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<ApiClient>> ListAsync(CancellationToken ct);
    /// <summary>Usable Admin keys at <paramref name="now"/>.</summary>
    Task<int> CountUsableAdminsAsync(DateTimeOffset now, CancellationToken ct);
    Task SaveAsync(ApiClient client, CancellationToken ct);
    /// <summary>Cheap, best-effort LastUsedAt write (only when the stored value is older than <paramref name="olderThan"/>).</summary>
    Task TouchAsync(Guid id, DateTimeOffset now, DateTimeOffset olderThan, CancellationToken ct);
}

public interface ISessionRepository
{
    Task<Session?> GetAsync(Guid id, Guid clientId, CancellationToken ct);
    /// <summary>Every session that is not stopped, oldest first (used to re-adopt sessions at startup).</summary>
    Task<IReadOnlyList<Session>> ListActiveAsync(CancellationToken ct);
    Task<Session?> FindConnectedAsync(Guid clientId, CancellationToken ct);
    Task AddAsync(Session session, CancellationToken ct);
    Task UpdateAsync(Session session, CancellationToken ct);
}

public interface IMessageRepository
{
    Task<Message?> GetAsync(Guid id, CancellationToken ct);
    Task<Message?> FindByIdempotencyKeyAsync(Guid clientId, string key, CancellationToken ct);
    Task<int> CountSinceAsync(Guid clientId, DateTimeOffset since, CancellationToken ct);
    Task<int> CountForRecipientSinceAsync(Guid clientId, string recipientKey, DateTimeOffset since, CancellationToken ct);

    /// <summary>Returns false (and stores nothing) when another message already owns the same (client, idempotency key).</summary>
    Task<bool> TryAddAsync(Message message, CancellationToken ct);

    /// <summary>Persists changes; false when the row was changed by someone else since it was loaded.</summary>
    Task<bool> TrySaveAsync(Message message, CancellationToken ct);

    /// <summary>Leases up to <paramref name="take"/> due messages for this worker. Messages lost to a concurrent claimer are skipped.</summary>
    Task<IReadOnlyList<Message>> ClaimDueAsync(DateTimeOffset now, TimeSpan lease, int take, CancellationToken ct);
}

public interface IOtpRepository
{
    Task<OtpChallenge?> GetAsync(Guid id, Guid clientId, CancellationToken ct);
    Task<IReadOnlyList<OtpChallenge>> GetActiveAsync(Guid clientId, string phoneE164, string purpose, CancellationToken ct);

    /// <summary>Returns false when an Active challenge for the same (client, phone, purpose) already exists.</summary>
    Task<bool> TryAddAsync(OtpChallenge challenge, CancellationToken ct);

    /// <summary>
    /// Persists changes atomically. Returns false on an optimistic-concurrency conflict; the conflicting
    /// entities are detached so the caller must reload them before retrying.
    /// </summary>
    Task<bool> TrySaveAsync(IEnumerable<OtpChallenge> challenges, CancellationToken ct);
}

public interface IRateLimiter
{
    /// <summary>Records a hit and returns true when it is within <paramref name="limit"/> per <paramref name="window"/>.</summary>
    bool TryAcquire(string key, int limit, TimeSpan window);

    /// <summary>True when the key already used up its quota. Does not record a hit.</summary>
    bool IsExceeded(string key, int limit, TimeSpan window);
}

public interface IOtpHasher
{
    string Hash(Guid challengeId, string code);
    bool Verify(Guid challengeId, string code, string hash);
}

public interface ICodeGenerator
{
    string GenerateNumeric(int digits);
}

public interface IWebhookDispatcher
{
    Task DispatchAsync(Guid clientId, string eventName, object payload, CancellationToken ct);
}

public interface ITokenIssuer
{
    string Issue(Guid clientId, string phoneE164, string purpose, TimeSpan lifetime);
}
