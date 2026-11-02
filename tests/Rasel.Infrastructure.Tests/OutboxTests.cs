using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Domain.Entities;
using Rasel.Domain.Enums;
using Rasel.Infrastructure.Messaging;

namespace Rasel.Infrastructure.Tests;

public class OutboxTests
{
    private const string Phone = "+201001234567";

    private static OutboxWorker Worker(Rig rig) => new(
        rig.Services.GetRequiredService<IServiceScopeFactory>(), rig.Services.GetRequiredService<OutboxOptions>(), rig.Clock, NullLogger<OutboxWorker>.Instance);

    private static async Task<Guid> QueueAsync(Rig rig, Guid sessionId, string body = "hello")
    {
        var message = Message.Queue(rig.Client.Id, sessionId, TargetType.Phone, "+20*******567", Phone, body, null, null, rig.Clock.UtcNow);
        await rig.InScopeAsync(async sp => await sp.GetRequiredService<IMessageRepository>().TryAddAsync(message, default));
        return message.Id;
    }

    [Fact]
    public async Task A_message_persisted_before_a_restart_is_delivered_by_a_fresh_worker_and_its_content_is_wiped()
    {
        await using var rig = await Rig.CreateAsync();
        var sessionId = await rig.ConnectedSessionAsync();
        var id = await QueueAsync(rig, sessionId);

        // nothing in memory knows about the message: the worker only reads the database
        Assert.Equal(1, await Worker(rig).ProcessBatchAsync(default));

        var sent = Assert.Single(rig.WhatsApp.SentMessages);
        Assert.Equal("hello", sent.Text);
        var stored = Assert.Single(await rig.AllMessagesAsync());
        Assert.Equal(id, stored.Id);
        Assert.Equal(MessageStatus.Sent, stored.Status);
        Assert.Null(stored.Body);
        Assert.Null(stored.Destination);
    }

    [Fact]
    public async Task A_leased_message_is_not_claimed_twice_until_the_lease_expires()
    {
        await using var rig = await Rig.CreateAsync(new() { ["Outbox:LeaseSeconds"] = "60" });
        var sessionId = await rig.ConnectedSessionAsync();
        await QueueAsync(rig, sessionId);

        var repo = rig.Services.CreateAsyncScope();
        var repository = repo.ServiceProvider.GetRequiredService<IMessageRepository>();
        Assert.Single(await repository.ClaimDueAsync(rig.Clock.UtcNow, TimeSpan.FromSeconds(60), 10, default));
        Assert.Empty(await rig.InScopeAsync(async sp => await sp.GetRequiredService<IMessageRepository>().ClaimDueAsync(rig.Clock.UtcNow, TimeSpan.FromSeconds(60), 10, default)));

        rig.Clock.Advance(TimeSpan.FromSeconds(61)); // the worker that held the lease is presumed dead
        Assert.Single(await rig.InScopeAsync(async sp => await sp.GetRequiredService<IMessageRepository>().ClaimDueAsync(rig.Clock.UtcNow, TimeSpan.FromSeconds(60), 10, default)));
    }

    [Fact]
    public async Task Transient_failures_are_retried_with_backoff_then_fail_after_max_attempts()
    {
        await using var rig = await Rig.CreateAsync(new() { ["Outbox:MaxAttempts"] = "3", ["Outbox:BaseBackoffSeconds"] = "5" });
        var sessionId = await rig.ConnectedSessionAsync();
        rig.WhatsApp.FailSends = true;
        rig.WhatsApp.FailTransiently = true;
        await QueueAsync(rig, sessionId);
        var worker = Worker(rig);

        await worker.ProcessBatchAsync(default);
        var afterFirst = Assert.Single(await rig.AllMessagesAsync());
        Assert.Equal(MessageStatus.Queued, afterFirst.Status);
        Assert.Equal(rig.Clock.UtcNow.AddSeconds(5), afterFirst.NextAttemptAt);
        Assert.Equal("hello", afterFirst.Body); // still needed for the retry

        Assert.Equal(0, await worker.ProcessBatchAsync(default)); // not due yet
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        await worker.ProcessBatchAsync(default);
        Assert.Equal(rig.Clock.UtcNow.AddSeconds(10), (await rig.AllMessagesAsync()).Single().NextAttemptAt); // exponential

        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        await worker.ProcessBatchAsync(default);
        var final = Assert.Single(await rig.AllMessagesAsync());
        Assert.Equal(MessageStatus.Failed, final.Status);
        Assert.Equal(3, final.Attempts);
        Assert.Null(final.Body);
    }

    [Fact]
    public async Task A_transient_failure_that_recovers_is_delivered_on_the_retry()
    {
        await using var rig = await Rig.CreateAsync();
        var sessionId = await rig.ConnectedSessionAsync();
        rig.WhatsApp.FailSends = true;
        rig.WhatsApp.FailTransiently = true;
        await QueueAsync(rig, sessionId);
        var worker = Worker(rig);

        await worker.ProcessBatchAsync(default);
        rig.WhatsApp.FailSends = false;
        rig.Clock.Advance(TimeSpan.FromSeconds(6));
        await worker.ProcessBatchAsync(default);

        Assert.Equal(MessageStatus.Sent, (await rig.AllMessagesAsync()).Single().Status);
        Assert.Single(rig.WhatsApp.SentMessages);
    }

    [Fact]
    public async Task A_permanent_failure_is_not_retried_and_the_error_is_redacted()
    {
        await using var rig = await Rig.CreateAsync();
        var sessionId = await rig.ConnectedSessionAsync();
        rig.WhatsApp.FailSends = true; // permanent
        await QueueAsync(rig, sessionId);

        await Worker(rig).ProcessBatchAsync(default);

        var stored = Assert.Single(await rig.AllMessagesAsync());
        Assert.Equal(MessageStatus.Failed, stored.Status);
        Assert.Equal(1, stored.Attempts);
        Assert.Equal(0, await Worker(rig).ProcessBatchAsync(default));
    }
}
