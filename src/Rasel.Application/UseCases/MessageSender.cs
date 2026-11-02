using Rasel.Application.Ports;
using Rasel.Domain.Enums;

namespace Rasel.Application.UseCases;

/// <summary>Delivers one queued message and records the outcome. Called by the background worker.</summary>
public sealed class MessageSender(IMessageRepository messages, IWhatsAppProvider provider, IWebhookDispatcher webhooks)
{
    public async Task SendAsync(QueuedMessage queued, CancellationToken ct)
    {
        var message = await messages.GetAsync(queued.MessageId, ct);
        if (message is null) return;

        try
        {
            if (queued.TargetType == TargetType.Phone)
                await provider.SendTextToPhoneAsync(queued.SessionId, queued.Target, queued.Text, ct);
            else
                await provider.SendTextToGroupAsync(queued.SessionId, queued.Target, queued.Text, ct);
            message.MarkSent();
        }
        catch (ProviderException ex)
        {
            message.MarkFailed(ex.Message);
        }

        await messages.UpdateAsync(message, ct);
        await webhooks.DispatchAsync(queued.ClientId,
            message.Status == MessageStatus.Sent ? "message.sent" : "message.failed",
            new { messageId = message.Id, status = message.Status.ToString(), error = message.Error }, ct);
    }
}
