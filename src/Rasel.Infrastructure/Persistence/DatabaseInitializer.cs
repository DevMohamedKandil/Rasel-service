using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rasel.Application.Common;
using Rasel.Application.Ports;
using Rasel.Domain.Entities;

namespace Rasel.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    /// <summary>Creates the schema and seeds the dev client when the database has none. Runs once at startup.</summary>
    public static async Task InitializeRaselAsync(this IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Rasel.Database");
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var env = scope.ServiceProvider.GetRequiredService<IHostEnvironment>();

        await scope.ServiceProvider.GetRequiredService<RaselDbContext>().Database.EnsureCreatedAsync(ct);

        var clients = scope.ServiceProvider.GetRequiredService<IApiClientRepository>();
        if (await clients.AnyAsync(ct)) return;

        var configured = config["Seed:DevApiKey"];
        var key = string.IsNullOrWhiteSpace(configured) ? "rsl_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant() : configured;
        var limit = config.GetValue("Seed:DevDailyMessageLimit", 1000);
        await clients.AddAsync(ApiClient.Create("dev", ApiKeyHasher.Hash(key), limit), ct);

        if (!string.IsNullOrWhiteSpace(configured))
            logger.LogInformation("Seeded dev client with the API key from configuration (Seed:DevApiKey).");
        else if (env.IsDevelopment())
            logger.LogWarning("Seeded dev client. API key (shown ONCE, store it now): {ApiKey}", key);
        else
            logger.LogWarning("Seeded dev client with a random API key that is NOT logged outside Development; set Seed__DevApiKey to choose one.");
    }
}
