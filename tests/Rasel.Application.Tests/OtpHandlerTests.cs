using System.Text;
using System.Text.Json;
using Rasel.Application.Common;
using Rasel.Application.UseCases;
using Rasel.Domain.Enums;

namespace Rasel.Application.Tests;

public class OtpHandlerTests
{
    private const string Phone = "+20 100 123 4567";
    private const string E164 = "+201001234567";

    private static RequestOtpCommand Cmd(string purpose = "login", string channel = "whatsapp") => new(Phone, purpose, channel);

    [Fact]
    public async Task Request_sends_code_via_whatsapp_and_stores_only_a_hash()
    {
        var h = await Harness.CreateConnectedAsync();
        var result = await h.RequestOtp().HandleAsync(h.Client, Cmd(), default);

        var sent = Assert.Single(h.WhatsApp.SentMessages);
        Assert.Equal(E164, sent.Target);
        Assert.Contains("123456", sent.Text);
        Assert.Equal(300, result.ExpiresInSeconds);
        Assert.Equal(60, result.ResendAfterSeconds);

        var challenge = Assert.Single(h.Store.Otps);
        Assert.DoesNotContain("123456", challenge.CodeHash);
        Assert.NotEqual(Guid.Empty, result.ChallengeId);
        // delivery record never keeps the text or the full number
        Assert.DoesNotContain(E164, h.Store.Messages.Single().Target);
    }

    [Fact]
    public async Task Correct_code_returns_signed_token_with_expected_claims()
    {
        var h = await Harness.CreateConnectedAsync();
        var req = await h.RequestOtp().HandleAsync(h.Client, Cmd(), default);
        var verified = await h.VerifyOtp().HandleAsync(h.Client.Id, req.ChallengeId, "123456", default);

        var parts = verified.Token.Split('.');
        Assert.Equal(3, parts.Length);
        using var payload = JsonDocument.Parse(Convert.FromBase64String(Pad(parts[1])));
        Assert.Equal(h.Client.Id, payload.RootElement.GetProperty("clientId").GetGuid());
        Assert.Equal(E164, payload.RootElement.GetProperty("phone").GetString());
        Assert.Equal("login", payload.RootElement.GetProperty("purpose").GetString());
        var lifetime = payload.RootElement.GetProperty("exp").GetInt64() - payload.RootElement.GetProperty("iat").GetInt64();
        Assert.Equal(300, lifetime);
    }

    [Fact]
    public async Task Same_challenge_cannot_be_verified_twice()
    {
        var h = await Harness.CreateConnectedAsync();
        var req = await h.RequestOtp().HandleAsync(h.Client, Cmd(), default);
        await h.VerifyOtp().HandleAsync(h.Client.Id, req.ChallengeId, "123456", default);

        var ex = await Assert.ThrowsAsync<AppException>(() => h.VerifyOtp().HandleAsync(h.Client.Id, req.ChallengeId, "123456", default));
        Assert.Equal("otp_already_used", ex.Code);
    }

    [Fact]
    public async Task Locks_after_five_wrong_attempts()
    {
        var h = await Harness.CreateConnectedAsync();
        var req = await h.RequestOtp().HandleAsync(h.Client, Cmd(), default);
        for (var i = 0; i < 5; i++)
        {
            var ex = await Assert.ThrowsAsync<AppException>(() => h.VerifyOtp().HandleAsync(h.Client.Id, req.ChallengeId, "000000", default));
            Assert.Equal("otp_invalid", ex.Code);
        }

        var locked = await Assert.ThrowsAsync<AppException>(() => h.VerifyOtp().HandleAsync(h.Client.Id, req.ChallengeId, "123456", default));
        Assert.Equal(ErrorKind.Locked, locked.Kind);
    }

    [Fact]
    public async Task Expired_code_is_rejected()
    {
        var h = await Harness.CreateConnectedAsync();
        var req = await h.RequestOtp().HandleAsync(h.Client, Cmd(), default);
        h.Clock.Advance(TimeSpan.FromMinutes(5));

        var ex = await Assert.ThrowsAsync<AppException>(() => h.VerifyOtp().HandleAsync(h.Client.Id, req.ChallengeId, "123456", default));
        Assert.Equal("otp_expired", ex.Code);
    }

