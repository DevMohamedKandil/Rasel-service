using Rasel.Application.Common;
using Rasel.Application.UseCases;
using Rasel.Domain.Enums;

namespace Rasel.Application.Tests;

/// <summary>A clock whose timers fire at once but move the time forward and record the requested delay.</summary>
public sealed class AutoAdvanceClock : TimeProvider
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    public List<TimeSpan> Delays { get; } = [];
    public override DateTimeOffset GetUtcNow() => UtcNow;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            Delays.Add(dueTime);
            UtcNow += dueTime;
            Task.Run(() => callback(state));
        }
        return new NoopTimer();
    }

    private sealed class NoopTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Timers created while <see cref="Gate"/> is set complete only when the gate task completes.</summary>
public sealed class GateClock : TimeProvider
{
    public Task? Gate { get; set; }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (Gate is { } g && dueTime != Timeout.InfiniteTimeSpan)
        {
            _ = g.ContinueWith(_ => callback(state));
            return new Noop();
        }
        return base.CreateTimer(callback, state, dueTime, period);
    }

    private sealed class Noop : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public class PacerTests
{
    private static readonly PacingOptions Opts = new() { MinDelayMs = 1000, MaxDelayMs = 5000, MinGapMs = 3000 };
    private static readonly Guid S = Guid.NewGuid();

    [Fact]
    public async Task Bulk_waits_the_jitter_and_keeps_the_minimum_gap()
    {
        var clock = new AutoAdvanceClock();
        var rnd = new FakeRandom();
        rnd.Values.Enqueue(1500);   // first send: jitter 1500
        rnd.Values.Enqueue(1200);   // second: jitter is below the gap, so the gap wins
        var pacer = new SendPacer(Opts, clock, rnd);

        await pacer.WaitTurnAsync(S, SendPriority.Bulk, default);
        await pacer.WaitTurnAsync(S, SendPriority.Bulk, default);

        Assert.Equal([TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(3000)], clock.Delays);
    }

    [Fact]
    public async Task Sessions_are_paced_independently()
    {
        var clock = new AutoAdvanceClock();
        var pacer = new SendPacer(Opts, clock, new FakeRandom());
        await pacer.WaitTurnAsync(S, SendPriority.Bulk, default);
        await pacer.WaitTurnAsync(Guid.NewGuid(), SendPriority.Bulk, default);
        Assert.Equal([TimeSpan.FromMilliseconds(1000), TimeSpan.FromMilliseconds(1000)], clock.Delays);
    }

    [Fact]
    public async Task Otp_on_an_idle_session_is_not_delayed()
    {
        var clock = new AutoAdvanceClock();
        var pacer = new SendPacer(Opts, clock, new FakeRandom());
        await pacer.WaitTurnAsync(S, SendPriority.Otp, default);
        Assert.Empty(clock.Delays);
    }

    [Fact]
    public async Task Otp_right_after_a_send_waits_only_the_jitter_never_the_gap()
    {
        var clock = new AutoAdvanceClock();
        var pacer = new SendPacer(Opts, clock, new FakeRandom());
        await pacer.WaitTurnAsync(S, SendPriority.Bulk, default);   // 1000ms, last send = now
        clock.Delays.Clear();

        await pacer.WaitTurnAsync(S, SendPriority.Otp, default);

        Assert.Equal([TimeSpan.FromMilliseconds(1000)], clock.Delays);   // jitter only; the gap is 3000
    }

