using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Domain.Enums;

namespace Rasel.Application.UseCases;

/// <summary>Delivery state only: never the body, the destination or an OTP code.</summary>
public sealed record MessageStatusDto(
    Guid Id, string Status, int Attempts, DateTimeOffset CreatedAt, DateTimeOffset? SentAt,
    DateTimeOffset? NextAttemptAt, string? LastErrorCode);

public sealed class GetMessageHandler(IMessageRepository messages)
{
    public async Task<MessageStatusDto> HandleAsync(Guid clientId, Guid id, CancellationToken ct)
    {
        var m = await messages.GetAsync(id, ct);
        if (m is null || m.ClientId != clientId)
            throw new AppException(ErrorKind.NotFound, "message_not_found", "Message not found.");
        // The stored error text is free-form provider output, so only a coarse code is exposed.
        var code = m.Error is null ? null : m.Status == MessageStatus.Queued ? "provider_transient_error" : "provider_error";
        return new MessageStatusDto(m.Id, m.Status.ToString(), m.Attempts, m.CreatedAt, m.SentAt,
            m.Status == MessageStatus.Queued ? m.NextAttemptAt : null, code);
    }
}
