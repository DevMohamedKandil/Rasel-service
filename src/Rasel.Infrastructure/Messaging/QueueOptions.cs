namespace Rasel.Infrastructure.Messaging;

public sealed class QueueOptions
{
    public int MinDelayMs { get; set; } = 1000;
    public int MaxDelayMs { get; set; } = 3000;
}
