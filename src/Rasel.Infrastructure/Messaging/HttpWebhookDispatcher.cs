using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Rasel.Application.Ports;
using Rasel.Infrastructure.Configuration;

namespace Rasel.Infrastructure.Messaging;

/// <summary>
/// Posts signed events. The signature covers "{timestamp}.{body}" so a captured request cannot be replayed
/// outside the receiver tolerance window.
/// </summary>
public sealed class HttpWebhookDispatcher(HttpClient http, WebhookOptions options, TimeProvider clock, ILogger<HttpWebhookDispatcher> logger)
    : IWebhookDispatcher
{
    public async Task DispatchAsync(Guid clientId, string eventName, object payload, CancellationToken ct)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.Url)) return;

        var now = clock.GetUtcNow();
        var body = JsonSerializer.SerializeToUtf8Bytes(new { @event = eventName, clientId, timestamp = now, data = payload });
        var timestamp = now.ToUnixTimeSeconds().ToString();
        var signed = Encoding.UTF8.GetBytes(timestamp + ".").Concat(body).ToArray();
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Secret), signed)).ToLowerInvariant();
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-Rasel-Timestamp", timestamp);
        request.Headers.Add("X-Rasel-Signature", $"sha256={signature}");

        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) logger.LogWarning("Webhook {Event} returned {Status}", eventName, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Webhook {Event} delivery failed", eventName);
        }
    }
}
