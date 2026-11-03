using Microsoft.Extensions.Logging.Abstractions;
using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Application.UseCases;
using Rasel.Domain.Entities;
using Rasel.Domain.Enums;
using Rasel.Infrastructure.Messaging;
using Rasel.Infrastructure.Providers;
using Rasel.Infrastructure.Security;

namespace Rasel.Application.Tests;

public sealed class FakeClock : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => UtcNow += by;
    public override DateTimeOffset GetUtcNow() => UtcNow;
}

/// <summary>Returns queued values (clamped into range), then the minimum.</summary>
public sealed class FakeRandom : IRandomSource
{
    public Queue<int> Values { get; } = new();
    public int Next(int minInclusive, int maxExclusive) =>
        Values.Count == 0 ? minInclusive : Math.Clamp(Values.Dequeue(), minInclusive, maxExclusive - 1);
}

public sealed class FixedCodeGenerator : ICodeGenerator
{
    public string Next { get; set; } = "123456";
    public string GenerateNumeric(int digits) => Next;
}

public sealed class InMemoryStore : ISessionRepository, IMessageRepository, IOtpRepository
{
    public List<Session> Sessions { get; } = [];
    public List<Message> Messages { get; } = [];
    public List<OtpChallenge> Otps { get; } = [];

    public Task<Session?> GetAsync(Guid id, Guid clientId, CancellationToken ct) =>
        Task.FromResult(Sessions.FirstOrDefault(s => s.Id == id && s.ClientId == clientId));
    public Task<Session?> FindConnectedAsync(Guid clientId, CancellationToken ct) =>
        Task.FromResult(Sessions.FirstOrDefault(s => s.ClientId == clientId && s.Status == SessionStatus.Connected));
    public Task AddAsync(Session session, CancellationToken ct) { Sessions.Add(session); return Task.CompletedTask; }
    public Task UpdateAsync(Session session, CancellationToken ct) => Task.CompletedTask;

    Task<Message?> IMessageRepository.GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Messages.FirstOrDefault(m => m.Id == id));
    public Task<Message?> FindByIdempotencyKeyAsync(Guid clientId, string key, CancellationToken ct) =>
        Task.FromResult(Messages.FirstOrDefault(m => m.ClientId == clientId && m.IdempotencyKey == key));
    public Task<int> CountSinceAsync(Guid clientId, DateTimeOffset since, CancellationToken ct) =>
        Task.FromResult(Messages.Count(m => m.ClientId == clientId && m.CreatedAt >= since));
    public Task<int> CountForRecipientSinceAsync(Guid clientId, string recipientKey, DateTimeOffset since, CancellationToken ct) =>
        Task.FromResult(Messages.Count(m => m.ClientId == clientId && m.RecipientKey == recipientKey && m.CreatedAt >= since));
    public Task<bool> TryAddAsync(Message message, CancellationToken ct)
    {
        if (message.IdempotencyKey is not null && Messages.Any(m => m.ClientId == message.ClientId && m.IdempotencyKey == message.IdempotencyKey))
            return Task.FromResult(false);
        Messages.Add(message);
        return Task.FromResult(true);
    }
    public Task<bool> TrySaveAsync(Message message, CancellationToken ct) => Task.FromResult(true);
    public Task<IReadOnlyList<Message>> ClaimDueAsync(DateTimeOffset now, TimeSpan lease, int take, CancellationToken ct)
    {
        var due = Messages.Where(m => m.Status == MessageStatus.Queued && m.NextAttemptAt <= now && (m.LeasedUntil is null || m.LeasedUntil <= now)).Take(take).ToList();
        foreach (var m in due) m.Lease(now, lease);
        return Task.FromResult<IReadOnlyList<Message>>(due);
    }

    Task<OtpChallenge?> IOtpRepository.GetAsync(Guid id, Guid clientId, CancellationToken ct) =>
        Task.FromResult(Otps.FirstOrDefault(c => c.Id == id && c.ClientId == clientId));
    public Task<IReadOnlyList<OtpChallenge>> GetActiveAsync(Guid clientId, string phoneE164, string purpose, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<OtpChallenge>>(Otps.Where(c => c.ClientId == clientId && c.PhoneE164 == phoneE164
            && c.Purpose == purpose && c.Status == OtpStatus.Active).ToList());
    public Task<bool> TryAddAsync(OtpChallenge challenge, CancellationToken ct) { Otps.Add(challenge); return Task.FromResult(true); }
    public Task<bool> TrySaveAsync(IEnumerable<OtpChallenge> challenges, CancellationToken ct) => Task.FromResult(true);
}

