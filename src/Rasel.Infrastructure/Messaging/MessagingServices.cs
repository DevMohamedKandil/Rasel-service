using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rasel.Application.Ports;
using Rasel.Application.UseCases;

namespace Rasel.Infrastructure.Messaging;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class SlidingWindowRateLimiter(IClock clock) : IRateLimiter
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new();

    public bool TryAcquire(string key, int limit, TimeSpan window)
    {
        var now = clock.UtcNow;
        var hits = _hits.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (hits)
        {
            while (hits.Count > 0 && now - hits.Peek() >= window) hits.Dequeue();
            if (hits.Count >= limit) return false;
            hits.Enqueue(now);
            return true;
        }
    }
}

public sealed class InMemoryMessageQueue : IMessageQueue
{
    private readonly Channel<QueuedMessage> _channel = Channel.CreateUnbounded<QueuedMessage>();

    public ValueTask EnqueueAsync(QueuedMessage message, CancellationToken ct) => _channel.Writer.WriteAsync(message, ct);

    public ValueTask<QueuedMessage> DequeueAsync(CancellationToken ct) => _channel.Reader.ReadAsync(ct);
}

public sealed class QueueOptions
{
    public int MinDelayMs { get; set; } = 1000;
    public int MaxDelayMs { get; set; } = 3000;
}

public sealed class MessageQueueWorker(
    IMessageQueue queue, IServiceScopeFactory scopes, QueueOptions options, ILogger<MessageQueueWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var queued = await queue.DequeueAsync(stoppingToken);
                // A small random pause between sends makes traffic look less robotic to WhatsApp.
                if (options.MaxDelayMs > 0)
                    await Task.Delay(Random.Shared.Next(options.MinDelayMs, options.MaxDelayMs + 1), stoppingToken);

                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<MessageSender>().SendAsync(queued, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process a queued message");
            }
        }
    }
}

public sealed class WebhookOptions
{
    public bool Enabled { get; set; }
    public string Url { get; set; } = "";
    public string Secret { get; set; } = "";
}

public sealed class HttpWebhookDispatcher(HttpClient http, WebhookOptions options, IClock clock, ILogger<HttpWebhookDispatcher> logger)
    : IWebhookDispatcher
{
    public async Task DispatchAsync(Guid clientId, string eventName, object payload, CancellationToken ct)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.Url)) return;

        var body = JsonSerializer.SerializeToUtf8Bytes(new { @event = eventName, clientId, timestamp = clock.UtcNow, data = payload });
        var signature = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.Secret), body)).ToLowerInvariant();
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add("X-Rasel-Signature", $"sha256={signature}");

        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) logger.LogWarning("Webhook {Event} returned {Status}", eventName, (int)response.StatusCode);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Webhook {Event} delivery failed", eventName);
        }
    }
}
