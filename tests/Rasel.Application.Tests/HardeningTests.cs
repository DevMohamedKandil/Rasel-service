using Microsoft.Extensions.Logging;
using Rasel.Application.Common;
using Rasel.Application.UseCases;
using Rasel.Domain.Enums;
using Rasel.Infrastructure.Providers;

namespace Rasel.Application.Tests;

public class HardeningTests
{
    private const string Phone = "+201001234567";

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + exception);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12 456")]
    [InlineData("abcdef")]
    public async Task Malformed_code_is_rejected_and_does_not_consume_an_attempt(string code)
    {
        var h = await Harness.CreateConnectedAsync();
        var req = await h.RequestOtp().HandleAsync(h.Client, new(Phone, "login", "whatsapp"), default);

        var ex = await Assert.ThrowsAsync<AppException>(() => h.VerifyOtp().HandleAsync(h.Client.Id, req.ChallengeId, code, default));
        Assert.Equal("invalid_code_format", ex.Code);
        Assert.Equal(0, h.Store.Otps.Single().Attempts);
    }

    [Fact]
    public async Task Null_code_is_a_bad_request_not_a_crash()
    {
        var h = await Harness.CreateConnectedAsync();
        var req = await h.RequestOtp().HandleAsync(h.Client, new(Phone, "login", "whatsapp"), default);
        var ex = await Assert.ThrowsAsync<AppException>(() => h.VerifyOtp().HandleAsync(h.Client.Id, req.ChallengeId, null, default));
        Assert.Equal("invalid_code_format", ex.Code);
    }

    [Fact]
    public async Task Verification_runs_the_hash_comparison_even_for_finished_challenges()
    {
        // behavioural guard for the "no early exit by status" property: a consumed challenge still answers 409, a
        // wrong code on a consumed challenge is not distinguishable from a right one
        var h = await Harness.CreateConnectedAsync();
        var req = await h.RequestOtp().HandleAsync(h.Client, new(Phone, "login", "whatsapp"), default);
        await h.VerifyOtp().HandleAsync(h.Client.Id, req.ChallengeId, "123456", default);

        var right = await Assert.ThrowsAsync<AppException>(() => h.VerifyOtp().HandleAsync(h.Client.Id, req.ChallengeId, "123456", default));
        var wrong = await Assert.ThrowsAsync<AppException>(() => h.VerifyOtp().HandleAsync(h.Client.Id, req.ChallengeId, "654321", default));
        Assert.Equal(right.Code, wrong.Code);
    }

    [Fact]
    public async Task Logs_never_contain_the_code_or_the_full_phone_number()
    {
        var h = await Harness.CreateConnectedAsync();
        var requestLog = new CaptureLogger<RequestOtpHandler>();
        var verifyLog = new CaptureLogger<VerifyOtpHandler>();
        var request = new RequestOtpHandler(h.Store, h.Store, h.Store, h.WhatsApp, h.Sms, h.Hasher, h.Codes, h.Limiter, h.DailyLimit, h.RecipientLimit, h.Composer, h.Pacer, h.Clock, h.Options, requestLog);
        var verify = new VerifyOtpHandler(h.Store, h.Hasher, h.Tokens, h.Clock, h.Options, verifyLog);

        var req = await request.HandleAsync(h.Client, new(Phone, "login", "whatsapp"), default);
        await Assert.ThrowsAsync<AppException>(() => request.HandleAsync(h.Client, new(Phone, "login", "whatsapp"), default)); // too soon
        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<AppException>(() => verify.HandleAsync(h.Client.Id, req.ChallengeId, "000000", default));

        var lines = requestLog.Lines.Concat(verifyLog.Lines).ToList();
        Assert.NotEmpty(lines);
        Assert.All(lines, line =>
        {
            Assert.DoesNotContain("201001234567", line);
            Assert.DoesNotContain("123456", line);
        });
        Assert.Contains(lines, l => l.Contains("locked", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lines, l => l.Contains("+20") && l.Contains("567") && l.Contains('*'));

    }

    [Fact]
    public async Task Delivery_records_hold_the_masked_number_only_and_never_the_text()
    {
        var h = await Harness.CreateConnectedAsync();
        await h.RequestOtp().HandleAsync(h.Client, new(Phone, "login", "whatsapp"), default);
        await h.SendText().HandleAsync(h.Client, new SendTextCommand(null, Phone, null, "private text"), default);

        foreach (var message in h.Store.Messages)
            Assert.DoesNotContain("201001234567", message.Target);
        var otpRecord = h.Store.Messages.First();
        Assert.Null(otpRecord.Body);
        Assert.Null(otpRecord.Destination);
        // the pending message keeps its content only until delivery
        var pending = h.Store.Messages.Last();
        Assert.Equal("private text", pending.Body);
        await h.Sender().SendAsync(pending, default);
        Assert.Null(pending.Body);
        Assert.Null(pending.Destination);
    }

    [Fact]
    public async Task Sender_redacts_phone_numbers_from_provider_errors()
    {
        var h = await Harness.CreateConnectedAsync();
        await h.SendText().HandleAsync(h.Client, new SendTextCommand(null, Phone, null, "x"), default);
        var message = h.Store.Messages.Single();
        var sender = new MessageSender(h.Store, new LeakyProvider(), h.Webhooks, h.Outbox, h.Pacer, h.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<MessageSender>.Instance);

        await sender.SendAsync(message, default);

        Assert.Equal(MessageStatus.Failed, message.Status);
        Assert.DoesNotContain("201001234567", message.Error);
    }

    [Fact]
    public async Task Webhook_fires_only_for_terminal_outcomes_not_for_a_scheduled_retry()
    {
        var h = await Harness.CreateConnectedAsync();
        await h.SendText().HandleAsync(h.Client, new SendTextCommand(null, Phone, null, "x"), default);
        var message = h.Store.Messages.Single();

        h.WhatsApp.FailSends = true;
        h.WhatsApp.FailTransiently = true;
        await h.Sender().SendAsync(message, default);
        Assert.Empty(h.Webhooks.Events);
        Assert.Equal(MessageStatus.Queued, message.Status);

        h.WhatsApp.FailSends = false;
        await h.Sender().SendAsync(message, default);
        Assert.Equal("message.sent", Assert.Single(h.Webhooks.Events).Event);
    }

    [Fact]
    public async Task Provider_failure_while_creating_a_session_marks_it_failed_and_surfaces_the_error()
    {
        var h = new Harness();
        var handler = new CreateSessionHandler(h.Store, new ThrowingProvider(), h.Clock);

        await Assert.ThrowsAsync<Rasel.Application.Ports.ProviderException>(() => handler.HandleAsync(h.Client.Id, "main", default));
        Assert.Equal(SessionStatus.Failed, h.Store.Sessions.Single().Status);
    }

    private sealed class LeakyProvider : FakeWhatsAppProviderBase
    {
        public override Task SendTextToPhoneAsync(Guid sessionId, string phoneE164, string text, Guid messageId, CancellationToken ct) =>
            throw new Rasel.Application.Ports.ProviderException($"cannot reach {phoneE164} right now");
    }

    private sealed class ThrowingProvider : FakeWhatsAppProviderBase
    {
        public override Task CreateSessionAsync(Guid sessionId, CancellationToken ct) =>
            throw new Rasel.Application.Ports.ProviderException("sidecar down", isTransient: true);
    }

    private abstract class FakeWhatsAppProviderBase : Rasel.Application.Ports.IWhatsAppProvider
    {
        public virtual Task CreateSessionAsync(Guid sessionId, CancellationToken ct) => Task.CompletedTask;
        public virtual Task StopSessionAsync(Guid sessionId, CancellationToken ct) => Task.CompletedTask;
        public virtual Task<string?> GetQrAsync(Guid sessionId, CancellationToken ct) => Task.FromResult<string?>(null);
        public virtual Task<SessionStatus> GetStatusAsync(Guid sessionId, CancellationToken ct) => Task.FromResult(SessionStatus.Connected);
        public virtual Task SendTextToPhoneAsync(Guid sessionId, string phoneE164, string text, Guid messageId, CancellationToken ct) => Task.CompletedTask;
        public virtual Task SendTextToGroupAsync(Guid sessionId, string groupName, string text, Guid messageId, CancellationToken ct) => Task.CompletedTask;
        public virtual Task<IReadOnlyList<Rasel.Application.Ports.GroupInfo>> FindGroupsByNameAsync(Guid sessionId, string exactName, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<Rasel.Application.Ports.GroupInfo>>([]);
    }
}

