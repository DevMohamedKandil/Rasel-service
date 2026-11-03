using Rasel.Domain.Enums;

namespace Rasel.Domain.Entities;

/// <summary>
/// An outbox row: durable delivery state. <see cref="Body"/> and <see cref="Destination"/> exist only while the
/// message is pending so it survives a restart; both are cleared the moment the message reaches a terminal state.
/// <see cref="Target"/> is always safe to display (masked phone or group name).
/// </summary>
public class Message
{
    private Message() { }

    public Guid Id { get; private set; }
    public Guid ClientId { get; private set; }
    public Guid SessionId { get; private set; }
    public TargetType TargetType { get; private set; }
    public string Target { get; private set; } = "";
    public string? Destination { get; private set; }
    public string? Body { get; private set; }
    public string? IdempotencyKey { get; private set; }
    public string? Fingerprint { get; private set; }
    /// <summary>Keyed hash of (client, recipient number) for the per-recipient daily cap; null for groups.</summary>
    public string? RecipientKey { get; private set; }
    public MessageStatus Status { get; private set; }
    public string? Error { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset? LeasedUntil { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }

    /// <summary>Optimistic concurrency token, bumped on every state change (see <see cref="OtpChallenge.Version"/>).</summary>
    public long Version { get; private set; }

    public static Message Queue(
        Guid clientId, Guid sessionId, TargetType type, string displayTarget, string destination, string body,
        string? idempotencyKey, string? fingerprint, DateTimeOffset now, string? recipientKey = null) => new()
    {
        Id = Guid.NewGuid(), ClientId = clientId, SessionId = sessionId, TargetType = type, Target = displayTarget,
        Destination = destination, Body = body, IdempotencyKey = idempotencyKey, Fingerprint = fingerprint, RecipientKey = recipientKey,
        Status = MessageStatus.Queued, NextAttemptAt = now, CreatedAt = now,
    };

    /// <summary>A message that was delivered (or failed) inline and never needed the outbox. Stores no body.</summary>
    public static Message Record(Guid id, Guid clientId, Guid sessionId, string displayTarget, bool sent, string? error, DateTimeOffset now, string? recipientKey = null) => new()
    {
        Id = id, ClientId = clientId, SessionId = sessionId, TargetType = TargetType.Phone, Target = displayTarget, RecipientKey = recipientKey,
        Status = sent ? MessageStatus.Sent : MessageStatus.Failed, Error = sent ? null : error, Attempts = 1,
        NextAttemptAt = now, CreatedAt = now, SentAt = sent ? now : null,
    };

    public bool IsTerminal => Status is MessageStatus.Sent or MessageStatus.Failed;

    public void Lease(DateTimeOffset now, TimeSpan duration)
    {
        LeasedUntil = now + duration;
        Version++;
    }

    public void MarkSent(DateTimeOffset now)
    {
        Status = MessageStatus.Sent;
        SentAt = now;
        Attempts++;
        Error = null;
        Finish();
    }

    /// <summary>
    /// Records a failed attempt. Transient failures are rescheduled after <paramref name="retryDelay"/> until
    /// <paramref name="maxAttempts"/> is reached; permanent failures end the message immediately.
    /// </summary>
    public void RecordFailure(string error, bool transient, DateTimeOffset now, int maxAttempts, TimeSpan retryDelay)
    {
        Attempts++;
        Error = error;
        if (!transient || Attempts >= maxAttempts)
        {
            Status = MessageStatus.Failed;
            Finish();
            return;
        }

        NextAttemptAt = now + retryDelay;
        LeasedUntil = null;
        Version++;
    }

    /// <summary>The session is not ready: wait and try again later without using up an attempt.</summary>
    public void Defer(string reason, DateTimeOffset now, TimeSpan delay)
    {
        Error = reason;
        NextAttemptAt = now + delay;
        LeasedUntil = null;
        Version++;
    }

    private void Finish()
    {
        Body = null;
        Destination = null;
        LeasedUntil = null;
        Version++;
    }
}
