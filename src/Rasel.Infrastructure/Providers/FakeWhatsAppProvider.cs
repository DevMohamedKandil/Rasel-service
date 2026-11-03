using Rasel.Application.Ports;
using Rasel.Domain.Enums;

namespace Rasel.Infrastructure.Providers;

public sealed record FakeSentMessage(Guid SessionId, string Target, bool IsGroup, string Text);

/// <summary>
/// In-memory stand-in for WhatsApp. A session starts in NeedsQr and becomes Connected the first time
/// its QR is fetched (simulating a scan). Sent messages are captured so tests and demos can inspect them.
/// </summary>
public sealed class FakeWhatsAppProvider : IWhatsAppProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, SessionStatus> _sessions = [];
    private readonly List<string> _groups = ["Rasel Demo Group"];
    private readonly List<FakeSentMessage> _sent = [];

    /// <summary>When true every send fails, to exercise failure paths.</summary>
    public bool FailSends { get; set; }

    /// <summary>When true, injected failures are reported as transient (the outbox retries them).</summary>
    public bool FailTransiently { get; set; }

    public IReadOnlyList<FakeSentMessage> SentMessages
    {
        get { lock (_gate) return [.. _sent]; }
    }

    public void AddGroup(string name)
    {
        lock (_gate) _groups.Add(name);
    }

    public Task CreateSessionAsync(Guid sessionId, CancellationToken ct)
    {
        lock (_gate) _sessions[sessionId] = SessionStatus.NeedsQr;
        return Task.CompletedTask;
    }

    public Task StopSessionAsync(Guid sessionId, CancellationToken ct)
    {
        lock (_gate) _sessions[sessionId] = SessionStatus.Stopped;
        return Task.CompletedTask;
    }

    public Task LogoutAsync(Guid sessionId, CancellationToken ct)
    {
        lock (_gate) _sessions[sessionId] = SessionStatus.NeedsQr;
        return Task.CompletedTask;
    }

    public Task<string?> GetQrAsync(Guid sessionId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var status) || status != SessionStatus.NeedsQr) return Task.FromResult<string?>(null);
            _sessions[sessionId] = SessionStatus.Connected;
            return Task.FromResult<string?>($"fake-qr:{sessionId:N}");
        }
    }

    public Task<SessionStatus> GetStatusAsync(Guid sessionId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_sessions.GetValueOrDefault(sessionId, SessionStatus.Failed));
    }

    /// <summary>Fixed fake number while connected.</summary>
    public const string FakeLinkedPhone = "201000000000";

    public Task<string?> GetLinkedPhoneAsync(Guid sessionId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(_sessions.GetValueOrDefault(sessionId) == SessionStatus.Connected ? FakeLinkedPhone : null);
    }

    public Task SendTextToPhoneAsync(Guid sessionId, string phoneE164, string text, Guid messageId, CancellationToken ct) =>
        Record(new FakeSentMessage(sessionId, phoneE164, false, text));

    public Task SendTextToGroupAsync(Guid sessionId, string groupName, string text, Guid messageId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_groups.Count(g => g == groupName) != 1) throw new ProviderException("Group name does not match exactly one group.");
        }

        return Record(new FakeSentMessage(sessionId, groupName, true, text));
    }

    public Task<IReadOnlyList<GroupInfo>> FindGroupsByNameAsync(Guid sessionId, string exactName, CancellationToken ct)
    {
        lock (_gate)
        {
            IReadOnlyList<GroupInfo> found = _groups.Where(g => g == exactName).Select((g, i) => new GroupInfo($"fake-group-{i}", g)).ToList();
            return Task.FromResult(found);
        }
    }

    private Task Record(FakeSentMessage message)
    {
        lock (_gate)
        {
            if (FailSends || _sessions.GetValueOrDefault(message.SessionId) != SessionStatus.Connected)
                throw new ProviderException("Fake WhatsApp send failed.", FailTransiently);
            _sent.Add(message);
        }

        return Task.CompletedTask;
    }
}
