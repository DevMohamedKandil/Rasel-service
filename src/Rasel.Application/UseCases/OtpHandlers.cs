using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Domain.Entities;
using Rasel.Domain.Enums;
using Rasel.Domain.ValueObjects;

namespace Rasel.Application.UseCases;

public sealed record RequestOtpCommand(string? Phone, string? Purpose, string? Channel);

public sealed record RequestOtpResult(Guid ChallengeId, int ExpiresInSeconds, int ResendAfterSeconds);

public sealed class RequestOtpHandler(
    IOtpRepository otps, ISessionRepository sessions, IMessageRepository messages,
    IWhatsAppProvider whatsApp, ISmsProvider sms, IOtpHasher hasher, ICodeGenerator codes,
    IRateLimiter limiter, DailyLimitGuard dailyLimit, TimeProvider clock, OtpOptions options)
{
    public async Task<RequestOtpResult> HandleAsync(ApiClient client, RequestOtpCommand cmd, CancellationToken ct)
    {
        if (!PhoneNumber.TryParse(cmd.Phone, out var phone))
            throw new AppException(ErrorKind.BadRequest, "invalid_phone", "Phone must be an international number.");
        if (string.IsNullOrWhiteSpace(cmd.Purpose) || cmd.Purpose.Length > 64)
            throw new AppException(ErrorKind.BadRequest, "invalid_purpose", "Purpose is required (max 64 characters).");
        var viaSms = ParseChannel(cmd.Channel);
        var purpose = cmd.Purpose.Trim();
        var now = clock.GetUtcNow();

        var active = await otps.GetActiveAsync(client.Id, phone.E164, purpose, ct);
        if (active.FirstOrDefault(c => c.ResendAvailableAt > now) is { } recent)
            throw new AppException(ErrorKind.TooManyRequests, "otp_resend_too_soon", "Please wait before requesting another code.",
                (int)Math.Ceiling((recent.ResendAvailableAt - now).TotalSeconds));

        if (!limiter.TryAcquire($"otp:client:{client.Id}", options.PerClientPerMinute, TimeSpan.FromMinutes(1))
            || !limiter.TryAcquire($"otp:phone:{client.Id}:{phone.E164}", options.PerPhonePerHour, TimeSpan.FromHours(1)))
            throw new AppException(ErrorKind.TooManyRequests, "otp_rate_limited", "Too many code requests.", 60);
        await dailyLimit.EnsureWithinAsync(client, ct);

        var id = Guid.NewGuid();
        var code = codes.GenerateNumeric(6);
        var challenge = OtpChallenge.Issue(id, client.Id, phone.E164, purpose, hasher.Hash(id, code), now,
            TimeSpan.FromSeconds(options.TtlSeconds), TimeSpan.FromSeconds(options.ResendCooldownSeconds), options.MaxAttempts);

        foreach (var previous in active) previous.Supersede();
        await otps.UpdateAsync(active, ct);
        await otps.AddAsync(challenge, ct);

        var text = $"Your verification code is {code}. It expires in {options.TtlSeconds / 60} minutes. Do not share it.";
        if (!await DeliverAsync(client.Id, phone, text, viaSms, ct))
        {
            challenge.Supersede();
            await otps.UpdateAsync([challenge], ct);
            throw new AppException(ErrorKind.BadGateway, "otp_delivery_failed", "Could not deliver the code.");
        }

        return new RequestOtpResult(id, options.TtlSeconds, options.ResendCooldownSeconds);
    }

    private static bool ParseChannel(string? channel) => channel?.ToLowerInvariant() switch
    {
        null or "" or "whatsapp" => false,
        "sms" => true,
        _ => throw new AppException(ErrorKind.BadRequest, "invalid_channel", "Channel must be whatsapp or sms."),
    };

    /// <summary>WhatsApp first (unless SMS was requested), falling back to SMS. Records the outcome without the text.</summary>
    private async Task<bool> DeliverAsync(Guid clientId, PhoneNumber phone, string text, bool viaSms, CancellationToken ct)
    {
        var sessionId = Guid.Empty;
        var sent = false;
        string? error = null;

        if (!viaSms)
        {
            var session = await sessions.FindConnectedAsync(clientId, ct);
            if (session is null) error = "no connected WhatsApp session";
            else
            {
                sessionId = session.Id;
                try
                {
                    await whatsApp.SendTextToPhoneAsync(session.Id, phone.E164, text, ct);
                    sent = true;
                }
                catch (ProviderException ex) { error = ex.Message; }
            }
        }

        if (!sent)
        {
            try
            {
                await sms.SendAsync(phone.E164, text, ct);
                sent = true;
                error = null;
            }
            catch (ProviderException ex) { error = ex.Message; }
        }

        var message = Message.Queue(clientId, sessionId, TargetType.Phone, phone.Masked, clock.GetUtcNow());
        if (sent) message.MarkSent(); else message.MarkFailed(error ?? "delivery failed");
        await messages.AddAsync(message, ct);
        return sent;
    }
}

public sealed record VerifyOtpResult(string Token, int ExpiresInSeconds);

public sealed class VerifyOtpHandler(IOtpRepository otps, IOtpHasher hasher, ITokenIssuer tokens, TimeProvider clock, OtpOptions options)
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);

    public async Task<VerifyOtpResult> HandleAsync(Guid clientId, Guid challengeId, string? code, CancellationToken ct)
    {
        var challenge = await otps.GetAsync(challengeId, clientId, ct)
            ?? throw new AppException(ErrorKind.NotFound, "otp_not_found", "Challenge not found.");

        var matches = hasher.Verify(challenge.Id, code ?? "", challenge.CodeHash);
        var result = challenge.Attempt(matches, clock.GetUtcNow());
        await otps.UpdateAsync([challenge], ct);

        return result switch
        {
            OtpAttemptResult.Verified => new VerifyOtpResult(
                tokens.Issue(clientId, challenge.PhoneE164, challenge.Purpose, TokenLifetime), (int)TokenLifetime.TotalSeconds),
            OtpAttemptResult.Invalid => throw new AppException(ErrorKind.BadRequest, "otp_invalid",
                $"Incorrect code. {Math.Max(0, options.MaxAttempts - challenge.Attempts)} attempt(s) left."),
            OtpAttemptResult.Locked => throw new AppException(ErrorKind.Locked, "otp_locked", "Too many failed attempts."),
            OtpAttemptResult.Expired => throw new AppException(ErrorKind.Gone, "otp_expired", "The code has expired."),
            OtpAttemptResult.Superseded => throw new AppException(ErrorKind.Gone, "otp_superseded", "A newer code was issued."),
            _ => throw new AppException(ErrorKind.Conflict, "otp_already_used", "This code was already used."),
        };
    }
}