    [Fact]
    public async Task Resend_within_cooldown_is_rejected_then_allowed_and_invalidates_previous()
    {
        var h = await Harness.CreateConnectedAsync();
        var first = await h.RequestOtp().HandleAsync(h.Client, Cmd(), default);

        var ex = await Assert.ThrowsAsync<AppException>(() => h.RequestOtp().HandleAsync(h.Client, Cmd(), default));
        Assert.Equal(ErrorKind.TooManyRequests, ex.Kind);
        Assert.InRange(ex.RetryAfterSeconds!.Value, 1, 60);

        h.Clock.Advance(TimeSpan.FromSeconds(61));
        h.Codes.Next = "654321";
        var second = await h.RequestOtp().HandleAsync(h.Client, Cmd(), default);

        var old = await Assert.ThrowsAsync<AppException>(() => h.VerifyOtp().HandleAsync(h.Client.Id, first.ChallengeId, "123456", default));
        Assert.Equal("otp_superseded", old.Code);
        await h.VerifyOtp().HandleAsync(h.Client.Id, second.ChallengeId, "654321", default);
    }

    [Fact]
    public async Task Falls_back_to_sms_when_whatsapp_fails()
    {
        var h = await Harness.CreateConnectedAsync();
        h.WhatsApp.FailSends = true;
        await h.RequestOtp().HandleAsync(h.Client, Cmd(), default);

        Assert.Empty(h.WhatsApp.SentMessages);
        Assert.Equal(E164, Assert.Single(h.Sms.SentMessages).PhoneE164);
        Assert.Equal(MessageStatus.Sent, h.Store.Messages.Single().Status);
    }

    [Fact]
    public async Task Sms_channel_skips_whatsapp()
    {
        var h = await Harness.CreateConnectedAsync();
        await h.RequestOtp().HandleAsync(h.Client, Cmd(channel: "sms"), default);
        Assert.Empty(h.WhatsApp.SentMessages);
        Assert.Single(h.Sms.SentMessages);
    }

    [Fact]
    public async Task Delivery_failure_on_all_channels_returns_502_and_voids_the_challenge()
    {
        var h = await Harness.CreateConnectedAsync();
        h.WhatsApp.FailSends = true;
        h.Sms.FailSends = true;

        var ex = await Assert.ThrowsAsync<AppException>(() => h.RequestOtp().HandleAsync(h.Client, Cmd(), default));
        Assert.Equal(ErrorKind.BadGateway, ex.Kind);
        Assert.Equal(OtpStatus.Superseded, h.Store.Otps.Single().Status);
        Assert.Equal(MessageStatus.Failed, h.Store.Messages.Single().Status);
    }

    [Fact]
    public async Task Per_phone_hourly_limit_applies_across_purposes()
    {
        var h = await Harness.CreateConnectedAsync();
        for (var i = 0; i < 5; i++) await h.RequestOtp().HandleAsync(h.Client, Cmd(purpose: $"p{i}"), default);

        var ex = await Assert.ThrowsAsync<AppException>(() => h.RequestOtp().HandleAsync(h.Client, Cmd(purpose: "p5"), default));
        Assert.Equal("otp_rate_limited", ex.Code);
    }

    [Theory]
    [InlineData("0123", "login", "whatsapp", "invalid_phone")]
    [InlineData(Phone, "", "whatsapp", "invalid_purpose")]
    [InlineData(Phone, "login", "telegram", "invalid_channel")]
    public async Task Invalid_input_is_rejected(string phone, string purpose, string channel, string code)
    {
        var h = await Harness.CreateConnectedAsync();
        var ex = await Assert.ThrowsAsync<AppException>(() => h.RequestOtp().HandleAsync(h.Client, new RequestOtpCommand(phone, purpose, channel), default));
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public async Task Challenge_of_another_client_is_not_found()
    {
        var h = await Harness.CreateConnectedAsync();
        var req = await h.RequestOtp().HandleAsync(h.Client, Cmd(), default);
        var ex = await Assert.ThrowsAsync<AppException>(() => h.VerifyOtp().HandleAsync(Guid.NewGuid(), req.ChallengeId, "123456", default));
        Assert.Equal(ErrorKind.NotFound, ex.Kind);
    }

    private static string Pad(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
    }
}
