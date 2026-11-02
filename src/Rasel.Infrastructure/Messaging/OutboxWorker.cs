using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Application.UseCases;

namespace Rasel.Infrastructure.Messaging;

/// <summary>
/// Drains the durable outbox: claims due rows (lease), sends them one by one and lets <see cref="MessageSender"/>
/// record the outcome. State lives in the database, so a crash or restart loses nothing; a row whose lease
/// expired is simply claimed again.
/// </summary>
public sealed class OutboxWorker(
    IServiceScopeFactory scopes, OutboxOptions options, TimeProvider clock, ILogger<OutboxWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processed = await ProcessBatchAsync(stoppingToken);
                if (processed == 0)
                    await Task.Delay(TimeSpan.FromMilliseconds(options.PollIntervalMs), clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox iteration failed");
                try { await Task.Delay(TimeSpan.FromMilliseconds(options.PollIntervalMs), clock, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>Claims and delivers one batch. Returns how many messages were claimed.</summary>
    public async Task<int> ProcessBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMessageRepository>();
        var sender = scope.ServiceProvider.GetRequiredService<MessageSender>();

        var batch = await repository.ClaimDueAsync(clock.GetUtcNow(), TimeSpan.FromSeconds(options.LeaseSeconds), options.BatchSize, ct);
        foreach (var message in batch)
        {
            ct.ThrowIfCancellationRequested();
            // A small random pause between sends makes traffic look less robotic to WhatsApp.
            if (options.MaxDelayMs > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(options.MinDelayMs, options.MaxDelayMs + 1)), clock, ct);
            await sender.SendAsync(message, ct);
        }

        return batch.Count;
    }
}
