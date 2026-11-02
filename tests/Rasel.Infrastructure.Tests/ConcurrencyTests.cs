using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Application.UseCases;
using Rasel.Domain.Entities;
using Rasel.Domain.Enums;
using Rasel.Infrastructure.Persistence;

namespace Rasel.Infrastructure.Tests;

/// <summary>Real SQLite, real handlers, many parallel scopes: proves the races are closed, not just that the happy path works.</summary>
public class ConcurrencyTests
{
    private const string Phone = "+201001234567";

    private static async Task<string> RunAsync(Func<Task> action)
    {
        try
        {
            await action();
            return "ok";
        }
        catch (AppException ex)
        {
            return ex.Code;
        }
    }

    private static async Task<(Guid Id, string Code)> RequestAsync(Rig rig)
    {
        var result = await rig.InScopeAsync(sp => sp.GetRequiredService<RequestOtpHandler>()
            .HandleAsync(rig.Client, new RequestOtpCommand(Phone, "login", "whatsapp"), default));
        var code = Regex.Match(rig.WhatsApp.SentMessages.Last().Text, @"\b\d{6}\b").Value;
        return (result.ChallengeId, code);
    }

    private static Task<string[]> ParallelAsync(int count, Func<Task<string>> work) =>
        Task.WhenAll(Enumerable.Range(0, count).Select(_ => Task.Run(work)));

    [Fact]
    public async Task Parallel_verifies_with_the_correct_code_produce_exactly_one_token()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.ConnectedSessionAsync();
        var (id, code) = await RequestAsync(rig);

        var results = await ParallelAsync(20, () => RunAsync(() => rig.InScopeAsync(sp =>
            sp.GetRequiredService<VerifyOtpHandler>().HandleAsync(rig.Client.Id, id, code, default))));

        Assert.Equal(1, results.Count(r => r == "ok"));
        Assert.All(results.Where(r => r != "ok"), r => Assert.Equal("otp_already_used", r));
    }

    [Fact]
    public async Task Parallel_wrong_guesses_can_never_exceed_the_attempt_limit()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.ConnectedSessionAsync();
        var (id, code) = await RequestAsync(rig);
        var wrong = code == "000000" ? "111111" : "000000";

        var results = await ParallelAsync(30, () => RunAsync(() => rig.InScopeAsync(sp =>
            sp.GetRequiredService<VerifyOtpHandler>().HandleAsync(rig.Client.Id, id, wrong, default))));

        Assert.Equal(5, results.Count(r => r == "otp_invalid"));
        Assert.Equal(25, results.Count(r => r == "otp_locked"));
        var stored = await rig.InScopeAsync(sp => sp.GetRequiredService<RaselDbContext>().OtpChallenges.AsNoTracking().SingleAsync(c => c.Id == id));
        Assert.Equal(5, stored.Attempts);
        Assert.Equal(OtpStatus.Locked, stored.Status);

        // once locked, even the correct code is refused
        var after = await RunAsync(() => rig.InScopeAsync(sp => sp.GetRequiredService<VerifyOtpHandler>().HandleAsync(rig.Client.Id, id, code, default)));
        Assert.Equal("otp_locked", after);
    }

    [Fact]
    public async Task Parallel_code_requests_for_one_phone_create_a_single_active_challenge()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.ConnectedSessionAsync();

        var results = await ParallelAsync(10, () => RunAsync(() => rig.InScopeAsync(sp => sp.GetRequiredService<RequestOtpHandler>()
            .HandleAsync(rig.Client, new RequestOtpCommand(Phone, "login", "whatsapp"), default))));

        Assert.Equal(1, results.Count(r => r == "ok"));
        Assert.All(results.Where(r => r != "ok"), r => Assert.Contains(r, new[] { "otp_resend_too_soon", "otp_rate_limited" }));
        var active = await rig.InScopeAsync(sp => sp.GetRequiredService<RaselDbContext>().OtpChallenges
            .CountAsync(c => c.Status == OtpStatus.Active));
        Assert.Equal(1, active);
        Assert.Single(rig.WhatsApp.SentMessages);
    }

    [Fact]
    public async Task Parallel_sends_with_the_same_idempotency_key_create_one_message()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.ConnectedSessionAsync();
        var cmd = new SendTextCommand(null, Phone, null, "hello", "key-1");

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => rig.InScopeAsync(sp =>
            sp.GetRequiredService<SendTextHandler>().HandleAsync(rig.Client, cmd, default)))));

        Assert.Single(results.Select(r => r.MessageId).Distinct());
        Assert.Equal(9, results.Count(r => r.Replayed));
        Assert.Single(await rig.AllMessagesAsync());
    }

    [Fact]
    public async Task Idempotency_key_reuse_with_a_different_payload_is_rejected()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.ConnectedSessionAsync();
        await rig.InScopeAsync(sp => sp.GetRequiredService<SendTextHandler>().HandleAsync(rig.Client, new(null, Phone, null, "hello", "k"), default));

        var code = await RunAsync(() => rig.InScopeAsync(sp =>
            sp.GetRequiredService<SendTextHandler>().HandleAsync(rig.Client, new(null, Phone, null, "different", "k"), default)));
        Assert.Equal("idempotency_key_reused", code);
    }

    [Fact]
    public async Task Concurrent_outbox_claimers_never_get_the_same_message()
    {
        await using var rig = await Rig.CreateAsync();
        var sessionId = await rig.ConnectedSessionAsync();
        await rig.InScopeAsync(async sp =>
        {
            var repo = sp.GetRequiredService<IMessageRepository>();
            for (var i = 0; i < 20; i++)
                await repo.TryAddAsync(Message.Queue(rig.Client.Id, sessionId, TargetType.Phone, "+20*******567", Phone, $"m{i}", null, null, rig.Clock.UtcNow), default);
            return 0;
        });

        var claimed = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => rig.InScopeAsync(async sp =>
        {
            var repo = sp.GetRequiredService<IMessageRepository>();
            var ids = new List<Guid>();
            while (true)
            {
                var batch = await repo.ClaimDueAsync(rig.Clock.UtcNow, TimeSpan.FromMinutes(2), 3, default);
                if (batch.Count == 0) return ids;
                ids.AddRange(batch.Select(m => m.Id));
            }
        }))));

        var all = claimed.SelectMany(x => x).ToList();
        Assert.Equal(20, all.Count);
        Assert.Equal(20, all.Distinct().Count());
    }

    [Fact]
    public async Task Stale_writer_loses_with_a_concurrency_conflict_instead_of_overwriting()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.ConnectedSessionAsync();
        var (id, _) = await RequestAsync(rig);

        await using var a = rig.Services.CreateAsyncScope();
        await using var b = rig.Services.CreateAsyncScope();
        var repoA = a.ServiceProvider.GetRequiredService<IOtpRepository>();
        var repoB = b.ServiceProvider.GetRequiredService<IOtpRepository>();
        var first = (await repoA.GetAsync(id, rig.Client.Id, default))!;
        var second = (await repoB.GetAsync(id, rig.Client.Id, default))!;

        first.Attempt(false, rig.Clock.UtcNow);
        second.Attempt(false, rig.Clock.UtcNow);
        Assert.True(await repoA.TrySaveAsync([first], default));
        Assert.False(await repoB.TrySaveAsync([second], default));

        var stored = await rig.InScopeAsync(sp => sp.GetRequiredService<RaselDbContext>().OtpChallenges.AsNoTracking().SingleAsync(c => c.Id == id));
        Assert.Equal(1, stored.Attempts);
    }
}
