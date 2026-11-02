using Rasel.Domain.Enums;

namespace Rasel.Domain.Entities;

/// <summary>Delivery record only. The text body is deliberately never persisted.</summary>
public class Message
{
    private Message() { }

    public Guid Id { get; private set; }
    public Guid ClientId { get; private set; }
    public Guid SessionId { get; private set; }
    public TargetType TargetType { get; private set; }
    public string Target { get; private set; } = "";
    public MessageStatus Status { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Message Queue(Guid clientId, Guid sessionId, TargetType type, string target, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), ClientId = clientId, SessionId = sessionId, TargetType = type, Target = target,
        Status = MessageStatus.Queued, CreatedAt = now,
    };

    public void MarkSent() => Status = MessageStatus.Sent;

    public void MarkFailed(string error)
    {
        Status = MessageStatus.Failed;
        Error = error;
    }
}
