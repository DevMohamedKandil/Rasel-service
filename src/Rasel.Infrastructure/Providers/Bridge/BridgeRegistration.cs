using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using Rasel.Application.Ports;
using Rasel.Infrastructure.Configuration;

namespace Rasel.Infrastructure.Providers.Bridge;

public static class BridgeRegistration
{
    /// <summary>Retries only GET /Qr (pure read). Sends, including the GET /sendMessage send, are never retried here: the outbox owns that.</summary>
    public static IServiceCollection AddBridgeProvider(this IServiceCollection services)
    {
        services.AddHttpClient<IWhatsAppProvider, BridgeWhatsAppProvider>(c => c.Timeout = Timeout.InfiniteTimeSpan)
            .AddResilienceHandler("bridge", (pipeline, context) =>
            {
                var o = context.ServiceProvider.GetRequiredService<IOptions<WhatsAppOptions>>().Value.Bridge;

                if (o.ReadRetries > 0) pipeline.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = o.ReadRetries,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = TimeSpan.FromMilliseconds(200),
                    ShouldHandle = args =>
                    {
                        var request = args.Context.GetRequestMessage();
                        var pureRead = request?.Method == HttpMethod.Get
                            && request.RequestUri is { } uri && uri.AbsolutePath.Equals("/Qr", StringComparison.OrdinalIgnoreCase);
                        return new ValueTask<bool>(pureRead && HttpClientResiliencePredicates.IsTransient(args.Outcome));
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
