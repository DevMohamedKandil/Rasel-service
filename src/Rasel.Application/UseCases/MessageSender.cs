using Microsoft.Extensions.Logging;
using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Domain.Entities;
using Rasel.Domain.Enums;

namespace Rasel.Application.UseCases;

/// <summary>
/// Delivers one leased outbox message and records the outcome: sent, rescheduled with backoff (transient
/// failure) or failed (permanent failure / attempts exhausted). Called by the outbox worker.
/// </summary>
public sealed class MessageSender(
    IMessageRepository messages, IWhatsAppProvider provider, IWebhookDispatcher webhooks,
    OutboxOptions options, ISendPacer pacer, TimeProvider clock, ILogger<MessageSender> logger)
{
    public async Task SendAsync(Message message, CancellationToken ct)
    {
        try
        {
            if (message.Body is null || message.Destination is null)
                throw new ProviderException("Message has no content to send.");

            await pacer.WaitTurnAsync(message.SessionId, SendPriority.Bulk, ct);
            if (message.TargetType == TargetType.Phone)
                await provider.SendTextToPhoneAsync(message.SessionId, message.Destination, message.Body, message.Id, ct);
            else
                await provider.SendTextToGroupAsync(message.SessionId, message.Destination, message.Body, message.Id, ct);
            message.MarkSent(clock.GetUtcNow());
        }
        catch (ProviderException ex)
        {
            message.RecordFailure(PiiRedactor.Redact(ex.Message), ex.IsTransient, clock.GetUtcNow(),
                options.MaxAttempts, options.BackoffFor(message.Attempts + 1));
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A provider bug or an unmapped exception must still count as an attempt. Otherwise the row would stay at the
            // head of the queue, be re-claimed after every lease and starve the messages behind it forever.
            logger.LogError(ex, "Unexpected error delivering message {MessageId}", message.Id);
            message.RecordFailure("unexpected delivery error", transient: true, clock.GetUtcNow(),
                options.MaxAttempts, options.BackoffFor(message.Attempts + 1));
        }

        // The provider call is done (or definitively failed): persist the outcome even during shutdown,
        // otherwise a sent message would be sent again after restart.
        if (!await messages.TrySaveAsync(message, CancellationToken.None))
        {
            logger.LogWarning("Message {MessageId} changed while it was being sent; outcome not saved", message.Id);
            return;
        }

        if (message.Status == MessageStatus.Queued)
        {
            logger.LogInformation("Message {MessageId} attempt {Attempt} failed transiently, retry at {NextAttemptAt:o}",
                message.Id, message.Attempts, message.NextAttemptAt);
            return;
        }

        logger.LogInformation("Message {MessageId} finished as {Status} after {Attempts} attempt(s)", message.Id, message.Status, message.Attempts);
        await webhooks.DispatchAsync(message.ClientId,
            message.Status == MessageStatus.Sent ? "message.sent" : "message.failed",
            new { messageId = message.Id, status = message.Status.ToString(), error = message.Error }, CancellationToken.None);
    }
}
