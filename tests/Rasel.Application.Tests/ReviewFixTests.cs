using Microsoft.Extensions.Logging.Abstractions;
using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Application.UseCases;
using Rasel.Domain.Enums;

namespace Rasel.Application.Tests;

public class ReviewFixTests
{
    private const string Phone = "+201001234567";

    private class BuggyProvider : IWhatsAppProvider
    {
        public Task CreateSessionAsync(Guid sessionId, CancellationToken ct) => Task.CompletedTask;
        public Task StopSessionAsync(Guid sessionId, CancellationToken ct) => Task.CompletedTask;
        public Task LogoutAsync(Guid sessionId, CancellationToken ct) => Task.CompletedTask;
        public Task<string?> GetQrAsync(Guid sessionId, CancellationToken ct) => Task.FromResult<string?>(null);
        public Task<SessionStatus> GetStatusAsync(Guid sessionId, CancellationToken ct) => Task.FromResult(SessionStatus.Connected);
        public virtual Task SendTextToPhoneAsync(Guid sessionId, string phoneE164, string text, Guid messageId, CancellationToken ct) =>
            throw new InvalidOperationException("provider bug with +201001234567 inside");
        public Task SendTextToGroupAsync(Guid sessionId, string groupName, string text, Guid messageId, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<GroupInfo>> FindGroupsByNameAsync(Guid sessionId, string exactName, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GroupInfo>>([]);
    }

    private sealed class CancellingProvider(CancellationTokenSource cts) : BuggyProvider
    {
        public override Task SendTextToPhoneAsync(Guid sessionId, string phoneE164, string text, Guid messageId, CancellationToken ct)
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Unexpected_provider_exception_counts_as_an_attempt_and_eventually_fails_instead_of_wedging_the_queue()
    {
        var h = await Harness.CreateConnectedAsync();
        await h.SendText().HandleAsync(h.Client, new SendTextCommand(null, Phone, null, "x"), default);
        var message = h.Store.Messages.Single();
        var sender = new MessageSender(h.Store, new BuggyProvider(), h.Webhooks, h.Outbox, h.Pacer, h.Clock, NullLogger<MessageSender>.Instance);

        await sender.SendAsync(message, default);
        Assert.Equal(MessageStatus.Queued, message.Status);
        Assert.Equal(1, message.Attempts);
        Assert.DoesNotContain("2010", message.Error);

        for (var i = 1; i < h.Outbox.MaxAttempts; i++) await sender.SendAsync(message, default);
        Assert.Equal(MessageStatus.Failed, message.Status);
        Assert.Equal("message.failed", Assert.Single(h.Webhooks.Events).Event);
    }

    private sealed class NotReadyProvider : BuggyProvider
    {
        public override Task SendTextToPhoneAsync(Guid sessionId, string phoneE164, string text, Guid messageId, CancellationToken ct) =>
            throw new ProviderException("The WhatsApp engine of this session is not ready yet.", isTransient: true, isNotReady: true);
    }

    [Fact]
    public async Task A_not_ready_session_defers_the_message_without_using_attempts_until_it_is_too_old()
    {
        var h = await Harness.CreateConnectedAsync();
        await h.SendText().HandleAsync(h.Client, new SendTextCommand(null, Phone, null, "x"), default);
        var message = h.Store.Messages.Single();
        var sender = new MessageSender(h.Store, new NotReadyProvider(), h.Webhooks, h.Outbox, h.Pacer, h.Clock, NullLogger<MessageSender>.Instance);

        for (var i = 0; i < h.Outbox.MaxAttempts + 3; i++) await sender.SendAsync(message, default);
        Assert.Equal(MessageStatus.Queued, message.Status); // still waiting, nothing burnt
        Assert.Equal(0, message.Attempts);
        Assert.True(message.NextAttemptAt > h.Clock.GetUtcNow());

        h.Clock.Advance(TimeSpan.FromMinutes(h.Outbox.NotReadyMaxAgeMinutes + 1)); // older than the documented limit: normal transient accounting
        for (var i = 0; i < h.Outbox.MaxAttempts; i++) await sender.SendAsync(message, default);
        Assert.Equal(MessageStatus.Failed, message.Status);
    }

    [Fact]
    public async Task Cancellation_is_not_recorded_as_a_failed_attempt()
    {
        var h = await Harness.CreateConnectedAsync();
        await h.SendText().HandleAsync(h.Client, new SendTextCommand(null, Phone, null, "x"), default);
        var message = h.Store.Messages.Single();
        using var cts = new CancellationTokenSource();
        var sender = new MessageSender(h.Store, new CancellingProvider(cts), h.Webhooks, h.Outbox, h.Pacer, h.Clock, NullLogger<MessageSender>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(message, cts.Token));
        Assert.Equal(0, message.Attempts);
    }

    [Fact]
    public async Task Overlong_group_name_is_rejected_before_anything_is_stored()
    {
        var h = await Harness.CreateConnectedAsync();
        var ex = await Assert.ThrowsAsync<AppException>(() =>
            h.SendText().HandleAsync(h.Client, new SendTextCommand(null, null, new string('g', 129), "x"), default));
        Assert.Equal("invalid_target", ex.Code);
        Assert.Empty(h.Store.Messages);
    }
}
