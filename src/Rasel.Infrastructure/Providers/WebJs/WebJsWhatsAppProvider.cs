using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Domain.Enums;
using Rasel.Infrastructure.Configuration;

namespace Rasel.Infrastructure.Providers.WebJs;

/// <summary>
/// Talks to the whatsapp-web.js sidecar over HTTP (contract in docs/ARCHITECTURE.md). Timeouts, retries of
/// idempotent calls and the circuit breaker live in the HttpClient pipeline (see <c>AddWebJsProvider</c>); this
/// class only maps outcomes to <see cref="ProviderException"/>: transient (timeout, 5xx, 429, open circuit,
/// network error) versus permanent (other 4xx).
/// </summary>
public sealed class WebJsWhatsAppProvider(HttpClient http, IOptions<WhatsAppOptions> options) : IWhatsAppProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private string Base => options.Value.WebJs.BaseUrl.TrimEnd('/');

    public Task CreateSessionAsync(Guid sessionId, CancellationToken ct) =>
        SendDiscardAsync(HttpMethod.Post, "/sessions", new { sessionId }, null, ct);

    public Task StopSessionAsync(Guid sessionId, CancellationToken ct) =>
        SendDiscardAsync(HttpMethod.Delete, $"/sessions/{sessionId}", null, null, ct);

    public Task LogoutAsync(Guid sessionId, CancellationToken ct) =>
        SendDiscardAsync(HttpMethod.Post, $"/sessions/{sessionId}/logout", null, null, ct);

    public async Task<string?> GetQrAsync(Guid sessionId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/sessions/{sessionId}/qr", null, null, ct);
        return (await ReadAsync<QrBody>(response, ct)).Qr;
    }

    public async Task<SessionStatus> GetStatusAsync(Guid sessionId, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/sessions/{sessionId}/status", null, null, ct);
        return (await ReadAsync<StatusBody>(response, ct)).Status;
    }

    public Task SendTextToPhoneAsync(Guid sessionId, string phoneE164, string text, Guid messageId, CancellationToken ct) =>
        SendDiscardAsync(HttpMethod.Post, $"/sessions/{sessionId}/messages",
            new { to = phoneE164, text, clientMessageId = messageId }, messageId, ct);

    public Task SendTextToGroupAsync(Guid sessionId, string groupName, string text, Guid messageId, CancellationToken ct) =>
        SendDiscardAsync(HttpMethod.Post, $"/sessions/{sessionId}/group-messages",
            new { groupName, text, clientMessageId = messageId }, messageId, ct);

    public async Task<IReadOnlyList<GroupInfo>> FindGroupsByNameAsync(Guid sessionId, string exactName, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/sessions/{sessionId}/groups?name={Uri.EscapeDataString(exactName)}", null, null, ct);
        var groups = await ReadAsync<List<GroupInfo>>(response, ct);
        return groups.Where(g => g.Name == exactName).ToList();
    }

    private async Task SendDiscardAsync(HttpMethod method, string path, object? body, Guid? idempotencyKey, CancellationToken ct)
    {
        using var response = await SendAsync(method, path, body, idempotencyKey, ct);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, Guid? idempotencyKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Base + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.WebJs.ApiToken);
        if (idempotencyKey is { } key) request.Headers.Add("Idempotency-Key", key.ToString());
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (BrokenCircuitException)
        {
            throw new ProviderException("WhatsApp provider circuit is open.", isTransient: true);
        }
        catch (TimeoutRejectedException)
        {
            throw new ProviderException("WhatsApp provider timed out.", isTransient: true);
        }
        catch (HttpRequestException ex)
        {
            throw new ProviderException($"WhatsApp provider unreachable ({ex.HttpRequestError}).", isTransient: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ProviderException("WhatsApp provider timed out.", isTransient: true);
        }

        if (response.IsSuccessStatusCode) return response;

        var status = response.StatusCode;
        var transient = status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
        var detail = await TryReadErrorAsync(response, ct);
        response.Dispose();
        throw new ProviderException($"WhatsApp provider returned {(int)status}{(detail is null ? "" : ": " + detail)}", transient);
    }

    private static async Task<string?> TryReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorBody>(Json, ct);
            return error?.Error is { Length: > 0 } text ? PiiRedactor.Redact(text) : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
        {
            return null;
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, ct) ?? throw new ProviderException("WhatsApp provider returned an empty body.");
        }
        catch (JsonException)
        {
            throw new ProviderException("WhatsApp provider returned an invalid body.");
        }
    }

    private sealed record QrBody(string? Qr);

    private sealed record StatusBody(SessionStatus Status);

    private sealed record ErrorBody(string? Error);
}
