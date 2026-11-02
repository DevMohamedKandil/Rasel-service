using Rasel.Domain.Enums;

namespace Rasel.Domain.Entities;

public class Session
{
    private Session() { }

    public Guid Id { get; private set; }
    public Guid ClientId { get; private set; }
    public string Name { get; private set; } = "";
    public SessionStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Session Start(Guid clientId, string name, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), ClientId = clientId, Name = name, Status = SessionStatus.Starting, CreatedAt = now, UpdatedAt = now,
    };

    public void UpdateStatus(SessionStatus status, DateTimeOffset now)
    {
        Status = status;
        UpdatedAt = now;
    }
}
