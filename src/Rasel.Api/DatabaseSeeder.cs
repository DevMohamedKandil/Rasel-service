using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Domain.Entities;
using Rasel.Infrastructure.Persistence;

namespace Rasel.Api;

public static class DatabaseSeeder
{
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration config, ILogger logger)
    {
        using var scope = services.CreateScope();
        // EnsureCreated keeps the prototype simple; switch to EF migrations before real deployments.
        await scope.ServiceProvider.GetRequiredService<RaselDbContext>().Database.EnsureCreatedAsync();

        var clients = scope.ServiceProvider.GetRequiredService<IApiClientRepository>();
        if (await clients.AnyAsync(CancellationToken.None)) return;

        var configured = config["Seed:DevApiKey"];
        var key = string.IsNullOrWhiteSpace(configured) ? "rsl_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant() : configured;
        var limit = config.GetValue("Seed:DevDailyMessageLimit", 1000);
        await clients.AddAsync(ApiClient.Create("dev", ApiKeyHasher.Hash(key), limit), CancellationToken.None);

        if (string.IsNullOrWhiteSpace(configured))
            logger.LogWarning("Seeded dev client. API key (shown ONCE, store it now): {ApiKey}", key);
        else
            logger.LogInformation("Seeded dev client with the API key from configuration (Seed:DevApiKey).");
    }
}
