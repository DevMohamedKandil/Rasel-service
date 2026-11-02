using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Rasel.Application.Ports;
using Rasel.Infrastructure.Configuration;

namespace Rasel.Infrastructure.Providers.WebJs;

public static class WebJsRegistration
{
    /// <summary>
    /// Registers the sidecar provider with a resilience pipeline (outermost first): retry of idempotent calls,
    /// circuit breaker over all calls, per-attempt timeout. Sends (POST) are deliberately never retried here:
    /// a timed-out send may already have been delivered, so the durable outbox decides about retries and the
    /// message id travels as Idempotency-Key for sidecar-side de-duplication.
    /// </summary>
    public static IServiceCollection AddWebJsProvider(this IServiceCollection services)
    {
        services.AddHttpClient<IWhatsAppProvider, WebJsWhatsAppProvider>(c => c.Timeout = Timeout.InfiniteTimeSpan)
            .AddResilienceHandler("webjs", (pipeline, context) =>
            {
                var o = context.ServiceProvider.GetRequiredService<IOptions<WhatsAppOptions>>().Value.WebJs;

                pipeline.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = o.ReadRetries,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = TimeSpan.FromMilliseconds(200),
                    ShouldHandle = args =>
                    {
                        var method = args.Context.GetRequestMessage()?.Method;
                        var idempotent = method == HttpMethod.Get || method == HttpMethod.Delete;
                        return new ValueTask<bool>(idempotent && HttpClientResiliencePredicates.IsTransient(args.Outcome));
                    },
                });

                pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    MinimumThroughput = o.BreakerMinimumThroughput,
                    FailureRatio = o.BreakerFailureRatio,
                    SamplingDuration = TimeSpan.FromSeconds(o.BreakerSamplingSeconds),
                    BreakDuration = TimeSpan.FromSeconds(o.BreakerBreakSeconds),
                });

                pipeline.AddTimeout(TimeSpan.FromSeconds(o.AttemptTimeoutSeconds));
            });
        return services;
    }
}
