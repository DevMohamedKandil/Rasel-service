using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Rasel.Application.Common;
using Rasel.Infrastructure.Configuration;
using Rasel.Infrastructure.Messaging;
using Rasel.Infrastructure.Persistence;
using Rasel.Infrastructure.Security;

namespace Rasel.Infrastructure.Tests;

public class InfrastructureUnitTests
{
    [Fact]
    public void Rate_limiter_enforces_the_window_and_evicts_idle_keys()
    {
        var clock = new FakeClock();
        var limiter = new SlidingWindowRateLimiter(clock);

        Assert.True(limiter.TryAcquire("k", 2, TimeSpan.FromMinutes(1)));
        Assert.True(limiter.TryAcquire("k", 2, TimeSpan.FromMinutes(1)));
        Assert.False(limiter.TryAcquire("k", 2, TimeSpan.FromMinutes(1)));
        Assert.True(limiter.IsExceeded("k", 2, TimeSpan.FromMinutes(1)));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.False(limiter.IsExceeded("k", 2, TimeSpan.FromMinutes(1)));

        for (var i = 0; i < 1000; i++) limiter.TryAcquire($"random-phone-{i}", 5, TimeSpan.FromHours(1));
        Assert.True(limiter.TrackedKeys >= 1000);
        clock.Advance(TimeSpan.FromHours(2));
        for (var i = 0; i < 600; i++) limiter.TryAcquire("trigger", 5, TimeSpan.FromHours(1));
        Assert.True(limiter.TrackedKeys < 10, $"idle windows were not evicted ({limiter.TrackedKeys} left)");
    }

    [Fact]
    public void Redactor_removes_phone_numbers_and_bounds_length()
    {
        Assert.DoesNotContain("201001234567", PiiRedactor.Redact("send to +20 100 123 4567 failed"));
        Assert.DoesNotContain("201001234567", PiiRedactor.Redact("send to 201001234567 failed"));
        Assert.Equal(PiiRedactor.MaxLength, PiiRedactor.Redact(new string('x', 1000)).Length);
        Assert.Equal("unknown error", PiiRedactor.Redact(null));
    }

    [Fact]
    public void Api_key_comparison_is_hash_based_and_exact()
    {
        var hash = ApiKeyHasher.Hash("secret-key");
        Assert.True(ApiKeyHasher.Matches("secret-key", hash));
        Assert.False(ApiKeyHasher.Matches("secret-key2", hash));
        Assert.False(ApiKeyHasher.Matches("", hash));
    }

