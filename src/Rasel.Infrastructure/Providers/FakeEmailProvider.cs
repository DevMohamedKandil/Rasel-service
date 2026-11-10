using Rasel.Application.Ports;

namespace Rasel.Infrastructure.Providers;

public sealed record FakeEmail(string To, string Subject, string Body, bool IsHtml);

public sealed class FakeEmailProvider : IEmailProvider
{
    private readonly object _gate = new();
    private readonly List<FakeEmail> _sent = [];

    public bool FailSends { get; set; }

    public IReadOnlyList<FakeEmail> SentMessages
    {
        get { lock (_gate) return [.. _sent]; }
    }

    public Task SendAsync(string to, string subject, string body, bool isHtml, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (FailSends) throw new ProviderException("Fake email send failed.");
        lock (_gate) _sent.Add(new FakeEmail(to, subject, body, isHtml));
        return Task.CompletedTask;
    }
}