    [Fact]
    public async Task Bulk_yields_while_an_otp_is_waiting()
    {
        var clock = new GateClock();
        var pacer = new SendPacer(new PacingOptions { MinDelayMs = 10, MaxDelayMs = 10, MinGapMs = 1000 }, clock, new FakeRandom());
        await pacer.WaitTurnAsync(S, SendPriority.Bulk, default);   // session now "recently used"

        var gate = new TaskCompletionSource();
        clock.Gate = gate.Task;
        var otp = pacer.WaitTurnAsync(S, SendPriority.Otp, default);   // waits on the gated jitter
        var bulk = Task.Run(() => pacer.WaitTurnAsync(S, SendPriority.Bulk, default));
        await Task.Delay(300);
        Assert.False(otp.IsCompleted);
        Assert.False(bulk.IsCompleted);

        clock.Gate = null;
        gate.SetResult();
        await otp.WaitAsync(TimeSpan.FromSeconds(5));
        await bulk.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Sender_waits_for_the_pacer_before_calling_the_provider()
    {
        var h = await Harness.CreateConnectedAsync();
        h.Pacing.MinDelayMs = h.Pacing.MaxDelayMs = 40;
        var sent = await h.SendText().HandleAsync(h.Client, new(null, "+201001234567", null, "hi"), default);
        var msg = h.Store.Messages.Single(m => m.Id == sent.MessageId);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await h.Sender().SendAsync(msg, default);
        Assert.True(sw.ElapsedMilliseconds >= 30, $"elapsed {sw.ElapsedMilliseconds}");
        Assert.Equal(MessageStatus.Sent, msg.Status);
    }

    [Fact]
    public void Pacing_options_are_validated()
    {
        var v = new PacingOptionsValidator();
        Assert.True(v.Validate(null, new PacingOptions()).Succeeded);
        Assert.False(v.Validate(null, new PacingOptions { MinDelayMs = 500, MaxDelayMs = 100 }).Succeeded);
        Assert.False(v.Validate(null, new PacingOptions { MaxDelayMs = 120_000 }).Succeeded);
        Assert.False(v.Validate(null, new PacingOptions { MinGapMs = -1 }).Succeeded);
        Assert.False(v.Validate(null, new PacingOptions { DailyPerRecipient = 0 }).Succeeded);
    }
}

public class RecipientLimitTests
{
    private static RequestOtpCommand Otp(string phone) => new(phone, "login", "whatsapp");

    [Fact]
    public async Task Daily_cap_is_per_client_and_recipient_and_resets_next_day()
    {
        var h = await Harness.CreateConnectedAsync();
        h.Pacing.DailyPerRecipient = 2;
        await h.SendText().HandleAsync(h.Client, new(null, "+201001234567", null, "a"), default);
        await h.SendText().HandleAsync(h.Client, new(null, "+201001234567", null, "b"), default);

        var ex = await Assert.ThrowsAsync<AppException>(() =>
            h.SendText().HandleAsync(h.Client, new(null, "+201001234567", null, "c"), default));
        Assert.Equal("recipient_daily_limit", ex.Code);
        Assert.Equal(ErrorKind.TooManyRequests, ex.Kind);

        await h.SendText().HandleAsync(h.Client, new(null, "+201001234568", null, "a"), default);   // other recipient

        h.Clock.Advance(TimeSpan.FromHours(13));
        await h.SendText().HandleAsync(h.Client, new(null, "+201001234567", null, "d"), default);
    }

    [Fact]
    public async Task Otp_requests_count_toward_the_cap_and_the_cooldown_still_applies_first()
    {
        var h = await Harness.CreateConnectedAsync();
        h.Pacing.DailyPerRecipient = 2;
        h.Options.PerPhonePerHour = 100;
        await h.RequestOtp().HandleAsync(h.Client, Otp("+201001234567"), default);
        var tooSoon = await Assert.ThrowsAsync<AppException>(() => h.RequestOtp().HandleAsync(h.Client, Otp("+201001234567"), default));
        Assert.Equal("otp_resend_too_soon", tooSoon.Code);

        h.Clock.Advance(TimeSpan.FromSeconds(61));
        await h.RequestOtp().HandleAsync(h.Client, Otp("+201001234567"), default);
        h.Clock.Advance(TimeSpan.FromSeconds(61));
        var ex = await Assert.ThrowsAsync<AppException>(() => h.RequestOtp().HandleAsync(h.Client, Otp("+201001234567"), default));
        Assert.Equal("recipient_daily_limit", ex.Code);
    }

    [Fact]
    public async Task Stored_recipient_key_is_not_the_phone_number()
    {
        var h = await Harness.CreateConnectedAsync();
        await h.SendText().HandleAsync(h.Client, new(null, "+201001234567", null, "a"), default);
        var key = h.Store.Messages.Single().RecipientKey;
        Assert.NotNull(key);
        Assert.DoesNotContain("1234567", key);
    }
}

public class OtpTemplateTests
{
    private static bool HasArabic(string s) => s.Any(c => c is >= '؀' and <= 'ۿ');

    [Fact]
    public void Defaults_have_at_least_five_variants_per_language_with_app_and_code()
    {
        Assert.True(OtpMessageComposer.DefaultEn.Length >= 5);
        Assert.True(OtpMessageComposer.DefaultAr.Length >= 5);
        foreach (var t in OtpMessageComposer.DefaultEn.Concat(OtpMessageComposer.DefaultAr))
            Assert.True(t.Contains("{app}") && t.Contains("{code}"), t);
        Assert.All(OtpMessageComposer.DefaultAr, t => Assert.True(HasArabic(t)));
    }

