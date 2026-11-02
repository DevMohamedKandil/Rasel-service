using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Domain.Entities;
using Rasel.Domain.Enums;
using Rasel.Domain.ValueObjects;

namespace Rasel.Application.UseCases;

public sealed record SendTextCommand(Guid? SessionId, string? Phone, string? GroupName, string? Text);

public sealed record SendTextResult(Guid MessageId, MessageStatus Status);

public sealed class SendTextHandler(
    ISessionRepository sessions, IMessageRepository messages, IWhatsAppProvider provider, IMessageQueue queue, DailyLimitGuard dailyLimit, TimeProvider clock)
{
    public async Task<SendTextResult> HandleAsync(ApiClient client, SendTextCommand cmd, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.Text) || cmd.Text.Length > 4096)
            throw new AppException(ErrorKind.BadRequest, "invalid_text", "Text is required (max 4096 characters).");
        if (string.IsNullOrWhiteSpace(cmd.Phone) == string.IsNullOrWhiteSpace(cmd.GroupName))
            throw new AppException(ErrorKind.BadRequest, "invalid_target", "Provide exactly one of phone or groupName.");

        var session = await ResolveSessionAsync(client.Id, cmd.SessionId, ct);
        await dailyLimit.EnsureWithinAsync(client, ct);

        TargetType type;
        string target;
        if (!string.IsNullOrWhiteSpace(cmd.Phone))
        {
            if (!PhoneNumber.TryParse(cmd.Phone, out var phone))
                throw new AppException(ErrorKind.BadRequest, "invalid_phone", "Phone must be an international number.");
            type = TargetType.Phone;
            target = phone.E164;
        }
        else
        {
            var matches = await provider.FindGroupsByNameAsync(session.Id, cmd.GroupName!, ct);
            if (matches.Count == 0)
                throw new AppException(ErrorKind.NotFound, "group_not_found", "No group has this exact name.");
            if (matches.Count > 1)
                throw new AppException(ErrorKind.Conflict, "group_ambiguous", "More than one group has this exact name.");
            type = TargetType.Group;
            target = cmd.GroupName!;
        }

        var message = Message.Queue(client.Id, session.Id, type, target, clock.GetUtcNow());
        await messages.AddAsync(message, ct);
        await queue.EnqueueAsync(new QueuedMessage(message.Id, client.Id, session.Id, type, target, cmd.Text), ct);
        return new SendTextResult(message.Id, message.Status);
    }


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
