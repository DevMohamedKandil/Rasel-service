using Microsoft.Extensions.DependencyInjection;
using Rasel.Application.Common;
using Rasel.Application.UseCases;

namespace Rasel.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddRaselApplication(this IServiceCollection services, OtpOptions otpOptions)
    {
        services.AddSingleton(otpOptions);
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
