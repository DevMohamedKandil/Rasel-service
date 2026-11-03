// Copy this file into your .NET backend. Needs only Microsoft.Extensions.Http (for AddHttpClient).
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Rasel.Client;

public sealed class RaselOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5114";
    /// <summary>Load from a secret store or environment variable, never from source control.</summary>
    public string ApiKey { get; set; } = "";
    public int MaxRetries { get; set; } = 3;
}

/// <summary>Raised for any non-2xx answer. Code is the stable problem+json "code" (for example otp_invalid).</summary>
public sealed class RaselException(HttpStatusCode status, string code, string detail, int? retryAfterSeconds)
    : Exception($"{(int)status} {code}: {detail}")
{
    public HttpStatusCode Status { get; } = status;
    public string Code { get; } = code;
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

public sealed record SessionDto(Guid Id, string Name, string Status);
public sealed record SendResult(Guid MessageId, string Status, bool Replayed);
public sealed record MessageStatus(Guid Id, string Status, int Attempts, DateTimeOffset CreatedAt, DateTimeOffset? SentAt, DateTimeOffset? NextAttemptAt, string? LastErrorCode);
public sealed record OtpChallenge(Guid ChallengeId, int ExpiresInSeconds, int ResendAfterSeconds);
public sealed record OtpToken(string Token, int ExpiresInSeconds);

public sealed class RaselClient(HttpClient http, RaselOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<SessionDto> CreateSessionAsync(string name, CancellationToken ct = default) =>
        Send<SessionDto>(HttpMethod.Post, "/v1/sessions", new { name }, null, ct);

    public Task<SessionDto> GetSessionAsync(Guid id, CancellationToken ct = default) =>
        Send<SessionDto>(HttpMethod.Get, $"/v1/sessions/{id}", null, null, ct);

    /// <summary>Always pass a stable idempotencyKey (for example "order-42-shipped") so a retry can never send twice.</summary>
    public Task<SendResult> SendTextAsync(string phone, string text, string idempotencyKey, Guid? sessionId = null, CancellationToken ct = default) =>
        Send<SendResult>(HttpMethod.Post, "/v1/messages", new { phone, text, sessionId }, idempotencyKey, ct);

    public Task<MessageStatus> GetMessageAsync(Guid id, CancellationToken ct = default) =>
        Send<MessageStatus>(HttpMethod.Get, $"/v1/messages/{id}", null, null, ct);

    /// <summary>Polls once per second until the message is Sent or Failed, or the timeout passes.</summary>
    public async Task<MessageStatus> WaitForFinalStatusAsync(Guid id, TimeSpan timeout, CancellationToken ct = default)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            var m = await GetMessageAsync(id, ct);
            if (m.Status is "Sent" or "Failed" || DateTimeOffset.UtcNow >= until) return m;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    public Task<OtpChallenge> RequestOtpAsync(string phone, string purpose, string language = "ar", CancellationToken ct = default) =>
        Send<OtpChallenge>(HttpMethod.Post, "/v1/otp/request", new { phone, purpose, channel = "whatsapp", language }, null, ct);

    public Task<OtpToken> VerifyOtpAsync(Guid challengeId, string code, CancellationToken ct = default) =>
        Send<OtpToken>(HttpMethod.Post, "/v1/otp/verify", new { challengeId, code }, null, ct);

    private async Task<T> Send<T>(HttpMethod method, string path, object? body, string? idempotencyKey, CancellationToken ct)
    {
        // Retry only when repeating is harmless: GET requests, or a POST that carries an Idempotency-Key.
        // 4xx answers (including 429 and 423) are decisions, not failures, so they are never retried here.
        var canRetry = method == HttpMethod.Get || idempotencyKey is not null;
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(method, options.BaseUrl.TrimEnd('/') + path);
            req.Headers.Add("X-Api-Key", options.ApiKey);
            if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
            if (body is not null) req.Content = JsonContent.Create(body, options: Json);
            try
            {
                using var res = await http.SendAsync(req, ct);
                if (res.IsSuccessStatusCode) return (await res.Content.ReadFromJsonAsync<T>(Json, ct))!;
                if ((int)res.StatusCode >= 500 && canRetry && attempt < options.MaxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
                    continue;
                }
                throw await ToException(res, ct);
            }
            catch (HttpRequestException) when (canRetry && attempt < options.MaxRetries)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), ct);
            }
        }
    }

    private static async Task<RaselException> ToException(HttpResponseMessage res, CancellationToken ct)
    {
        string code = "unknown", detail = res.ReasonPhrase ?? "";
        try
        {
            var p = await res.Content.ReadFromJsonAsync<Problem>(Json, ct);
            if (p is not null) (code, detail) = (p.Code ?? code, p.Detail ?? detail);
        }
        catch (JsonException) { }
        int? retry = res.Headers.RetryAfter?.Delta is { } d ? (int)d.TotalSeconds : null;
        return new RaselException(res.StatusCode, code, detail, retry);
    }

    private sealed record Problem([property: JsonPropertyName("code")] string? Code, string? Detail);
}

public static class RaselServiceCollectionExtensions
{
    /// <summary>services.AddRasel(o => { o.BaseUrl = cfg["Rasel:BaseUrl"]!; o.ApiKey = cfg["Rasel:ApiKey"]!; });</summary>
    public static IServiceCollection AddRasel(this IServiceCollection services, Action<RaselOptions> configure)
    {
        var options = new RaselOptions();
        configure(options);
        services.AddSingleton(options);
        services.AddHttpClient<RaselClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        return services;
    }
}
