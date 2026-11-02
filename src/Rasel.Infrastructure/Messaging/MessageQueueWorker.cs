using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rasel.Application.Ports;
using Rasel.Application.UseCases;

namespace Rasel.Infrastructure.Messaging;

public sealed class MessageQueueWorker(
    IMessageQueue queue, IServiceScopeFactory scopes, QueueOptions options, ILogger<MessageQueueWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var queued = await queue.DequeueAsync(stoppingToken);
                // A small random pause between sends makes traffic look less robotic to WhatsApp.
                if (options.MaxDelayMs > 0)
                    await Task.Delay(Random.Shared.Next(options.MinDelayMs, options.MaxDelayMs + 1), stoppingToken);

                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<MessageSender>().SendAsync(queued, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process a queued message");
            }
        }
    }
}
