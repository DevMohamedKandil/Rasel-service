using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Domain.Entities;
using Rasel.Domain.Enums;

namespace Rasel.Application.UseCases;

public sealed record SessionDto(Guid Id, string Name, SessionStatus Status, string? LinkedPhoneMasked = null);

public sealed record QrDto(Guid SessionId, SessionStatus Status, string? Qr, string? LinkedPhoneMasked = null);

/// <summary>The full number. Returned only by its own authenticated endpoint; never put it in any other DTO or log.</summary>
public sealed record LinkedNumberDto(Guid SessionId, string Phone);

internal static class LinkedPhone
{
    /// <summary>Only asked when connected; a provider failure here must not fail the status call.</summary>
    public static async Task<string?> TryGetAsync(IWhatsAppProvider provider, Guid id, SessionStatus status, CancellationToken ct)
    {
        if (status != SessionStatus.Connected) return null;
        try { return await provider.GetLinkedPhoneAsync(id, ct); }
        catch (ProviderException) { return null; }
    }
}

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
        try
        {
            await provider.CreateSessionAsync(session.Id, ct);
            session.UpdateStatus(await provider.GetStatusAsync(session.Id, ct), clock.GetUtcNow());
        }
        catch (ProviderException)
        {
            // Do not leave a session stuck in Starting when the provider is down.
            session.UpdateStatus(SessionStatus.Failed, clock.GetUtcNow());
            await sessions.UpdateAsync(session, CancellationToken.None);
            throw;
        }

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

        return new SessionDto(session.Id, session.Name, session.Status, PhoneMask.Mask(await LinkedPhone.TryGetAsync(provider, id, session.Status, ct)));
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
        return new QrDto(session.Id, session.Status, qr, PhoneMask.Mask(await LinkedPhone.TryGetAsync(provider, id, session.Status, ct)));
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

public sealed class GetLinkedNumberHandler(ISessionRepository sessions, IWhatsAppProvider provider)
{
    public async Task<LinkedNumberDto> HandleAsync(Guid clientId, Guid id, CancellationToken ct)
    {
        var session = await SessionLookup.GetOrThrowAsync(sessions, id, clientId, ct);
        var phone = PhoneMask.Full(await LinkedPhone.TryGetAsync(provider, id, SessionStatus.Connected, ct));
        if (session.Status == SessionStatus.Stopped || phone is null)
            throw new AppException(ErrorKind.Conflict, "session_not_connected", "No linked number: the session is not connected.");
        return new LinkedNumberDto(session.Id, phone);
    }
}
