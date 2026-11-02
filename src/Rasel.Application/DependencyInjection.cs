using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Rasel.Application.Common;
using Rasel.Application.UseCases;

namespace Rasel.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddRaselApplication(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<OtpOptions>().Bind(config.GetSection("Otp")).ValidateOnStart();
        services.AddOptions<OutboxOptions>().Bind(config.GetSection("Outbox")).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<OtpOptions>, OtpOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<OutboxOptions>, OutboxOptionsValidator>());
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<OtpOptions>>().Value);
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<OutboxOptions>>().Value);

        services.AddScoped<CreateSessionHandler>();
        services.AddScoped<GetSessionStatusHandler>();
        services.AddScoped<GetSessionQrHandler>();
        services.AddScoped<StopSessionHandler>();
        services.AddScoped<SendTextHandler>();
        services.AddScoped<RequestOtpHandler>();
        services.AddScoped<VerifyOtpHandler>();
        services.AddScoped<MessageSender>();
        services.AddScoped<DailyLimitGuard>();
        return services;
    }
}
