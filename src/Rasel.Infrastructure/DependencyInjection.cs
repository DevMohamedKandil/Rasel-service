using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Rasel.Application.Ports;
using Rasel.Infrastructure.Configuration;
using Rasel.Infrastructure.Health;
using Rasel.Infrastructure.Messaging;
using Rasel.Infrastructure.Persistence;
using Rasel.Infrastructure.Providers;
using Rasel.Infrastructure.Providers.Bridge;
using Rasel.Infrastructure.Providers.WebJs;
using Rasel.Infrastructure.Security;
using Rasel.Infrastructure.Sidecar;

namespace Rasel.Infrastructure;

public static class DependencyInjection
{
    public const string ReadyTag = "ready";

    public static IServiceCollection AddRaselInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddValidatedOptions<DatabaseOptions, DatabaseOptionsValidator>(config, "Database");
        services.AddValidatedOptions<SecurityOptions, SecurityOptionsValidator>(config, "Security");
        services.AddValidatedOptions<WebhookOptions, WebhookOptionsValidator>(config, "Webhooks");
        services.AddValidatedOptions<WhatsAppOptions, WhatsAppOptionsValidator>(config, "WhatsApp");

        services.AddDbContext<RaselDbContext>((sp, o) =>
            o.UseSqlite($"Data Source={sp.GetRequiredService<IOptions<DatabaseOptions>>().Value.Path}"));
        services.AddScoped<IApiClientRepository, ApiClientRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<IMessageRepository, MessageRepository>();
        services.AddScoped<IOtpRepository, OtpRepository>();

        // Fake stays the default; the real sidecar is opt-in via WhatsApp:Provider=WebJs.
        var provider = config["WhatsApp:Provider"] ?? WhatsAppOptions.FakeProvider;
        if (provider.Equals(WhatsAppOptions.ManagedProvider, StringComparison.OrdinalIgnoreCase))
        {
            services.AddValidatedOptions<SidecarOptions, SidecarOptionsValidator>(config, "Sidecar");
            services.AddHttpClient("sidecar-probe");
            services.AddSingleton<IProcessLauncher, SystemProcessLauncher>();
            services.AddSingleton<ISidecarProbe, HttpSidecarProbe>();
            services.AddSingleton<SidecarFiles>();
            services.AddSingleton<SidecarSupervisor>();
            services.AddHostedService(sp => sp.GetRequiredService<SidecarSupervisor>());
            services.AddSingleton<ISidecarControl>(sp => sp.GetRequiredService<SidecarSupervisor>());
            services.AddSingleton<IBridgeEndpoint>(sp => new SupervisorBridgeEndpoint(sp.GetRequiredService<SidecarSupervisor>()));
            services.AddBridgeProvider();
            services.AddHealthChecks().AddCheck<SidecarHealthCheck>("sidecar", failureStatus: HealthStatus.Degraded, tags: [ReadyTag]);
        }
        else if (provider.Equals(WhatsAppOptions.BridgeProvider, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IBridgeEndpoint, OptionsBridgeEndpoint>();
            services.AddBridgeProvider();
        }
        else if (provider.Equals(WhatsAppOptions.WebJsProvider, StringComparison.OrdinalIgnoreCase))
            services.AddWebJsProvider();
        else
        {
            services.AddSingleton<FakeWhatsAppProvider>();
            services.AddSingleton<IWhatsAppProvider>(sp => sp.GetRequiredService<FakeWhatsAppProvider>());
        }

        services.TryAddSingleton<ISidecarControl, NullSidecarControl>();
        services.AddSingleton<FakeSmsProvider>();
        services.AddSingleton<ISmsProvider>(sp => sp.GetRequiredService<FakeSmsProvider>());

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ICodeGenerator, CsprngCodeGenerator>();
        services.AddSingleton<IOtpHasher>(sp => new HmacOtpHasher(sp.GetRequiredService<IOptions<SecurityOptions>>().Value.OtpPepper));
        services.AddSingleton<ITokenIssuer>(sp => new HmacTokenIssuer(
            sp.GetRequiredService<IOptions<SecurityOptions>>().Value.TokenSecret, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IRateLimiter, SlidingWindowRateLimiter>();

        services.AddHostedService<DatabaseInitializerService>(); // must stay before the worker
        services.AddHostedService<OutboxWorker>();

        services.AddSingleton(sp => sp.GetRequiredService<IOptions<WebhookOptions>>().Value);
        services.AddHttpClient<IWebhookDispatcher, HttpWebhookDispatcher>(c => c.Timeout = TimeSpan.FromSeconds(5));

        services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: [ReadyTag])
            .AddCheck<OutboxHealthCheck>("outbox", failureStatus: HealthStatus.Degraded, tags: [ReadyTag]);
        return services;
    }

    private static void AddValidatedOptions<TOptions, TValidator>(this IServiceCollection services, IConfiguration config, string section)
        where TOptions : class
        where TValidator : class, IValidateOptions<TOptions>
    {
        services.AddOptions<TOptions>().Bind(config.GetSection(section)).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<TOptions>, TValidator>());
    }
}
