using Rasel.Domain.Entities;
using Rasel.Domain.Enums;

namespace Rasel.Application.Ports;

public sealed record GroupInfo(string Id, string Name);

public sealed class ProviderException(string message) : Exception(message);

public interface IWhatsAppProvider
{
    Task CreateSessionAsync(Guid sessionId, CancellationToken ct);
    Task StopSessionAsync(Guid sessionId, CancellationToken ct);
    Task<string?> GetQrAsync(Guid sessionId, CancellationToken ct);
    Task<SessionStatus> GetStatusAsync(Guid sessionId, CancellationToken ct);
    Task SendTextToPhoneAsync(Guid sessionId, string phoneE164, string text, CancellationToken ct);
    /// <summary>Throws <see cref="ProviderException"/> unless exactly one group has this exact name.</summary>
    Task SendTextToGroupAsync(Guid sessionId, string groupName, string text, CancellationToken ct);
    Task<IReadOnlyList<GroupInfo>> FindGroupsByNameAsync(Guid sessionId, string exactName, CancellationToken ct);
}

public interface ISmsProvider
{
    Task SendAsync(string phoneE164, string text, CancellationToken ct);
}

public interface IApiClientRepository
{
    Task<ApiClient?> FindByKeyHashAsync(string apiKeyHash, CancellationToken ct);
    Task<bool> AnyAsync(CancellationToken ct);
    Task AddAsync(ApiClient client, CancellationToken ct);
}

public interface ISessionRepository
{
    Task<Session?> GetAsync(Guid id, Guid clientId, CancellationToken ct);
    Task<Session?> FindConnectedAsync(Guid clientId, CancellationToken ct);
    Task AddAsync(Session session, CancellationToken ct);
    Task UpdateAsync(Session session, CancellationToken ct);
}

public interface IMessageRepository
{
    Task<Message?> GetAsync(Guid id, CancellationToken ct);
    Task<int> CountSinceAsync(Guid clientId, DateTimeOffset since, CancellationToken ct);
    Task AddAsync(Message message, CancellationToken ct);
    Task UpdateAsync(Message message, CancellationToken ct);
}

public interface IOtpRepository
{
    Task<OtpChallenge?> GetAsync(Guid id, Guid clientId, CancellationToken ct);
    Task<IReadOnlyList<OtpChallenge>> GetActiveAsync(Guid clientId, string phoneE164, string purpose, CancellationToken ct);
    Task AddAsync(OtpChallenge challenge, CancellationToken ct);
    Task UpdateAsync(IEnumerable<OtpChallenge> challenges, CancellationToken ct);
}

/// <summary>A message waiting to be sent. The text lives only in memory until it is delivered.</summary>
public sealed record QueuedMessage(Guid MessageId, Guid ClientId, Guid SessionId, TargetType TargetType, string Target, string Text);

public interface IMessageQueue
{
    ValueTask EnqueueAsync(QueuedMessage message, CancellationToken ct);
    ValueTask<QueuedMessage> DequeueAsync(CancellationToken ct);
}

public interface IRateLimiter
{
    /// <summary>Records a hit and returns true when it is within <paramref name="limit"/> per <paramref name="window"/>.</summary>
    bool TryAcquire(string key, int limit, TimeSpan window);
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

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IWebhookDispatcher
{
    Task DispatchAsync(Guid clientId, string eventName, object payload, CancellationToken ct);
}

public interface ITokenIssuer
{
    string Issue(Guid clientId, string phoneE164, string purpose, TimeSpan lifetime);
}
