using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Application.UseCases;
using Rasel.Domain.Entities;
using Rasel.Domain.Enums;
using Rasel.Infrastructure.Messaging;
using Rasel.Infrastructure.Providers;
using Rasel.Infrastructure.Security;

namespace Rasel.Application.Tests;

public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public void Advance(TimeSpan by) => UtcNow += by;
}

public sealed class FixedCodeGenerator : ICodeGenerator
{
    public string Next { get; set; } = "123456";
    public string GenerateNumeric(int digits) => Next;
}

public sealed class InMemoryStore : ISessionRepository, IMessageRepository, IOtpRepository, IMessageQueue
{
    public List<Session> Sessions { get; } = [];
    public List<Message> Messages { get; } = [];
    public List<OtpChallenge> Otps { get; } = [];
    public List<QueuedMessage> Queued { get; } = [];

    public Task<Session?> GetAsync(Guid id, Guid clientId, CancellationToken ct) =>
        Task.FromResult(Sessions.FirstOrDefault(s => s.Id == id && s.ClientId == clientId));
    public Task<Session?> FindConnectedAsync(Guid clientId, CancellationToken ct) =>
        Task.FromResult(Sessions.FirstOrDefault(s => s.ClientId == clientId && s.Status == SessionStatus.Connected));
    public Task AddAsync(Session session, CancellationToken ct) { Sessions.Add(session); return Task.CompletedTask; }
    public Task UpdateAsync(Session session, CancellationToken ct) => Task.CompletedTask;

    Task<Message?> IMessageRepository.GetAsync(Guid id, CancellationToken ct) => Task.FromResult(Messages.FirstOrDefault(m => m.Id == id));
    public Task<int> CountSinceAsync(Guid clientId, DateTimeOffset since, CancellationToken ct) =>
        Task.FromResult(Messages.Count(m => m.ClientId == clientId && m.CreatedAt >= since));
    public Task AddAsync(Message message, CancellationToken ct) { Messages.Add(message); return Task.CompletedTask; }
    public Task UpdateAsync(Message message, CancellationToken ct) => Task.CompletedTask;

    Task<OtpChallenge?> IOtpRepository.GetAsync(Guid id, Guid clientId, CancellationToken ct) =>
        Task.FromResult(Otps.FirstOrDefault(c => c.Id == id && c.ClientId == clientId));
    public Task<IReadOnlyList<OtpChallenge>> GetActiveAsync(Guid clientId, string phoneE164, string purpose, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<OtpChallenge>>(Otps.Where(c => c.ClientId == clientId && c.PhoneE164 == phoneE164
            && c.Purpose == purpose && c.Status == OtpStatus.Active).ToList());
    public Task AddAsync(OtpChallenge challenge, CancellationToken ct) { Otps.Add(challenge); return Task.CompletedTask; }
    public Task UpdateAsync(IEnumerable<OtpChallenge> challenges, CancellationToken ct) => Task.CompletedTask;

    public ValueTask EnqueueAsync(QueuedMessage message, CancellationToken ct) { Queued.Add(message); return ValueTask.CompletedTask; }
    public ValueTask<QueuedMessage> DequeueAsync(CancellationToken ct) => throw new NotSupportedException();
}

public sealed class NullWebhooks : IWebhookDispatcher
{
    public Task DispatchAsync(Guid clientId, string eventName, object payload, CancellationToken ct) => Task.CompletedTask;
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
    public ApiClient Client { get; }
    public HmacOtpHasher Hasher { get; } = new("pepper");
    public HmacTokenIssuer Tokens { get; }
    public Session Session { get; private set; } = null!;

    public Harness(int dailyLimit = 100)
    {
        Client = ApiClient.Create("test", "hash", dailyLimit);
        Tokens = new HmacTokenIssuer("secret", Clock);
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

    public CreateSessionHandler CreateSession() => new(Store, WhatsApp, Clock);
    public SendTextHandler SendText() => new(Store, Store, WhatsApp, Store, Clock);
    public RequestOtpHandler RequestOtp() => new(Store, Store, Store, WhatsApp, Sms, Hasher, Codes, Limiter, Clock, Options);
    public VerifyOtpHandler VerifyOtp() => new(Store, Hasher, Tokens, Clock, Options);
    public MessageSender Sender() => new(Store, WhatsApp, new NullWebhooks());
}
