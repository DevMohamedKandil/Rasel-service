using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rasel.Application.Ports;
using Rasel.Infrastructure.Messaging;
using Rasel.Infrastructure.Persistence;
using Rasel.Infrastructure.Providers;
using Rasel.Infrastructure.Security;

namespace Rasel.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddRaselInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var dbPath = config["Database:Path"] ?? "rasel.db";
        services.AddDbContext<RaselDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        services.AddScoped<IApiClientRepository, ApiClientRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<IMessageRepository, MessageRepository>();
        services.AddScoped<IOtpRepository, OtpRepository>();

        services.AddSingleton<FakeWhatsAppProvider>();
        services.AddSingleton<IWhatsAppProvider>(sp => sp.GetRequiredService<FakeWhatsAppProvider>());
        services.AddSingleton<FakeSmsProvider>();
        services.AddSingleton<ISmsProvider>(sp => sp.GetRequiredService<FakeSmsProvider>());

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ICodeGenerator, CsprngCodeGenerator>();
        services.AddSingleton<IOtpHasher>(_ => new HmacOtpHasher(Required(config, "Security:OtpPepper")));
        services.AddSingleton<ITokenIssuer>(sp => new HmacTokenIssuer(Required(config, "Security:TokenSecret"), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IRateLimiter, SlidingWindowRateLimiter>();

        services.AddSingleton(config.GetSection("Queue").Get<QueueOptions>() ?? new QueueOptions());
        services.AddSingleton<IMessageQueue, InMemoryMessageQueue>();
        services.AddHostedService<MessageQueueWorker>();

        services.AddSingleton(config.GetSection("Webhooks").Get<WebhookOptions>() ?? new WebhookOptions());
        services.AddHttpClient<IWebhookDispatcher, HttpWebhookDispatcher>(c => c.Timeout = TimeSpan.FromSeconds(5));
        return services;
    }

    private static string Required(IConfiguration config, string key) =>
        config[key] is { Length: > 0 } value ? value : throw new InvalidOperationException($"Missing configuration: {key}");
}