    [Fact]
    public void Every_variant_renders_with_app_name_and_code_and_the_language_is_honoured()
    {
        var rnd = new FakeRandom();
        var composer = new OtpMessageComposer(new OtpOptions { AppName = "Acme" }, rnd);
        for (var i = 0; i < OtpMessageComposer.DefaultEn.Length; i++)
        {
            rnd.Values.Enqueue(i);
            var text = composer.Compose("987654", "en");
            Assert.Contains("Acme", text);
            Assert.Contains("987654", text);
            Assert.DoesNotContain("{", text);
        }
        for (var i = 0; i < OtpMessageComposer.DefaultAr.Length; i++)
        {
            rnd.Values.Enqueue(i);
            var text = composer.Compose("987654", "AR");
            Assert.Contains("Acme", text);
            Assert.Contains("987654", text);
            Assert.True(HasArabic(text));
        }
    }

    [Fact]
    public void Different_random_picks_give_different_texts()
    {
        var rnd = new FakeRandom();
        var composer = new OtpMessageComposer(new OtpOptions(), rnd);
        rnd.Values.Enqueue(0);
        var a = composer.Compose("111111", null);
        rnd.Values.Enqueue(1);
        var b = composer.Compose("111111", null);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task Request_uses_the_language_from_the_command()
    {
        var h = await Harness.CreateConnectedAsync();
        await h.RequestOtp().HandleAsync(h.Client, new("+201001234567", "login", "whatsapp", "ar"), default);
        var text = Assert.Single(h.WhatsApp.SentMessages).Text;
        Assert.Contains("123456", text);
        Assert.True(HasArabic(text));
    }

    [Fact]
    public void Custom_templates_must_contain_app_and_code()
    {
        var v = new OtpTemplatesValidator();
        Assert.True(v.Validate(null, new OtpOptions()).Succeeded);
        Assert.True(v.Validate(null, new OtpOptions { TemplatesEn = ["{app} {code}"] }).Succeeded);
        Assert.False(v.Validate(null, new OtpOptions { TemplatesEn = ["no placeholders"] }).Succeeded);
        Assert.False(v.Validate(null, new OtpOptions { TemplatesAr = ["{code} only"] }).Succeeded);
        Assert.False(v.Validate(null, new OtpOptions { TemplatesEn = [] }).Succeeded);
    }
}

public class GetMessageTests
{
    [Fact]
    public async Task Returns_status_without_body_or_destination_and_hides_other_clients()
    {
        var h = await Harness.CreateConnectedAsync();
        var sent = await h.SendText().HandleAsync(h.Client, new(null, "+201001234567", null, "secret body"), default);
        var handler = new GetMessageHandler(h.Store);

        var dto = await handler.HandleAsync(h.Client.Id, sent.MessageId, default);
        Assert.Equal("Queued", dto.Status);
        Assert.Equal(0, dto.Attempts);
        var json = System.Text.Json.JsonSerializer.Serialize(dto);
        Assert.DoesNotContain("secret body", json);
        Assert.DoesNotContain("1234567", json);

        var other = await Assert.ThrowsAsync<AppException>(() => handler.HandleAsync(Guid.NewGuid(), sent.MessageId, default));
        Assert.Equal(ErrorKind.NotFound, other.Kind);
        await Assert.ThrowsAsync<AppException>(() => handler.HandleAsync(h.Client.Id, Guid.NewGuid(), default));
    }

    [Fact]
    public async Task Otp_message_exposes_no_code_and_failure_shows_only_a_coarse_error_code()
    {
        var h = await Harness.CreateConnectedAsync();
        await h.RequestOtp().HandleAsync(h.Client, new("+201001234567", "login", "whatsapp"), default);
        var id = h.Store.Messages.Single().Id;
        var json = System.Text.Json.JsonSerializer.Serialize(await new GetMessageHandler(h.Store).HandleAsync(h.Client.Id, id, default));
        Assert.DoesNotContain("123456", json);
        Assert.Contains("Sent", json);

        h.WhatsApp.FailSends = true;
        var sent = await h.SendText().HandleAsync(h.Client, new(null, "+201001234999", null, "x"), default);
        var msg = h.Store.Messages.Single(m => m.Id == sent.MessageId);
        await h.Sender().SendAsync(msg, default);
        var dto = await new GetMessageHandler(h.Store).HandleAsync(h.Client.Id, sent.MessageId, default);
        Assert.Equal("Failed", dto.Status);
        Assert.Equal("provider_error", dto.LastErrorCode);
    }
}
