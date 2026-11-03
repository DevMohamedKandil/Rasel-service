using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Domain.Enums;
using Rasel.Infrastructure.Configuration;

namespace Rasel.Infrastructure.Providers.Bridge;

/// <summary>
/// Adapter from <see cref="IWhatsAppProvider"/> to the REAL existing whatsapp-web.js bridge (WPService), whose
/// contract differs from the invented sidecar contract: one global WhatsApp client (no sessions), no auth,
/// plain-text error bodies, no group listing and no status endpoint.
///
/// Mapping: sessions are a Rasel-side notion, every session maps to the single bridge client. Status is probed
/// with a deliberately invalid group send (empty group name): 503 = client not ready, 400 = ready; nothing is ever
/// sent by the probe. Group text goes to POST /doSendPersonalizedTextToGroup. Phone text uses GET /sendMessage.
/// FindGroups cannot be answered by the bridge, so the group is optimistically assumed and a missing group
/// surfaces as a permanent failure (404) when the outbox delivers.
/// Errors: 503, 408, 429, 5xx, timeouts, network errors, open circuit = transient; 400, 404, 409 and other 4xx = permanent.
/// </summary>
public sealed class BridgeWhatsAppProvider(HttpClient http, Rasel.Infrastructure.Sidecar.IBridgeEndpoint endpoint) : IWhatsAppProvider
{
    /// <summary>Managed mode: no endpoint until the supervised sidecar answers. That is a transient failure, so queued sends wait instead of failing.</summary>
    private (string BaseUrl, string Token) EndpointFor(Guid sessionId) =>
        endpoint.Resolve(sessionId) ?? throw new ProviderException("The WhatsApp engine of this session is not ready yet.", isTransient: true, isNotReady: true);

    public async Task CreateSessionAsync(Guid sessionId, CancellationToken ct)
    {
        await endpoint.EnsureSessionAsync(sessionId, ct); // Managed: starts this session's own engine and returns at once
        if (endpoint.Resolve(sessionId) is not null) await ProbeQrAsync(sessionId, ct); // the bridge must at least be alive
    }

    /// <summary>Managed: stops only this session's engine (data kept). External bridge: no-op, the shared bridge is never stopped.</summary>
    public Task StopSessionAsync(Guid sessionId, CancellationToken ct) => endpoint.StopSessionAsync(sessionId, purge: false, ct);

    public Task PurgeSessionAsync(Guid sessionId, CancellationToken ct) => endpoint.StopSessionAsync(sessionId, purge: true, ct);

    public async Task<string?> GetQrAsync(Guid sessionId, CancellationToken ct)
    {
        if (endpoint.Resolve(sessionId) is null && endpoint.NotReadyStatus(sessionId) is not null) return null;
        var qr = await ProbeQrAsync(sessionId, ct);
        // /Qr keeps serving the last QR even after login, so only expose it while the client is not ready.
        return await GetStatusAsync(sessionId, ct) == SessionStatus.Connected || string.IsNullOrWhiteSpace(qr) ? null : qr;
    }

    public async Task<SessionStatus> GetStatusAsync(Guid sessionId, CancellationToken ct)
    {
        if (endpoint.Resolve(sessionId) is null && endpoint.NotReadyStatus(sessionId) is { } notReady) return notReady; // Managed: engine still starting / stopped / failed
        var qr = await ProbeQrAsync(sessionId, ct);
        using var response = await SendAsync(sessionId, HttpMethod.Post, "/doSendPersonalizedTextToGroup", new { groupName = "", message = "" }, ct, throwOnError: false);
        return response.StatusCode switch
        {
            HttpStatusCode.BadRequest => SessionStatus.Connected, // validation ran, so the client is ready; nothing was sent
            HttpStatusCode.ServiceUnavailable => string.IsNullOrWhiteSpace(qr) ? SessionStatus.Starting : SessionStatus.NeedsQr,
            _ => SessionStatus.Failed,
        };
    }