public sealed class RecordingWebhooks : IWebhookDispatcher
{
    public List<(string Event, object Payload)> Events { get; } = [];

    public Task DispatchAsync(Guid clientId, string eventName, object payload, CancellationToken ct)
    {
        Events.Add((eventName, payload));
        return Task.CompletedTask;
    }
}

/// <summary>Wires the real handlers to in-memory fakes. The client has a connected session by default.</summary>
public sealed class Harness
{
    public FakeClock Clock { get; } = new();
    public FixedCodeGenerator Codes { get; } = new();
    public InMemoryStore Store { get; } = new();
    public FakeWhatsAppProvider WhatsApp { get; } = new();
    public FakeSmsProvider Sms { get; } = new();
    public OtpOptions Options { get; } = new();
    public OutboxOptions Outbox { get; } = new();
    public PacingOptions Pacing { get; } = new() { MinDelayMs = 0, MaxDelayMs = 0, MinGapMs = 0, DailyPerRecipient = 1000 };
    public FakeRandom Random { get; } = new();
    public SendPacer Pacer => _pacer ??= new(Pacing, Clock, Random);
    private SendPacer? _pacer;
    public RecordingWebhooks Webhooks { get; } = new();
    public ApiClient Client { get; }
    public HmacOtpHasher Hasher { get; } = new("pepper-pepper-pepper-pepper-pepper-1");
    public HmacTokenIssuer Tokens { get; }
    public Session Session { get; private set; } = null!;

    public Harness(int dailyLimit = 100)
    {
        Client = ApiClient.Create("test", "hash", dailyLimit);
        Tokens = new HmacTokenIssuer("secret-secret-secret-secret-secret-1", Clock);
        Limiter = new SlidingWindowRateLimiter(Clock);
    }

    public IRateLimiter Limiter { get; }

    public static async Task<Harness> CreateConnectedAsync(int dailyLimit = 100)
    {
        var h = new Harness(dailyLimit);
        var dto = await h.CreateSession().HandleAsync(h.Client.Id, "main", default);
        await h.WhatsApp.GetQrAsync(dto.Id, default);
        h.Session = h.Store.Sessions.Single();
        h.Session.UpdateStatus(SessionStatus.Connected, h.Clock.UtcNow);
        return h;
    }

    public DailyLimitGuard DailyLimit => new(Store, Clock);
    public RecipientLimitGuard RecipientLimit => new(Store, Hasher, Pacing, Clock);
    public OtpMessageComposer Composer => new(Options, Random);
    public CreateSessionHandler CreateSession() => new(Store, WhatsApp, Clock);
    public SendTextHandler SendText() => new(Store, Store, WhatsApp, DailyLimit, RecipientLimit, Clock);
    public RequestOtpHandler RequestOtp() =>
        new(Store, Store, Store, WhatsApp, Sms, Hasher, Codes, Limiter, DailyLimit, RecipientLimit, Composer, Pacer, Clock, Options, NullLogger<RequestOtpHandler>.Instance);
    public VerifyOtpHandler VerifyOtp() => new(Store, Hasher, Tokens, Clock, Options, NullLogger<VerifyOtpHandler>.Instance);
    public MessageSender Sender() => new(Store, WhatsApp, Webhooks, Outbox, Pacer, Clock, NullLogger<MessageSender>.Instance);
}