    [Fact]
    public void Token_carries_issuer_audience_unique_jti_and_a_valid_signature()
    {
        var clock = new FakeClock();
        var secret = "token-secret-token-secret-token-secret";
        var issuer = new HmacTokenIssuer(secret, clock);
        var a = issuer.Issue(Guid.NewGuid(), "+201001234567", "login", TimeSpan.FromMinutes(5));
        var b = issuer.Issue(Guid.NewGuid(), "+201001234567", "login", TimeSpan.FromMinutes(5));

        string Field(string token, string name)
        {
            var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')));
            return doc.RootElement.GetProperty(name).ToString();
        }

        Assert.Equal("rasel", Field(a, "iss"));
        Assert.Equal(HmacTokenIssuer.Audience, Field(a, "aud"));
        Assert.NotEqual(Field(a, "jti"), Field(b, "jti"));

        var parts = a.Split('.');
        var expected = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}")))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, parts[2]);
    }

    [Fact]
    public async Task Webhook_signature_covers_timestamp_and_body()
    {
        HttpRequestMessage? captured = null;
        byte[] body = [];
        var handler = new CapturingHandler(async r => { captured = r; body = await r.Content!.ReadAsByteArrayAsync(); });
        var clock = new FakeClock();
        const string secret = "webhook-secret-0123456789";
        var dispatcher = new HttpWebhookDispatcher(new HttpClient(handler), new WebhookOptions { Enabled = true, Url = "https://hooks.test/x", Secret = secret },
            clock, NullLogger<HttpWebhookDispatcher>.Instance);

        await dispatcher.DispatchAsync(Guid.NewGuid(), "message.sent", new { messageId = Guid.NewGuid() }, default);

        var timestamp = captured!.Headers.GetValues("X-Rasel-Timestamp").Single();
        Assert.Equal(clock.UtcNow.ToUnixTimeSeconds().ToString(), timestamp);
        var expected = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray())).ToLowerInvariant();
        Assert.Equal(expected, captured.Headers.GetValues("X-Rasel-Signature").Single());
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, Task> capture) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await capture(request);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task Schema_comes_from_committed_migrations_and_matches_the_model()
    {
        await using var rig = await Rig.CreateAsync();
        await rig.InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<RaselDbContext>();
            Assert.Contains(await db.Database.GetAppliedMigrationsAsync(), m => m.EndsWith("InitialCreate"));
            Assert.False(db.Database.HasPendingModelChanges(), "model changed without a new migration");
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());

            var indexes = await db.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type = 'index'").ToListAsync();
            Assert.Contains("UX_OtpChallenges_Active", indexes);
            Assert.Contains(indexes, i => i.Contains("IdempotencyKey"));
            return 0;
        });
    }

    [Theory]
    [InlineData("Production", "dev-only-otp-pepper-change-me-0123456789", "tokentokentokentokentokentokentoken", false)]
    [InlineData("Production", "prod-pepper-0123456789-0123456789-xx", "tokentokentokentokentokentokentoken", true)]
    [InlineData("Development", "dev-only-otp-pepper-change-me-0123456789", "tokentokentokentokentokentokentoken", true)]
    [InlineData("Development", "short", "tokentokentokentokentokentokentoken", false)]
    [InlineData("Development", "same-same-same-same-same-same-same", "same-same-same-same-same-same-same", false)]
    public void Secrets_are_validated_with_placeholders_allowed_only_in_development(string env, string pepper, string token, bool valid)
    {
        var validator = new SecurityOptionsValidator(new TestEnvironment(env));
        Assert.Equal(valid, validator.Validate(null, new SecurityOptions { OtpPepper = pepper, TokenSecret = token }).Succeeded);
    }

    [Fact]
    public void Invalid_options_are_rejected()
    {
        Assert.False(new OtpOptionsValidator().Validate(null, new OtpOptions { MaxAttempts = 0 }).Succeeded);
        Assert.False(new OtpOptionsValidator().Validate(null, new OtpOptions { TtlSeconds = 60, ResendCooldownSeconds = 60 }).Succeeded);
        Assert.False(new OutboxOptionsValidator().Validate(null, new OutboxOptions { MinDelayMs = 100, MaxDelayMs = 10 }).Succeeded);
        Assert.False(new WhatsAppOptionsValidator().Validate(null, new WhatsAppOptions { Provider = "WebJs" }).Succeeded);
        Assert.False(new WhatsAppOptionsValidator().Validate(null, new WhatsAppOptions { Provider = "Nope" }).Succeeded);
        Assert.True(new WhatsAppOptionsValidator().Validate(null, new WhatsAppOptions()).Succeeded);
        Assert.False(new WebhookOptionsValidator(new TestEnvironment("Production"))
            .Validate(null, new WebhookOptions { Enabled = true, Url = "http://hooks.example.com", Secret = new string('s', 20) }).Succeeded);
    }

    [Fact]
    public async Task Startup_options_validation_fails_fast_for_a_bad_configuration()
    {
        await Assert.ThrowsAsync<OptionsValidationException>(async () =>
        {
            await using var rig = await Rig.CreateAsync(new() { ["Otp:MaxAttempts"] = "0" });
            _ = rig.Services.GetRequiredService<OtpOptions>();
        });
    }

    [Fact]
    public void Outbox_backoff_is_exponential_and_capped()
    {
        var o = new OutboxOptions { BaseBackoffSeconds = 5, MaxBackoffSeconds = 60 };
        Assert.Equal(TimeSpan.FromSeconds(5), o.BackoffFor(1));
        Assert.Equal(TimeSpan.FromSeconds(10), o.BackoffFor(2));
        Assert.Equal(TimeSpan.FromSeconds(20), o.BackoffFor(3));
        Assert.Equal(TimeSpan.FromSeconds(60), o.BackoffFor(10));
    }
}