    /// <summary>GET /info on the Rasel sidecar. The legacy WPService has no such route (404) and then this is simply null.</summary>
    public async Task<string?> GetLinkedPhoneAsync(Guid sessionId, CancellationToken ct)
    {
        if (endpoint.Resolve(sessionId) is null) return null;
        using var response = await SendAsync(sessionId, HttpMethod.Get, "/info", null, ct, throwOnError: false);
        if (!response.IsSuccessStatusCode) return null;
        try
        {
            var doc = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
            var phone = doc.ValueKind == System.Text.Json.JsonValueKind.Object && doc.TryGetProperty("phone", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String ? p.GetString() : null;
            return phone is { Length: >= 5 and <= 20 } && phone.All(char.IsAsciiDigit) ? phone : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public async Task SendTextToPhoneAsync(Guid sessionId, string phoneE164, string text, Guid messageId, CancellationToken ct)
    {
        var digits = phoneE164.TrimStart('+');
        var path = $"/sendMessage?phone={Uri.EscapeDataString(digits)}&message={Uri.EscapeDataString(text)}";
        using var _ = await SendAsync(sessionId, HttpMethod.Get, path, null, ct);
    }

    public async Task SendTextToGroupAsync(Guid sessionId, string groupName, string text, Guid messageId, CancellationToken ct)
    {
        using var _ = await SendAsync(sessionId, HttpMethod.Post, "/doSendPersonalizedTextToGroup", new { groupName, message = text }, ct);
    }

    public Task<IReadOnlyList<GroupInfo>> FindGroupsByNameAsync(Guid sessionId, string exactName, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<GroupInfo>>([new GroupInfo(exactName, exactName)]);

    private async Task<string> ProbeQrAsync(Guid sessionId, CancellationToken ct)
    {
        using var response = await SendAsync(sessionId, HttpMethod.Get, "/Qr", null, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<HttpResponseMessage> SendAsync(Guid sessionId, HttpMethod method, string path, object? body, CancellationToken ct, bool throwOnError = true)
    {
        var (baseUrl, token) = EndpointFor(sessionId);
        using var request = new HttpRequestMessage(method, baseUrl.TrimEnd('/') + path);
        if (body is not null) request.Content = JsonContent.Create(body);
        if (token.Length > 0) request.Headers.Add("X-Bridge-Token", token);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (BrokenCircuitException) { throw new ProviderException("WhatsApp bridge circuit is open.", isTransient: true); }
        catch (TimeoutRejectedException) { throw new ProviderException("WhatsApp bridge timed out.", isTransient: true); }
        catch (HttpRequestException ex) { throw new ProviderException($"WhatsApp bridge unreachable ({ex.HttpRequestError}).", isTransient: true); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ProviderException("WhatsApp bridge timed out.", isTransient: true); }

        if (response.IsSuccessStatusCode || !throwOnError) return response;

        var status = response.StatusCode;
        var transient = status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500; // includes 503 not ready
        var detail = await ReadTextAsync(response, ct);
        response.Dispose();
        var meaning = status switch
        {
            HttpStatusCode.ServiceUnavailable => " (bridge not ready)",
            HttpStatusCode.NotFound => " (group not found)",
            HttpStatusCode.Conflict => " (group name ambiguous)",
            HttpStatusCode.BadRequest => " (rejected as invalid)",
            _ => "",
        };
        // 503 = the engine is up but its WhatsApp client is not ready (needs QR / loading): queued messages wait without using up attempts.
        throw new ProviderException($"WhatsApp bridge returned {(int)status}{meaning}{(detail is null ? "" : ": " + detail)}", transient, isNotReady: status == HttpStatusCode.ServiceUnavailable);
    }

    private static async Task<string?> ReadTextAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = (await response.Content.ReadAsStringAsync(ct)).Trim();
            if (text.Length == 0) return null;
            return PiiRedactor.Redact(text.Length > 200 ? text[..200] : text);
        }
        catch (HttpRequestException) { return null; }
    }
}
