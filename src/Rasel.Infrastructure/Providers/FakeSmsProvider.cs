using Rasel.Application.Ports;

namespace Rasel.Infrastructure.Providers;

public sealed record FakeSms(string PhoneE164, string Text);

public sealed class FakeSmsProvider : ISmsProvider
{
    private readonly object _gate = new();
    private readonly List<FakeSms> _sent = [];

    public bool FailSends { get; set; }

    public IReadOnlyList<FakeSms> SentMessages
    {
        get { lock (_gate) return [.. _sent]; }
    }

    public Task SendAsync(string phoneE164, string text, CancellationToken ct)
    {
        if (FailSends) throw new ProviderException("Fake SMS send failed.");
        lock (_gate) _sent.Add(new FakeSms(phoneE164, text));
        return Task.CompletedTask;
    }
}
