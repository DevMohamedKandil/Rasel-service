using System.Security.Cryptography;
using System.Text;
using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Domain.Entities;
using Rasel.Domain.Enums;
using Rasel.Domain.ValueObjects;

namespace Rasel.Application.UseCases;

public sealed record SendTextCommand(Guid? SessionId, string? Phone, string? GroupName, string? Text, string? IdempotencyKey = null);

public sealed record SendTextResult(Guid MessageId, MessageStatus Status, bool Replayed = false);

public sealed class SendTextHandler(
    ISessionRepository sessions, IMessageRepository messages, IWhatsAppProvider provider, DailyLimitGuard dailyLimit,
    RecipientLimitGuard recipientLimit, TimeProvider clock)
{
    public const int MaxIdempotencyKeyLength = 128;
    public const int MaxGroupNameLength = 128; // matches the column size

    public async Task<SendTextResult> HandleAsync(ApiClient client, SendTextCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Text) || cmd.Text.Length > 4096)
            throw new AppException(ErrorKind.BadRequest, "invalid_text", "Text is required (max 4096 characters).");
        if (string.IsNullOrWhiteSpace(cmd.Phone) == string.IsNullOrWhiteSpace(cmd.GroupName))
            throw new AppException(ErrorKind.BadRequest, "invalid_target", "Provide exactly one of phone or groupName.");
        if (cmd.GroupName is { Length: > MaxGroupNameLength })
            throw new AppException(ErrorKind.BadRequest, "invalid_target", $"groupName is too long (max {MaxGroupNameLength} characters).");
        var key = NormalizeKey(cmd.IdempotencyKey);

        // A replay must be answered from the stored record, before anything else can fail (session stopped, limit hit...).
        var fingerprint = Fingerprint(cmd);
        if (key is not null && await FindReplayAsync(client.Id, key, fingerprint, ct) is { } replay) return replay;

        var session = await ResolveSessionAsync(client.Id, cmd.SessionId, ct);
        await dailyLimit.EnsureWithinAsync(client, ct);

        string? recipientKey = null;
        TargetType type;
        string display, destination;
        if (!string.IsNullOrWhiteSpace(cmd.Phone))
        {
            if (!PhoneNumber.TryParse(cmd.Phone, out var phone))
                throw new AppException(ErrorKind.BadRequest, "invalid_phone", "Phone must be an international number.");
            type = TargetType.Phone;
            display = phone.Masked;
            destination = phone.E164;
            await recipientLimit.EnsureWithinAsync(client.Id, phone.E164, ct);
            recipientKey = recipientLimit.KeyFor(client.Id, phone.E164);
        }
        else
        {
            var matches = await provider.FindGroupsByNameAsync(session.Id, cmd.GroupName!, ct);
            if (matches.Count == 0)
                throw new AppException(ErrorKind.NotFound, "group_not_found", "No group has this exact name.");
            if (matches.Count > 1)
                throw new AppException(ErrorKind.Conflict, "group_ambiguous", "More than one group has this exact name.");
            type = TargetType.Group;
            display = destination = cmd.GroupName!;
        }

        var message = Message.Queue(client.Id, session.Id, type, display, destination, cmd.Text, key, key is null ? null : fingerprint, clock.GetUtcNow(), recipientKey);
        if (!await messages.TryAddAsync(message, ct))
        {
            // Lost a race with a parallel request carrying the same key.
            return await FindReplayAsync(client.Id, key!, fingerprint, ct)
                ?? throw new AppException(ErrorKind.Conflict, "idempotency_conflict", "A request with this Idempotency-Key is in progress.");
        }

        return new SendTextResult(message.Id, message.Status);
    }

    private async Task<SendTextResult?> FindReplayAsync(Guid clientId, string key, string fingerprint, CancellationToken ct)
    {
        var existing = await messages.FindByIdempotencyKeyAsync(clientId, key, ct);
        if (existing is null) return null;
        if (existing.Fingerprint != fingerprint)
            throw new AppException(ErrorKind.Conflict, "idempotency_key_reused", "This Idempotency-Key was already used with a different request.");
        return new SendTextResult(existing.Id, existing.Status, Replayed: true);
    }

    private static string? NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        key = key.Trim();
        if (key.Length > MaxIdempotencyKeyLength || key.Any(c => c < 0x21 || c > 0x7E))
            throw new AppException(ErrorKind.BadRequest, "invalid_idempotency_key",
                $"Idempotency-Key must be 1-{MaxIdempotencyKeyLength} printable ASCII characters.");
        return key;
    }

    private static string Fingerprint(SendTextCommand cmd) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes($"{cmd.SessionId:N}|{cmd.Phone?.Trim()}|{cmd.GroupName}|{cmd.Text}")));

    private async Task<Session> ResolveSessionAsync(Guid clientId, Guid? sessionId, CancellationToken ct)
    {
        var session = sessionId is { } id
            ? await sessions.GetAsync(id, clientId, ct)
            : await sessions.FindConnectedAsync(clientId, ct);
        if (session is null)
            throw new AppException(ErrorKind.NotFound, "session_not_found", "No usable session found.");
        if (session.Status != SessionStatus.Connected)
            throw new AppException(ErrorKind.Conflict, "session_not_connected", "Session is not connected.");
        return session;
    }
}
