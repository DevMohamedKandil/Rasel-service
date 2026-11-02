using System.Threading.Channels;
using Rasel.Application.Ports;

namespace Rasel.Infrastructure.Messaging;

public sealed class InMemoryMessageQueue : IMessageQueue
{
    private readonly Channel<QueuedMessage> _channel = Channel.CreateUnbounded<QueuedMessage>();

    public ValueTask EnqueueAsync(QueuedMessage message, CancellationToken ct) => _channel.Writer.WriteAsync(message, ct);

    public ValueTask<QueuedMessage> DequeueAsync(CancellationToken ct) => _channel.Reader.ReadAsync(ct);
}
