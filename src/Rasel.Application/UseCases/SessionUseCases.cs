using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Domain.Entities;
using Rasel.Domain.Enums;

namespace Rasel.Application.UseCases;

public sealed record SessionDto(Guid Id, string Name, SessionStatus Status);

public sealed record QrDto(Guid SessionId, SessionStatus Status, string? Qr);

internal static class SessionLookup
{
    public static async Task<Session> GetOrThrowAsync(ISessionRepository sessions, Guid id, Guid clientId, CancellationToken ct) =>
        await sessions.GetAsync(id, clientId, ct) ?? throw new AppException(ErrorKind.NotFound, "session_not_found", "Session not found.");
}

public sealed class CreateSessionHandler(ISessionRepository sessions, IWhatsAppProvider provider, TimeProvider clock)
{
    public async Task<SessionDto> HandleAsync(Guid clientId, string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            throw new AppException(ErrorKind.BadRequest, "invalid_name", "Session name is required (max 100 characters).");

        var session = Session.Start(clientId, name.Trim(), clock.GetUtcNow());
        await sessions.AddAsync(session, ct);
        await provider.CreateSessionAsync(session.Id, ct);
        session.UpdateStatus(await provider.GetStatusAsync(session.Id, ct), clock.GetUtcNow());
        await sessions.UpdateAsync(session, ct);
        return new SessionDto(session.Id, session.Name, session.Status);
    }
}

public sealed class GetSessionStatusHandler(ISessionRepository sessions, IWhatsAppProvider provider, TimeProvider clock)
{
    public async Task<SessionDto> HandleAsync(Guid clientId, Guid id, CancellationToken ct)
    {
        var session = await SessionLookup.GetOrThrowAsync(sessions, id, clientId, ct);
        if (session.Status != SessionStatus.Stopped)
        {
            session.UpdateStatus(await provider.GetStatusAsync(id, ct), clock.GetUtcNow());
            await sessions.UpdateAsync(session, ct);
        }

        return new SessionDto(session.Id, session.Name, session.Status);
    }
}

public sealed class GetSessionQrHandler(ISessionRepository sessions, IWhatsAppProvider provider, TimeProvider clock)
{
    public async Task<QrDto> HandleAsync(Guid clientId, Guid id, CancellationToken ct)
    {
        var session = await SessionLookup.GetOrThrowAsync(sessions, id, clientId, ct);
        if (session.Status == SessionStatus.Stopped)
            throw new AppException(ErrorKind.Conflict, "session_stopped", "Session is stopped.");

        var qr = await provider.GetQrAsync(id, ct);
        session.UpdateStatus(await provider.GetStatusAsync(id, ct), clock.GetUtcNow());
        await sessions.UpdateAsync(session, ct);
        return new QrDto(session.Id, session.Status, qr);
    }
}

public sealed class StopSessionHandler(ISessionRepository sessions, IWhatsAppProvider provider, TimeProvider clock)
{
    public async Task HandleAsync(Guid clientId, Guid id, CancellationToken ct)
    {
        var session = await SessionLookup.GetOrThrowAsync(sessions, id, clientId, ct);
        await provider.StopSessionAsync(id, ct);
        session.UpdateStatus(SessionStatus.Stopped, clock.GetUtcNow());
        await sessions.UpdateAsync(session, ct);
    }
}
