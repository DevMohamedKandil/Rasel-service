using Microsoft.Extensions.Logging;
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
    IRateLimiter limiter, DailyLimitGuard dailyLimit, TimeProvider clock, OtpOptions options, ILogger<RequestOtpHandler> logger)
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
            throw TooSoon((int)Math.Ceiling((recent.ResendAvailableAt - now).TotalSeconds));

        if (!limiter.TryAcquire($"otp:client:{client.Id}", options.PerClientPerMinute, TimeSpan.FromMinutes(1))
            || !limiter.TryAcquire($"otp:phone:{client.Id}:{phone.E164}", options.PerPhonePerHour, TimeSpan.FromHours(1)))
        {
            logger.LogWarning("OTP request rate-limited for client {ClientId}, phone {Phone}", client.Id, phone.Masked);
            throw new AppException(ErrorKind.TooManyRequests, "otp_rate_limited", "Too many code requests.", 60);
        }

        await dailyLimit.EnsureWithinAsync(client, ct);

        var id = Guid.NewGuid();
        var code = codes.GenerateNumeric(options.CodeLength);
        var challenge = OtpChallenge.Issue(id, client.Id, phone.E164, purpose, hasher.Hash(id, code), now,
            TimeSpan.FromSeconds(options.TtlSeconds), TimeSpan.FromSeconds(options.ResendCooldownSeconds), options.MaxAttempts);

        // Both writes are guarded: superseding uses the row version, and the database allows only one Active
        // challenge per (client, phone, purpose). A parallel request therefore loses with a 429, never creates a twin.
        foreach (var previous in active) previous.Supersede();
        if (active.Count > 0 && !await otps.TrySaveAsync(active, ct)) throw TooSoon(options.ResendCooldownSeconds);
        if (!await otps.TryAddAsync(challenge, ct)) throw TooSoon(options.ResendCooldownSeconds);

        var text = $"Your verification code is {code}. It expires in {options.TtlSeconds / 60} minutes. Do not share it.";
        if (!await DeliverAsync(client.Id, phone, text, viaSms, ct))
        {
            challenge.Supersede();
            await otps.TrySaveAsync([challenge], ct);
            logger.LogWarning("OTP delivery failed for phone {Phone}", phone.Masked);
            throw new AppException(ErrorKind.BadGateway, "otp_delivery_failed", "Could not deliver the code.");
        }

        logger.LogInformation("OTP challenge {ChallengeId} issued to {Phone}", id, phone.Masked);
        return new RequestOtpResult(id, options.TtlSeconds, options.ResendCooldownSeconds);
    }

    private static AppException TooSoon(int retryAfterSeconds) =>
        new(ErrorKind.TooManyRequests, "otp_resend_too_soon", "Please wait before requesting another code.", Math.Max(1, retryAfterSeconds));

    private static bool ParseChannel(string? channel) => channel?.ToLowerInvariant() switch
    {
        null or "" or "whatsapp" => false,
        "sms" => true,
        _ => throw new AppException(ErrorKind.BadRequest, "invalid_channel", "Channel must be whatsapp or sms."),
    };

    /// <summary>WhatsApp first (unless SMS was requested), falling back to SMS. Records the outcome without the text.</summary>
    private async Task<bool> DeliverAsync(Guid clientId, PhoneNumber phone, string text, bool viaSms, CancellationToken ct)
    {
        var messageId = Guid.NewGuid();
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
                    await whatsApp.SendTextToPhoneAsync(session.Id, phone.E164, text, messageId, ct);
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

        var record = Message.Record(messageId, clientId, sessionId, phone.Masked, sent, sent ? null : PiiRedactor.Redact(error), clock.GetUtcNow());
        await messages.TryAddAsync(record, ct);
        return sent;
    }
}

public sealed record VerifyOtpResult(string Token, int ExpiresInSeconds);

public sealed class VerifyOtpHandler(
    IOtpRepository otps, IOtpHasher hasher, ITokenIssuer tokens, TimeProvider clock, OtpOptions options, ILogger<VerifyOtpHandler> logger)
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);
    private const int MaxConflictRetries = 25;

    public async Task<VerifyOtpResult> HandleAsync(Guid clientId, Guid challengeId, string? code, CancellationToken ct)
    {
        // Malformed input never touches the challenge, so garbage cannot burn someone else's attempts.
        if (code is null || code.Length != options.CodeLength || !code.All(char.IsAsciiDigit))
            throw new AppException(ErrorKind.BadRequest, "invalid_code_format", $"Code must be {options.CodeLength} digits.");

        // Read-modify-write guarded by the row version. If another request changed the challenge in between
        // (a parallel verify, a lockout, a supersede), reload and apply this attempt to the fresh state, so
        // attempts are never lost and a code can be consumed exactly once.
        for (var retry = 0; retry < MaxConflictRetries; retry++)
        {
            var challenge = await otps.GetAsync(challengeId, clientId, ct)
                ?? throw new AppException(ErrorKind.NotFound, "otp_not_found", "Challenge not found.");

            var matches = hasher.Verify(challenge.Id, code, challenge.CodeHash);
            var result = challenge.Attempt(matches, clock.GetUtcNow());
            if (!await otps.TrySaveAsync([challenge], ct))
            {
                await Task.Delay(Random.Shared.Next(1, 8), ct);
                continue;
            }

            return Outcome(challenge, result, clientId);
        }

        throw new AppException(ErrorKind.Conflict, "otp_busy", "The challenge is being updated, retry shortly.", 1);
    }

    private VerifyOtpResult Outcome(OtpChallenge challenge, OtpAttemptResult result, Guid clientId)
    {
        switch (result)
        {
            case OtpAttemptResult.Verified:
                logger.LogInformation("OTP challenge {ChallengeId} verified", challenge.Id);
                return new VerifyOtpResult(tokens.Issue(clientId, challenge.PhoneE164, challenge.Purpose, TokenLifetime), (int)TokenLifetime.TotalSeconds);
            case OtpAttemptResult.Invalid:
                if (challenge.Status == OtpStatus.Locked)
                    logger.LogWarning("OTP challenge {ChallengeId} locked after {Attempts} failed attempts", challenge.Id, challenge.Attempts);
                throw new AppException(ErrorKind.BadRequest, "otp_invalid",
                    $"Incorrect code. {Math.Max(0, options.MaxAttempts - challenge.Attempts)} attempt(s) left.");
            case OtpAttemptResult.Locked: throw new AppException(ErrorKind.Locked, "otp_locked", "Too many failed attempts.");
            case OtpAttemptResult.Expired: throw new AppException(ErrorKind.Gone, "otp_expired", "The code has expired.");
            case OtpAttemptResult.Superseded: throw new AppException(ErrorKind.Gone, "otp_superseded", "A newer code was issued.");
            default: throw new AppException(ErrorKind.Conflict, "otp_already_used", "This code was already used.");
        }
    }
}
